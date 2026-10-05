using System.Net;
using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeArtifactDownloader
{
    private const int BufferSize = 81920;
    private const int DownloadAttemptCount = 3;
    private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly RegistryHttpClient _registryClient;
    private readonly ExecutionRuntimeStore _store;
    private readonly ExecutionRuntimeHostTrustService _trust;
    private readonly TimeProvider _timeProvider;
    private readonly string _temporaryRootPath;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private readonly bool _developmentAllowInsecureLoopbackRegistry;

    public ExecutionRuntimeArtifactDownloader(
        RegistryHttpClient registryClient,
        ExecutionRuntimeStore store,
        ExecutionRuntimeHostTrustService trust,
        TimeProvider timeProvider,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        bool developmentAllowInsecureLoopbackRegistry = false)
        : this(
            registryClient,
            store,
            trust,
            timeProvider,
            Path.Combine(Path.GetTempPath(), "Sunder.ExecutionRuntimes", "download"),
            telemetry,
            developmentAllowInsecureLoopbackRegistry)
    {
    }

    internal ExecutionRuntimeArtifactDownloader(
        RegistryHttpClient registryClient,
        ExecutionRuntimeStore store,
        ExecutionRuntimeHostTrustService trust,
        TimeProvider timeProvider,
        string temporaryRootPath,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        bool developmentAllowInsecureLoopbackRegistry = false)
    {
        _registryClient = registryClient ?? throw new ArgumentNullException(nameof(registryClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
        _developmentAllowInsecureLoopbackRegistry = developmentAllowInsecureLoopbackRegistry;
        _temporaryRootPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(temporaryRootPath)
                ? throw new ArgumentException("A dedicated temporary root is required.", nameof(temporaryRootPath))
                : temporaryRootPath);
    }

    public async Task<ExecutionRuntimeEnsureResult> DownloadAsync(
        Uri registryOrigin,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        IReadOnlyList<string> trustedArtifactOrigins,
        CancellationToken cancellationToken)
    {
        using var operation = _telemetry.BeginOperation();
        var origin = NormalizeRegistryOrigin(registryOrigin);
        var allowedOrigins = BuildAllowedOrigins(origin, trustedArtifactOrigins);
        var plan = CreatePlan(origin, artifact, _trust, _timeProvider.GetUtcNow());
        var artifactUri = ValidateArtifactUri(origin, artifact.DownloadUrl, allowedOrigins);
        cancellationToken.ThrowIfCancellationRequested();

        var downloadStarted = false;
        var result = await _store.EnsureArchiveAsync(
                plan,
                operationToken =>
                {
                    downloadStarted = true;
                    return DownloadOwnedArchiveWithTelemetryAsync(
                        artifactUri,
                        origin,
                        allowedOrigins,
                        artifact,
                        plan.Identity,
                        operationToken);
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!downloadStarted)
        {
            using var cachePhase = _telemetry.BeginPhase(
                ExecutionRuntimePhaseNames.Download,
                plan.Identity,
                bytes: 0,
                cacheHit: true);
            cachePhase.Succeeded();
        }
        return result;
    }

    private async Task<ExecutionRuntimeOwnedArchive> DownloadOwnedArchiveWithTelemetryAsync(
        Uri artifactUri,
        Uri registryOrigin,
        IReadOnlySet<string> allowedOrigins,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        using var phase = _telemetry.BeginPhase(
            ExecutionRuntimePhaseNames.Download,
            identity,
            bytes: 0,
            cacheHit: false);
        long bytesDownloaded = 0;
        try
        {
            var archive = await DownloadOwnedArchiveAsync(
                    artifactUri,
                    registryOrigin,
                    allowedOrigins,
                    artifact,
                    bytes =>
                    {
                        bytesDownloaded += bytes;
                        phase.SetBytes(bytesDownloaded);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            phase.Succeeded();
            return archive;
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    private async Task<ExecutionRuntimeOwnedArchive> DownloadOwnedArchiveAsync(
        Uri artifactUri,
        Uri registryOrigin,
        IReadOnlySet<string> allowedOrigins,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        Action<int> recordBytes,
        CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await DownloadOwnedArchiveAttemptAsync(
                        artifactUri,
                        registryOrigin,
                        allowedOrigins,
                        artifact,
                        recordBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientDownloadFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= DownloadAttemptCount)
                {
                    throw;
                }
            }

            await Task.Delay(DownloadRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            attempt++;
        }
    }

    private async Task<ExecutionRuntimeOwnedArchive> DownloadOwnedArchiveAttemptAsync(
        Uri artifactUri,
        Uri registryOrigin,
        IReadOnlySet<string> allowedOrigins,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        Action<int> recordBytes,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(_temporaryRootPath, Guid.NewGuid().ToString("N"));
        var temporaryArchivePath = Path.Combine(temporaryDirectory, "runtime.sunderruntime");
        var ownsTemporaryDirectory = true;
        try
        {
            Directory.CreateDirectory(_temporaryRootPath);
            Directory.CreateDirectory(temporaryDirectory);
            await DownloadArchiveAsync(
                    artifactUri,
                    registryOrigin,
                    allowedOrigins,
                    artifact,
                    temporaryArchivePath,
                    recordBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var ownedArchive = new ExecutionRuntimeOwnedArchive(
                temporaryArchivePath,
                () => TryDeleteDirectory(temporaryDirectory));
            ownsTemporaryDirectory = false;
            return ownedArchive;
        }
        finally
        {
            if (ownsTemporaryDirectory)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    private static bool IsTransientDownloadFailure(Exception exception)
        => exception switch
        {
            TimeoutException => true,
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: { } statusCode } => IsTransientStatusCode(statusCode),
            _ => false,
        };

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
           || (int)statusCode is >= 500 and <= 599;

    private async Task DownloadArchiveAsync(
        Uri artifactUri,
        Uri registryOrigin,
        IReadOnlySet<string> allowedOrigins,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        string temporaryArchivePath,
        Action<int> recordBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, artifactUri);
        using var response = await _registryClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ValidateArtifactUri(registryOrigin, response.RequestMessage?.RequestUri, allowedOrigins);
        await _registryClient.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is > SunderExecutionRuntimeFormat.MaxArchiveBytes)
        {
            throw TooLarge();
        }
        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != artifact.ArtifactSize)
        {
            throw InvalidArtifact("the HTTP content length does not match its declared artifact size");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            temporaryArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long bytesWritten = 0;
        while (bytesWritten < artifact.ArtifactSize)
        {
            var requested = (int)Math.Min(buffer.Length, artifact.ArtifactSize - bytesWritten);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw InvalidArtifact("the response ended before its declared artifact size");
            }

            recordBytes(read);
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesWritten += read;
        }

        var trailingBytes = await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (trailingBytes != 0)
        {
            recordBytes(trailingBytes);
            throw TooLarge();
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        var expectedHash = Convert.FromHexString(artifact.ArtifactSha256);
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
        {
            throw InvalidArtifact("the downloaded SHA-256 does not match its Registry descriptor");
        }
    }

    private static ExecutionRuntimeAcquisitionPlan CreatePlan(
        Uri registryOrigin,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        ExecutionRuntimeHostTrustService trust,
        DateTimeOffset now)
    {
        if (artifact is null || artifact.Identity is null || artifact.Lifecycle is null)
        {
            throw new InvalidDataException("Registry execution runtime artifact metadata is incomplete.");
        }

        var identity = artifact.Identity;
        var expectedModuleAbi = identity.RuntimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => SunderExecutionRuntimeFormat.DotNetModuleAbi,
            SunderExecutionRuntimeFormat.NodeRuntimeId => SunderExecutionRuntimeFormat.NodeModuleAbi,
            _ => null,
        };
        if (expectedModuleAbi is null
            || !string.Equals(identity.ModuleAbi, expectedModuleAbi, StringComparison.Ordinal)
            || !SemanticVersion.TryParse(identity.Version, out var version)
            || version.Prerelease is not null
            || identity.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision)
        {
            throw InvalidArtifact("the exact runtime identity, version, packaging revision, or module ABI is invalid");
        }

        var currentRid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        if (!string.Equals(identity.Rid, currentRid, StringComparison.Ordinal))
        {
            throw InvalidArtifact($"the exact RID does not match current Host RID '{currentRid}'");
        }
        if (artifact.ArtifactSize <= 0)
        {
            throw InvalidArtifact("the declared artifact size must be positive");
        }
        if (artifact.ArtifactSize > SunderExecutionRuntimeFormat.MaxArchiveBytes)
        {
            throw TooLarge();
        }
        if (!IsLowerSha256(artifact.ManifestSha256)
            || !IsLowerSha256(artifact.ArtifactSha256)
            || !IsLowerSha256(artifact.NativeAttestationSha256)
            || !IsLowerIdentifier(artifact.SigningKeyId)
            || !IsLowerIdentifier(artifact.NativeAttestationKeyId)
            || artifact.MinimumHostTrustGeneration <= 0
            || artifact.ObservedRuntimePolicyRevision <= 0)
        {
            throw InvalidArtifact("the Registry trust, digest, native-attestation, or policy evidence is invalid");
        }
        ValidateInstallationLifecycle(artifact, now);
        var trustEnvironment = trust.Classify(
            artifact.SigningKeyId,
            artifact.MinimumHostTrustGeneration);
        var acquisitionSource = trustEnvironment == ExecutionRuntimeTrustEnvironment.Development
            ? RuntimeExecutionRuntimeAcquisitionSources.Development
            : RuntimeExecutionRuntimeAcquisitionSources.Registry;

        return new ExecutionRuntimeAcquisitionPlan(
            new ExecutionRuntimeIdentity(
                identity.RuntimeId,
                identity.Version,
                identity.PackagingRevision,
                identity.Rid,
                identity.ModuleAbi),
            artifact.ArtifactSha256,
            artifact.ArtifactSize,
            artifact.ManifestSha256,
            ContentIdentity: null,
            artifact.SigningKeyId,
            artifact.MinimumHostTrustGeneration,
            trustEnvironment,
            acquisitionSource,
            registryOrigin.AbsoluteUri,
            artifact.NativeAttestationSha256,
            artifact.NativeAttestationKeyId);
    }

    private static void ValidateInstallationLifecycle(
        RegistryResolvedExecutionRuntimeArtifact artifact,
        DateTimeOffset now)
    {
        var lifecycle = artifact.Lifecycle;
        var supported = lifecycle.State == ExecutionRuntimeLifecycleStates.Supported
                        && lifecycle.SupportedFromUtc is not null
                        && lifecycle.DeprecatedAtUtc is null
                        && lifecycle.InstallBlockedAtUtc is null
                        && lifecycle.RevokedAtUtc is null;
        var deprecated = lifecycle.State == ExecutionRuntimeLifecycleStates.Deprecated
                         && lifecycle.SupportedFromUtc is not null
                         && lifecycle.DeprecatedAtUtc is not null
                         && lifecycle.RevokedAtUtc is null
                         && (lifecycle.InstallBlockedAtUtc is null || lifecycle.InstallBlockedAtUtc > now);
        if (lifecycle.StagedAtUtc == default || !supported && !deprecated)
        {
            throw InvalidArtifact("its lifecycle policy does not permit installation");
        }
    }

    private Uri NormalizeRegistryOrigin(Uri registryOrigin)
    {
        if (registryOrigin is null || !registryOrigin.IsAbsoluteUri)
        {
            throw new InvalidDataException("Execution runtime acquisition requires one absolute HTTPS Registry origin.");
        }

        try
        {
            var normalized = RegistryOrigin.Normalize(registryOrigin.AbsoluteUri);
            if (!IsAllowedRegistryOrigin(normalized))
            {
                throw new InvalidDataException("Execution runtime acquisition requires one absolute HTTPS Registry origin.");
            }
            return normalized;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Execution runtime acquisition requires one absolute HTTPS Registry origin.",
                exception);
        }
    }

    private IReadOnlySet<string> BuildAllowedOrigins(
        Uri registryOrigin,
        IReadOnlyList<string> advertisedOrigins)
    {
        if (advertisedOrigins is null)
        {
            throw new InvalidDataException("Registry trusted artifact origins are required.");
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeOrigin(registryOrigin),
        };
        foreach (var advertised in advertisedOrigins)
        {
            if (!Uri.TryCreate(advertised, UriKind.Absolute, out var candidate)
                || !IsOriginOnly(candidate)
                || candidate.Scheme != Uri.UriSchemeHttps
                   && (!_developmentAllowInsecureLoopbackRegistry
                       || candidate.Scheme != Uri.UriSchemeHttp
                       || !candidate.IsLoopback
                       || !string.Equals(
                           NormalizeOrigin(candidate),
                           NormalizeOrigin(registryOrigin),
                           StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Registry advertised an invalid trusted artifact origin.");
            }
            allowed.Add(NormalizeOrigin(candidate));
        }
        return allowed;
    }

    private Uri ValidateArtifactUri(
        Uri registryOrigin,
        string downloadUrl,
        IReadOnlySet<string> allowedOrigins)
    {
        Uri artifact;
        try
        {
            artifact = new Uri(registryOrigin, downloadUrl);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            throw new InvalidDataException("Registry execution runtime artifact URL is invalid.", exception);
        }
        return ValidateArtifactUri(registryOrigin, artifact, allowedOrigins);
    }

    private Uri ValidateArtifactUri(
        Uri registryOrigin,
        Uri? artifact,
        IReadOnlySet<string> allowedOrigins)
    {
        if (artifact is null
            || !artifact.IsAbsoluteUri
            || artifact.Scheme != Uri.UriSchemeHttps
               && (!_developmentAllowInsecureLoopbackRegistry
                   || artifact.Scheme != Uri.UriSchemeHttp
                   || !artifact.IsLoopback
                   || !string.Equals(
                       NormalizeOrigin(artifact),
                       NormalizeOrigin(registryOrigin),
                       StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(artifact.UserInfo)
            || registryOrigin.Scheme == Uri.UriSchemeHttps && artifact.Scheme != Uri.UriSchemeHttps
            || !allowedOrigins.Contains(NormalizeOrigin(artifact)))
        {
            throw new InvalidDataException(
                "Registry execution runtime artifact URL must remain on the Registry origin or an explicitly advertised trusted HTTPS artifact origin.");
        }
        return artifact;
    }

    private bool IsAllowedRegistryOrigin(Uri origin)
        => origin.Scheme == Uri.UriSchemeHttps
           || _developmentAllowInsecureLoopbackRegistry
           && origin.Scheme == Uri.UriSchemeHttp
           && origin.IsLoopback;

    private static bool IsOriginOnly(Uri origin)
        => origin.IsAbsoluteUri
           && string.IsNullOrEmpty(origin.UserInfo)
           && origin.AbsolutePath == "/"
           && string.IsNullOrEmpty(origin.Query)
           && string.IsNullOrEmpty(origin.Fragment);

    private static string NormalizeOrigin(Uri origin)
        => origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsLowerIdentifier(string? value)
        => !string.IsNullOrEmpty(value)
           && value.Length <= SunderExecutionRuntimeFormat.MaxKeyIdLength
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.Split('.').All(static segment => segment.Length > 0
                                                       && segment[0] != '-'
                                                       && segment[^1] != '-')
           && value.All(static character => character is >= 'a' and <= 'z'
                                                   or >= '0' and <= '9'
                                                   or '.'
                                                   or '-');

    private static InvalidDataException InvalidArtifact(string reason)
        => new($"Registry execution runtime artifact metadata is invalid: {reason}.");

    private static RegistryArtifactTooLargeException TooLarge()
        => new(
            $"Execution runtime artifact exceeds the {SunderExecutionRuntimeFormat.MaxArchiveBytes} byte download limit.");

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary download cleanup is best effort and never weakens store publication checks.
        }
    }
}
