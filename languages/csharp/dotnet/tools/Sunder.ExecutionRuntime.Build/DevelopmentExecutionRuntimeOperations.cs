using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;

namespace Sunder.ExecutionRuntime.Build;

internal static class DevelopmentExecutionRuntimeOperations
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<SunderExecutionRuntimeArchiveValidationResult> BuildAsync(
        string lockPath,
        string runtimeId,
        string version,
        string rid,
        string sourceArchivePath,
        string nativeAttestationPath,
        string outputPath,
        string? stateRoot,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(
            lockPath,
            ExecutionRuntimeLockPolicy.ProductionKind,
            cancellationToken);
        using var signingSession = DevelopmentExecutionRuntimeKeyStore.OpenSigningSession(stateRoot);
        var projectedDocument = ProjectTrust(document, signingSession.Trust);
        var entry = FindEntry(projectedDocument, runtimeId, version, rid);
        var workingRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build",
            "development-build",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingRoot);
            var signedAttestationPath = Path.Combine(workingRoot, "native-build-attestation.v1.json");
            await SignAttestationAsync(
                nativeAttestationPath,
                signedAttestationPath,
                signingSession,
                cancellationToken);
            return await ExecutionRuntimeArchiveBuilder.BuildAsync(
                projectedDocument,
                entry,
                sourceArchivePath,
                signedAttestationPath,
                outputPath,
                signingSession,
                verifier,
                TrustedKey(signingSession.Trust.ReleaseKey),
                TrustedKey(signingSession.Trust.NativeAttestationKey),
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    public static async Task<RegistryExecutionRuntimeLifecycleOperationResponse> PublishAsync(
        Uri registryBaseUri,
        string archiveDirectory,
        string? stateRoot,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        ValidateLoopbackRegistryUri(registryBaseUri);
        var directory = Path.GetFullPath(archiveDirectory);
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Development execution-runtime release input '{directory}' must be a non-link directory.");
        }
        var paths = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length != SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.Count
            || paths.Any(path => !File.Exists(path)
                                 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                                 || !path.EndsWith(SunderExecutionRuntimeFormat.Extension, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Development execution-runtime release input must contain exactly six non-link .sunderruntime files and no other entries.");
        }

        var trust = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);
        var artifacts = new List<InspectedArtifact>(paths.Length);
        var inspectionRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build",
            "development-publish",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(inspectionRoot);
            for (var index = 0; index < paths.Length; index++)
            {
                var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                    paths[index],
                    Path.Combine(inspectionRoot, $"validated-{index}.sunderruntime"),
                    Path.Combine(inspectionRoot, $"staging-{index}"),
                    verifier,
                    TrustedKey(trust.ReleaseKey),
                    verifier,
                    TrustedKey(trust.NativeAttestationKey),
                    cancellationToken);
                if (!result.Success
                    || !result.NativeAttestationSignatureVerified
                    || result.Manifest is null
                    || result.NativeAttestation is null)
                {
                    throw new InvalidDataException(
                        $"Development execution-runtime archive '{paths[index]}' failed trusted inspection: {string.Join(" ", result.Errors)}");
                }
                artifacts.Add(new InspectedArtifact(paths[index], result, result.NativeAttestation));
            }
        }
        finally
        {
            TryDeleteDirectory(inspectionRoot);
        }

        ValidateReleaseSet(artifacts, trust);
        var first = artifacts[0].Validation;
        var firstManifest = first.Manifest!;
        var release = new RegistryStageExecutionRuntimeReleaseRequest(
            firstManifest.Id!,
            firstManifest.Version!,
            firstManifest.PackagingRevision,
            firstManifest.ModuleAbi!,
            firstManifest.WorkerProtocol!,
            trust.Generation,
            trust.ReleaseKey.KeyId,
            artifacts
                .OrderBy(static artifact => artifact.Validation.Manifest!.Rid, StringComparer.Ordinal)
                .Select(static artifact => new RegistryExecutionRuntimeNativeAttestationExpectation(
                    artifact.Validation.Manifest!.Rid!,
                    artifact.NativeAttestation.Signing!.KeyId!))
                .ToArray(),
            SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers);
        var expectations = artifacts
            .OrderBy(static artifact => artifact.Validation.Manifest!.Rid, StringComparer.Ordinal)
            .Select(static artifact => new RegistryExecutionRuntimePublicationArtifactExpectation(
                artifact.Validation.Manifest!.Rid!,
                IdempotencyKey(artifact.Validation.ArchiveSha256!),
                artifact.Validation.ArchiveSha256!,
                artifact.Validation.ArchiveSize,
                artifact.Validation.ManifestSha256!,
                artifact.Validation.ContentIdentity!,
                artifact.Validation.Manifest.Signing!.KeyId!,
                artifact.Validation.Manifest.Provenance!.AttestationSha256!,
                artifact.NativeAttestation.Signing!.KeyId!))
            .ToArray();

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = registryBaseUri,
            Timeout = TimeSpan.FromMinutes(5),
        };
        var localArtifacts = artifacts
            .Select(static artifact => new DevelopmentExecutionRuntimeLocalArtifact(
                artifact.Validation.Manifest!.Rid!,
                artifact.Path,
                IdempotencyKey(artifact.Validation.ArchiveSha256!),
                artifact.Validation.ArchiveSha256!,
                artifact.Validation.ArchiveSize))
            .ToArray();
        return await PublishReleaseAsync(
            client,
            release,
            expectations,
            localArtifacts,
            cancellationToken);
    }

    internal static async Task<RegistryExecutionRuntimeLifecycleOperationResponse> PublishReleaseAsync(
        HttpClient client,
        RegistryStageExecutionRuntimeReleaseRequest release,
        IReadOnlyList<RegistryExecutionRuntimePublicationArtifactExpectation> expectations,
        IReadOnlyList<DevelopmentExecutionRuntimeLocalArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(artifacts);
        var expectedRids = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers
            .Order(StringComparer.Ordinal)
            .ToArray();
        var localRids = artifacts.Select(static artifact => artifact.Rid).Order(StringComparer.Ordinal).ToArray();
        if (!localRids.SequenceEqual(expectedRids, StringComparer.Ordinal)
            || artifacts.Select(static artifact => artifact.Rid).Distinct(StringComparer.Ordinal).Count()
            != expectedRids.Length)
        {
            throw new InvalidDataException(
                "Development execution-runtime publication requires one exact local artifact for every supported RID.");
        }
        var lookupRequest = new RegistryLookupExecutionRuntimePublicationRequest(release, expectations);
        var lookup = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
            client,
            HttpMethod.Post,
            RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            lookupRequest,
            emptyStatus: HttpStatusCode.NotFound,
            cancellationToken);
        var stage = lookup;
        if (stage is null)
        {
            stage = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
                client,
                HttpMethod.Post,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStages,
                release,
                emptyStatus: HttpStatusCode.Conflict,
                cancellationToken);
            if (stage is null)
            {
                stage = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
                    client,
                    HttpMethod.Post,
                    RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
                    lookupRequest,
                    emptyStatus: HttpStatusCode.NotFound,
                    cancellationToken);
            }
            if (stage is null)
            {
                throw new InvalidDataException(
                    "Development Registry did not create or recover the exact execution-runtime stage.");
            }
        }

        if (string.Equals(stage.Release.Lifecycle.State, ExecutionRuntimeLifecycleStates.Supported, StringComparison.Ordinal))
        {
            return AlreadySupported();
        }

        var byRid = artifacts.ToDictionary(
            static artifact => artifact.Rid,
            StringComparer.Ordinal);
        foreach (var rid in stage.MissingRids.Order(StringComparer.Ordinal))
        {
            if (!byRid.TryGetValue(rid, out var artifact))
            {
                throw new InvalidDataException(
                    $"Development Registry requested unexpected execution-runtime RID '{rid}'.");
            }
            var publication = new RegistryPublishLocalExecutionRuntimeArtifactRequest(
                artifact.Path,
                artifact.IdempotencyKey,
                artifact.ArtifactSha256,
                artifact.ArtifactSize);
            var publishedStage = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
                client,
                HttpMethod.Post,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageArtifactLocal(stage.StageId, rid),
                publication,
                emptyStatus: HttpStatusCode.Conflict,
                cancellationToken);
            if (publishedStage is not null)
            {
                stage = publishedStage;
                continue;
            }
            stage = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
                client,
                HttpMethod.Post,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
                lookupRequest,
                emptyStatus: HttpStatusCode.NotFound,
                cancellationToken)
                ?? throw new InvalidDataException(
                    $"Development Registry lost the exact release after a publication conflict for RID '{rid}'.");
            if (string.Equals(stage.Release.Lifecycle.State, ExecutionRuntimeLifecycleStates.Supported, StringComparison.Ordinal))
            {
                return AlreadySupported();
            }
            if (stage.MissingRids.Contains(rid, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Development Registry did not reconcile the publication conflict for RID '{rid}'.");
            }
        }

        stage = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
            client,
            HttpMethod.Post,
            RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            lookupRequest,
            emptyStatus: HttpStatusCode.NotFound,
            cancellationToken)
            ?? throw new InvalidDataException(
                "Development Registry lost the exact execution-runtime stage before promotion.");
        if (string.Equals(stage.Release.Lifecycle.State, ExecutionRuntimeLifecycleStates.Supported, StringComparison.Ordinal))
        {
            return AlreadySupported();
        }
        if (!stage.ReadyToPromote || stage.MissingRids.Count > 0)
        {
            throw new InvalidDataException(
                "Development Registry did not retain the exact complete six-RID release before promotion.");
        }
        var promotion = await SendAsync<RegistryExecutionRuntimeLifecycleOperationResponse>(
            client,
            HttpMethod.Post,
            RegistryApiRoutes.DevelopmentExecutionRuntimeStagePromotion(stage.StageId),
            body: null,
            emptyStatus: HttpStatusCode.Conflict,
            cancellationToken);
        if (promotion is not null)
        {
            return promotion;
        }
        stage = await SendAsync<RegistryStagedExecutionRuntimePublicationResponse>(
            client,
            HttpMethod.Post,
            RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            lookupRequest,
            emptyStatus: HttpStatusCode.NotFound,
            cancellationToken)
            ?? throw new InvalidDataException(
                "Development Registry lost the exact release after a promotion conflict.");
        if (string.Equals(stage.Release.Lifecycle.State, ExecutionRuntimeLifecycleStates.Supported, StringComparison.Ordinal))
        {
            return AlreadySupported();
        }
        throw new InvalidDataException(
            "Development Registry rejected promotion without finalizing the exact expected release.");
    }

    private static ExecutionRuntimeLockDocument ProjectTrust(
        ExecutionRuntimeLockDocument source,
        DevelopmentExecutionRuntimeTrust trust)
        => source with
        {
            Entries = source.Entries!
                .Select(entry => entry is null
                    ? null
                    : entry with
                    {
                        MinimumHostTrustGeneration = trust.Generation,
                        Trust = entry.Trust! with
                        {
                            ReleaseSigningKeyId = trust.ReleaseKey.KeyId,
                            AttestationSigningKeyId = trust.NativeAttestationKey.KeyId,
                        },
                    })
                .ToArray(),
        };

    private static ExecutionRuntimeLockEntry FindEntry(
        ExecutionRuntimeLockDocument document,
        string runtimeId,
        string version,
        string rid)
    {
        var entries = document.Entries!
            .Where(entry => entry is not null
                            && string.Equals(entry.Id, runtimeId, StringComparison.Ordinal)
                            && string.Equals(entry.Version, version, StringComparison.Ordinal)
                            && string.Equals(entry.Rid, rid, StringComparison.Ordinal))
            .ToArray();
        return entries.Length == 1
            ? entries[0]!
            : throw new InvalidDataException(
                $"Lock must contain exactly one entry for '{runtimeId}' version '{version}' RID '{rid}'.");
    }

    private static async Task SignAttestationAsync(
        string inputPath,
        string outputPath,
        DevelopmentExecutionRuntimeSigningSession signingSession,
        CancellationToken cancellationToken)
    {
        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath)
            || (File.GetAttributes(fullInputPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Development native attestation '{fullInputPath}' must be a non-link file.");
        }
        var observed = new FileInfo(fullInputPath);
        if (observed.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
        {
            throw new InvalidDataException(
                $"Development native attestation must contain at most {SunderExecutionRuntimeFormat.MaxMetadataBytes} bytes.");
        }
        var length = observed.Length;
        var writeTime = observed.LastWriteTimeUtc;
        var bytes = await File.ReadAllBytesAsync(fullInputPath, cancellationToken);
        observed.Refresh();
        if ((File.GetAttributes(fullInputPath) & FileAttributes.ReparsePoint) != 0
            || observed.Length != length
            || observed.LastWriteTimeUtc != writeTime)
        {
            throw new InvalidDataException("Development native attestation changed while being read.");
        }

        var attestation = StrictJson.Deserialize<SunderExecutionRuntimeNativeAttestation>(
            bytes,
            "Development native attestation");
        attestation = attestation with
        {
            Signing = new SunderExecutionRuntimeAttestationSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = signingSession.Trust.NativeAttestationKey.KeyId,
                Signature = null,
            },
        };
        attestation = attestation with
        {
            Signing = attestation.Signing with
            {
                Signature = Convert.ToBase64String(signingSession.SignNativeAttestation(
                    SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation))),
            },
        };
        await File.WriteAllBytesAsync(
            outputPath,
            SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation),
            cancellationToken);
    }

    private static void ValidateReleaseSet(
        IReadOnlyList<InspectedArtifact> artifacts,
        DevelopmentExecutionRuntimeTrust trust)
    {
        var first = artifacts[0].Validation.Manifest!;
        var expectedRids = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualRids = artifacts.Select(static artifact => artifact.Validation.Manifest!.Rid!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualRids.SequenceEqual(expectedRids, StringComparer.Ordinal)
            || artifacts.Any(artifact =>
                !string.Equals(artifact.Validation.Manifest!.Id, first.Id, StringComparison.Ordinal)
                || !string.Equals(artifact.Validation.Manifest.Version, first.Version, StringComparison.Ordinal)
                || artifact.Validation.Manifest.PackagingRevision != first.PackagingRevision
                || !string.Equals(artifact.Validation.Manifest.ModuleAbi, first.ModuleAbi, StringComparison.Ordinal)
                || !string.Equals(artifact.Validation.Manifest.WorkerProtocol, first.WorkerProtocol, StringComparison.Ordinal)
                || !string.Equals(artifact.Validation.Manifest.Signing!.KeyId, trust.ReleaseKey.KeyId, StringComparison.Ordinal)
                || !string.Equals(
                    artifact.NativeAttestation.Signing!.KeyId,
                    trust.NativeAttestationKey.KeyId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Development execution-runtime release input is partial, mixed, duplicated, or not signed by the current Development trust generation.");
        }
    }

    private static SunderExecutionRuntimeTrustedKey TrustedKey(DevelopmentExecutionRuntimePublicKey key)
    {
        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(key.PublicKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Development public key '{key.KeyId}' is not valid base64.", exception);
        }
        if (publicKey.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant(),
                key.PublicKeySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Development public key '{key.KeyId}' has inconsistent metadata.");
        }
        return new SunderExecutionRuntimeTrustedKey(key.KeyId, publicKey);
    }

    private static void ValidateLoopbackRegistryUri(Uri registryBaseUri)
    {
        ArgumentNullException.ThrowIfNull(registryBaseUri);
        if (!registryBaseUri.IsAbsoluteUri
            || registryBaseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(registryBaseUri.UserInfo)
            || registryBaseUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(registryBaseUri.Query)
            || !string.IsNullOrEmpty(registryBaseUri.Fragment))
        {
            throw new InvalidDataException(
                "Development Registry URL must be an absolute loopback HTTP(S) origin with no credentials, path, query, or fragment.");
        }
        if (!IPAddress.TryParse(registryBaseUri.DnsSafeHost, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new InvalidDataException("Development Registry URL host must be a loopback IP address.");
        }
    }

    private static async Task<T?> SendAsync<T>(
        HttpClient client,
        HttpMethod method,
        string route,
        object? body,
        HttpStatusCode? emptyStatus,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: WebJsonOptions);
        }
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (emptyStatus is { } expectedEmptyStatus && response.StatusCode == expectedEmptyStatus)
        {
            return default;
        }
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadBoundedResponseBodyAsync(response, cancellationToken);
            throw new InvalidDataException(
                $"Development Registry request '{route}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}");
        }
        try
        {
            await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidDataException(
                $"Development Registry response for '{route}' exceeded 65536 bytes.",
                exception);
        }
        return await response.Content.ReadFromJsonAsync<T>(WebJsonOptions, cancellationToken)
               ?? throw new InvalidDataException(
                   $"Development Registry request '{route}' returned an empty or invalid JSON response.");
    }

    private static string IdempotencyKey(string archiveSha256) => "development-v1:" + archiveSha256;

    private static async Task<string> ReadBoundedResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return "Response body exceeded 65536 bytes.";
        }
    }

    private static RegistryExecutionRuntimeLifecycleOperationResponse AlreadySupported()
        => new(true, null, RuntimePolicyRevision: 0, Errors: []);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record InspectedArtifact(
        string Path,
        SunderExecutionRuntimeArchiveValidationResult Validation,
        SunderExecutionRuntimeNativeAttestation NativeAttestation);

    internal sealed record DevelopmentExecutionRuntimeLocalArtifact(
        string Rid,
        string Path,
        string IdempotencyKey,
        string ArtifactSha256,
        long ArtifactSize);
}
