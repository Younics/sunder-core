using System.Text;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class ExecutionRuntimeTrustDispositions
{
    public const string Trusted = "trusted";
    public const string Denied = "denied";
}

internal sealed record ExecutionRuntimeLifecyclePolicyObservation(
    ExecutionRuntimeReceiptV1 Receipt,
    string RegistryOrigin,
    RegistryExecutionRuntimeLifecycle Lifecycle,
    long RuntimePolicyRevision,
    long MinimumHostTrustGeneration,
    DateTimeOffset PolicyObservedAtUtc,
    string TrustDisposition = ExecutionRuntimeTrustDispositions.Trusted);

internal sealed record ExecutionRuntimeLifecyclePolicyEntryV1(
    ExecutionRuntimeIdentity Identity,
    string RegistryOrigin,
    string WorkerProtocol,
    string ManifestSha256,
    string ArtifactSha256,
    long ArtifactSize,
    string ContentIdentity,
    string SigningKeyId,
    long MinimumHostTrustGeneration,
    string NativeAttestationSha256,
    string NativeAttestationKeyId,
    RegistryExecutionRuntimeLifecycle Lifecycle,
    long RuntimePolicyRevision,
    DateTimeOffset PolicyObservedAtUtc,
    [property: System.Text.Json.Serialization.JsonRequired]
    string TrustDisposition = ExecutionRuntimeTrustDispositions.Trusted)
{
    public bool MatchesReceipt(ExecutionRuntimeReceiptV1 receipt)
        => receipt.Identity == Identity
           && string.Equals(receipt.WorkerProtocol, WorkerProtocol, StringComparison.Ordinal)
           && string.Equals(receipt.ManifestSha256, ManifestSha256, StringComparison.Ordinal)
           && string.Equals(receipt.ArtifactSha256, ArtifactSha256, StringComparison.Ordinal)
           && receipt.ArtifactSize == ArtifactSize
           && string.Equals(receipt.ContentIdentity, ContentIdentity, StringComparison.Ordinal)
           && string.Equals(receipt.SigningKeyId, SigningKeyId, StringComparison.Ordinal)
           && string.Equals(
               receipt.NativeAttestationSha256,
               NativeAttestationSha256,
               StringComparison.Ordinal)
            && string.Equals(
                receipt.NativeAttestationKeyId,
                NativeAttestationKeyId,
                StringComparison.Ordinal)
           && receipt.TrustGeneration >= MinimumHostTrustGeneration
           && MatchesTrustAndProvenance(receipt);

    private bool MatchesTrustAndProvenance(ExecutionRuntimeReceiptV1 receipt)
        => receipt.TrustEnvironment switch
        {
            ExecutionRuntimeTrustEnvironment.Production =>
                receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                    ? receipt.RegistryOrigin is null
                    : receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Registry
                      && string.Equals(receipt.RegistryOrigin, RegistryOrigin, StringComparison.Ordinal),
            ExecutionRuntimeTrustEnvironment.Development =>
                receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development
                && string.Equals(receipt.RegistryOrigin, RegistryOrigin, StringComparison.Ordinal),
            _ => false,
        };

    public bool AuthorizesCollection
        => TrustDisposition == ExecutionRuntimeTrustDispositions.Denied
           || Lifecycle.State is ExecutionRuntimeLifecycleStates.Deprecated
            or ExecutionRuntimeLifecycleStates.Retired
            or ExecutionRuntimeLifecycleStates.Revoked;

    public bool PermitsActivation(DateTimeOffset now)
        => TrustDisposition == ExecutionRuntimeTrustDispositions.Trusted
           && (Lifecycle.ActivationBlockedAtUtc is null || Lifecycle.ActivationBlockedAtUtc > now)
           && Lifecycle.State is ExecutionRuntimeLifecycleStates.Supported
               or ExecutionRuntimeLifecycleStates.Deprecated
                or ExecutionRuntimeLifecycleStates.Retired;

    public bool PermitsInstallation(DateTimeOffset now)
        => TrustDisposition == ExecutionRuntimeTrustDispositions.Trusted
           && (Lifecycle.State == ExecutionRuntimeLifecycleStates.Supported
               || Lifecycle.State == ExecutionRuntimeLifecycleStates.Deprecated
               && (Lifecycle.InstallBlockedAtUtc is null || Lifecycle.InstallBlockedAtUtc > now));
}

internal sealed record PreparedExecutionRuntimeLifecyclePolicyUpdate(
    IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> Entries,
    ExecutionRuntimeLifecyclePolicySnapshot Authority);

internal sealed class ExecutionRuntimeLifecyclePolicySnapshot
{
    private readonly IReadOnlyDictionary<string, ExecutionRuntimeLifecyclePolicyEntryV1> _byIdentity;

    public ExecutionRuntimeLifecyclePolicySnapshot(
        long generation,
        IEnumerable<ExecutionRuntimeLifecyclePolicyEntryV1> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Generation = generation;
        Entries = Array.AsReadOnly(entries.ToArray());
        _byIdentity = Entries.ToDictionary(
            static entry => entry.Identity.SingleFlightKey,
            StringComparer.Ordinal);
    }

    public long Generation { get; }

    public IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> Entries { get; }

    public bool AuthorizesCollection(ExecutionRuntimeReceiptV1 receipt)
        => _byIdentity.TryGetValue(receipt.Identity.SingleFlightKey, out var policy)
           && policy.MatchesReceipt(receipt)
           && policy.AuthorizesCollection;

    public bool TryGet(
        ExecutionRuntimeIdentity identity,
        out ExecutionRuntimeLifecyclePolicyEntryV1? policy)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _byIdentity.TryGetValue(identity.SingleFlightKey, out policy);
    }
}

internal sealed record ExecutionRuntimeLifecyclePolicyFileV1(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> Policies)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed class ExecutionRuntimeLifecyclePolicyCache(
    ExecutionRuntimeStoreLayout layout,
    TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private ExecutionRuntimeLifecyclePolicySnapshot? _snapshot;
    private Exception? _persistenceFailure;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _snapshot) is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is not null)
            {
                return;
            }

            Directory.CreateDirectory(layout.PolicyRootPath);
            PackageStorePathGuard.EnsureSafe(layout.PolicyRootPath, layout.PolicyRootPath);
            var paths = Directory.EnumerateFiles(layout.PolicyRootPath, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
            foreach (var path in paths)
            {
                PackageStorePathGuard.EnsureSafe(path, layout.PolicyRootPath);
                var name = Path.GetFileName(path);
                if (!string.Equals(path, layout.LifecyclePolicyPath, PathComparison)
                    && !name.StartsWith("lifecycle.json.tmp-", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Execution-runtime lifecycle policy path '{path}' is not recognized.");
                }
            }

            var file = File.Exists(layout.LifecyclePolicyPath)
                ? await ReadAsync(layout.LifecyclePolicyPath, cancellationToken).ConfigureAwait(false)
                : new ExecutionRuntimeLifecyclePolicyFileV1(
                    ExecutionRuntimeLifecyclePolicyFileV1.CurrentSchemaVersion,
                    Generation: 0,
                    Policies: []);
            ValidateFile(file);
            _snapshot = new ExecutionRuntimeLifecyclePolicySnapshot(file.Generation, file.Policies);

            foreach (var temporaryPath in paths.Where(path =>
                         Path.GetFileName(path).StartsWith("lifecycle.json.tmp-", StringComparison.Ordinal)))
            {
                ExecutionRuntimeStore.TryDeleteFile(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ExecutionRuntimeLifecyclePolicySnapshot CaptureSnapshot()
    {
        if (Volatile.Read(ref _persistenceFailure) is { } persistenceFailure)
        {
            throw new InvalidDataException(
                "Execution-runtime lifecycle policy persistence previously failed.",
                persistenceFailure);
        }
        return Volatile.Read(ref _snapshot)
               ?? throw new InvalidOperationException(
                   "The execution-runtime lifecycle policy cache is not initialized.");
    }

    public async Task<TResult> ExecuteUnderGenerationFenceAsync<TResult>(
        long expectedGeneration,
        IReadOnlyList<ExecutionRuntimeIdentity> installationIdentities,
        IReadOnlyList<ExecutionRuntimeIdentity> activationIdentities,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        if (expectedGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        }
        ArgumentNullException.ThrowIfNull(installationIdentities);
        ArgumentNullException.ThrowIfNull(activationIdentities);
        if (installationIdentities.Count == 0 && activationIdentities.Count == 0)
        {
            throw new ArgumentException(
                "At least one exact execution-runtime policy identity is required.",
                nameof(installationIdentities));
        }
        ArgumentNullException.ThrowIfNull(operation);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = CaptureSnapshot();
            if (current.Generation != expectedGeneration)
            {
                throw new InvalidDataException(
                    $"{ExecutionRuntimeErrorCodes.PolicyRequired}: cached execution-runtime policy changed after local binding resolution.");
            }
            var now = _timeProvider.GetUtcNow();
            if (installationIdentities.Any(identity =>
                    !current.TryGet(identity, out var policy)
                    || policy is null
                    || !policy.PermitsInstallation(now)
                    || !policy.PermitsActivation(now))
                || activationIdentities.Any(identity =>
                    !current.TryGet(identity, out var policy)
                    || policy is null
                    || !policy.PermitsActivation(now)))
            {
                throw new InvalidDataException(
                    $"{ExecutionRuntimeErrorCodes.PolicyRequired}: cached execution-runtime policy no longer permits local package installation and activation.");
            }
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ObserveAsync(
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ApplyEntriesAsync(observations.Select(CreateEntry).ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PreparedExecutionRuntimeLifecyclePolicyUpdate> PrepareUpdateAsync(
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var candidates = observations.Select(CreateEntry).ToArray();
        return await PrepareEntriesAsync(candidates, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PreparedExecutionRuntimeLifecyclePolicyUpdate> PrepareEntriesAsync(
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            ValidateEntry(entry);
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_persistenceFailure is not null)
            {
                throw new InvalidDataException(
                    "Execution-runtime lifecycle policy persistence previously failed.",
                    _persistenceFailure);
            }

            var current = CaptureSnapshot();
            var (authority, _) = Merge(current, entries);
            ValidatePolicyDocument(authority);
            return new PreparedExecutionRuntimeLifecyclePolicyUpdate(
                Array.AsReadOnly(entries.ToArray()),
                authority);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ValidatePreparedUpdatePersistenceAsync(
        PreparedExecutionRuntimeLifecyclePolicyUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_persistenceFailure is not null)
            {
                throw new InvalidDataException(
                    "Execution-runtime lifecycle policy persistence previously failed.",
                    _persistenceFailure);
            }

            var (authority, changed) = Merge(CaptureSnapshot(), update.Entries);
            ValidatePolicyDocument(authority);
            if (authority.Generation != update.Authority.Generation
                || !authority.Entries.SequenceEqual(update.Authority.Entries))
            {
                throw new InvalidDataException(
                    "Prepared execution-runtime lifecycle policy authority became stale.");
            }
            if (!changed)
            {
                return;
            }
            if (Directory.Exists(layout.LifecyclePolicyPath))
            {
                throw new InvalidDataException(
                    "Execution-runtime lifecycle policy persistence path is not a file.");
            }
            if (File.Exists(layout.LifecyclePolicyPath))
            {
                var existing = new FileInfo(layout.LifecyclePolicyPath);
                if (existing.LinkTarget is not null
                    || (existing.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Execution-runtime lifecycle policy persistence path is linked.");
                }
            }

            var probePath = $"{layout.LifecyclePolicyPath}.tmp-{Guid.NewGuid():N}";
            PackageStorePathGuard.EnsureSafe(probePath, layout.PolicyRootPath);
            try
            {
                await DurableJsonDocument.WriteAsync(
                        probePath,
                        CreatePolicyDocument(authority),
                        ExecutionRuntimeStore.JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    "Execution-runtime lifecycle policy persistence is unavailable.",
                    exception);
            }
            finally
            {
                ExecutionRuntimeStore.TryDeleteFile(probePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ApplyEntriesAsync(
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            ValidateEntry(entry);
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_persistenceFailure is not null)
            {
                throw new InvalidDataException(
                    "Execution-runtime lifecycle policy persistence previously failed.",
                    _persistenceFailure);
            }

            var current = CaptureSnapshot();
            var (authority, changed) = Merge(current, entries);
            if (!changed)
            {
                return;
            }

            var next = CreatePolicyDocument(authority);
            ValidatePolicyDocument(authority);
            try
            {
                await DurableJsonDocument.WriteAsync(
                        layout.LifecyclePolicyPath,
                        next,
                        ExecutionRuntimeStore.JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _persistenceFailure, exception);
                throw;
            }
            Volatile.Write(ref _snapshot, authority);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ExecutionRuntimeLifecyclePolicyFileV1 CreatePolicyDocument(
        ExecutionRuntimeLifecyclePolicySnapshot authority)
        => new(
            ExecutionRuntimeLifecyclePolicyFileV1.CurrentSchemaVersion,
            authority.Generation,
            authority.Entries);

    private static void ValidatePolicyDocument(ExecutionRuntimeLifecyclePolicySnapshot authority)
    {
        var document = CreatePolicyDocument(authority);
        ValidateFile(document);
        if (JsonSerializer.SerializeToUtf8Bytes(document, ExecutionRuntimeStore.JsonOptions).Length
            + Encoding.UTF8.GetByteCount(Environment.NewLine)
            > SunderExecutionRuntimeFormat.MaxMetadataBytes)
        {
            throw new InvalidDataException(
                "The execution-runtime lifecycle policy document exceeds its durable metadata limit.");
        }
    }

    private static (ExecutionRuntimeLifecyclePolicySnapshot Authority, bool Changed) Merge(
        ExecutionRuntimeLifecyclePolicySnapshot current,
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> candidates)
    {
        var nextByIdentity = current.Entries.ToDictionary(
            static entry => entry.Identity.SingleFlightKey,
            StringComparer.Ordinal);
        var minimumRevisionByOrigin = current.Entries
            .GroupBy(static entry => entry.RegistryOrigin, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(static entry => entry.RuntimePolicyRevision),
                StringComparer.Ordinal);
        foreach (var originCandidates in candidates.GroupBy(
                     static candidate => candidate.RegistryOrigin,
                     StringComparer.Ordinal))
        {
            var revisions = originCandidates
                .Select(static candidate => candidate.RuntimePolicyRevision)
                .Distinct()
                .ToArray();
            if (revisions.Length != 1)
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle observations from '{originCandidates.Key}' do not form one authoritative revision snapshot.");
            }
            if (minimumRevisionByOrigin.TryGetValue(originCandidates.Key, out var minimumRevision)
                && revisions[0] < minimumRevision)
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle policy from '{originCandidates.Key}' moved below its observed revision watermark.");
            }
        }

        var changed = false;
        foreach (var candidate in candidates)
        {
            var key = candidate.Identity.SingleFlightKey;
            if (!nextByIdentity.TryGetValue(key, out var existing))
            {
                nextByIdentity.Add(key, candidate);
                changed = true;
                continue;
            }
            if (candidate.RuntimePolicyRevision < existing.RuntimePolicyRevision)
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle policy for '{key}' moved to an older revision.");
            }
            if (candidate.RuntimePolicyRevision == existing.RuntimePolicyRevision)
            {
                if (!SamePolicy(existing, candidate))
                {
                    throw new InvalidDataException(
                        $"Execution-runtime lifecycle policy for '{key}' conflicts at the same revision.");
                }
                continue;
            }
            if (!SameImmutableSubject(existing, candidate))
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle policy for '{key}' changed immutable evidence.");
            }
            if (!IsForwardTransition(existing.Lifecycle.State, candidate.Lifecycle.State))
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle policy for '{key}' moved backwards.");
            }
            if (BroadensLifecyclePermission(existing.Lifecycle, candidate.Lifecycle))
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle policy for '{key}' broadened an existing deadline.");
            }
            if (existing.TrustDisposition == ExecutionRuntimeTrustDispositions.Denied
                && candidate.TrustDisposition != ExecutionRuntimeTrustDispositions.Denied)
            {
                throw new InvalidDataException(
                    $"Execution-runtime lifecycle policy for '{key}' broadened a denied trust disposition.");
            }
            nextByIdentity[key] = candidate;
            changed = true;
        }

        var nextEntries = nextByIdentity.Values
            .OrderBy(static entry => entry.Identity.SingleFlightKey, StringComparer.Ordinal)
            .ToArray();
        return (
            new ExecutionRuntimeLifecyclePolicySnapshot(
                changed ? checked(current.Generation + 1) : current.Generation,
                nextEntries),
            changed);
    }

    private static ExecutionRuntimeLifecyclePolicyEntryV1 CreateEntry(
        ExecutionRuntimeLifecyclePolicyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Receipt);
        ArgumentNullException.ThrowIfNull(observation.Lifecycle);
        var hasProductionProvenance = observation.Receipt.TrustEnvironment
                                      == ExecutionRuntimeTrustEnvironment.Production
                                      && observation.Receipt.AcquisitionSource is (
                                          RuntimeExecutionRuntimeAcquisitionSources.Registry
                                          or RuntimeExecutionRuntimeAcquisitionSources.LocalImport);
        var hasDevelopmentProvenance = observation.Receipt.TrustEnvironment
                                       == ExecutionRuntimeTrustEnvironment.Development
                                       && observation.Receipt.AcquisitionSource
                                       == RuntimeExecutionRuntimeAcquisitionSources.Development;
        if (!hasProductionProvenance && !hasDevelopmentProvenance)
        {
            throw new InvalidDataException(
                "Execution-runtime receipt trust and acquisition provenance are not correctly paired.");
        }
        var origin = NormalizeOrigin(observation.RegistryOrigin);
        if (observation.Receipt.AcquisitionSource is (
                RuntimeExecutionRuntimeAcquisitionSources.Registry
                or RuntimeExecutionRuntimeAcquisitionSources.Development)
            && !string.Equals(observation.Receipt.RegistryOrigin, origin, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Execution-runtime lifecycle policy origin does not match its immutable receipt.");
        }
        if (observation.Receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
            && observation.Receipt.RegistryOrigin is not null)
        {
            throw new InvalidDataException(
                "A local execution-runtime import cannot carry immutable Registry provenance.");
        }
        var entry = new ExecutionRuntimeLifecyclePolicyEntryV1(
            observation.Receipt.Identity,
            origin,
            observation.Receipt.WorkerProtocol,
            observation.Receipt.ManifestSha256,
            observation.Receipt.ArtifactSha256,
            observation.Receipt.ArtifactSize,
            observation.Receipt.ContentIdentity,
            observation.Receipt.SigningKeyId,
            observation.MinimumHostTrustGeneration,
            observation.Receipt.NativeAttestationSha256,
            observation.Receipt.NativeAttestationKeyId,
            observation.Lifecycle,
            observation.RuntimePolicyRevision,
            observation.PolicyObservedAtUtc,
            observation.TrustDisposition);
        ValidateEntry(entry);
        return entry;
    }

    private static async Task<ExecutionRuntimeLifecyclePolicyFileV1> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || info.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
        {
            throw new InvalidDataException(
                $"Execution-runtime lifecycle policy document '{path}' is missing, linked, or too large.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            RejectDuplicateProperties(bytes);
            return JsonSerializer.Deserialize<ExecutionRuntimeLifecyclePolicyFileV1>(
                       bytes,
                       ExecutionRuntimeStore.JsonOptions)
                   ?? throw new InvalidDataException(
                       $"Execution-runtime lifecycle policy document '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Execution-runtime lifecycle policy document '{path}' is invalid.",
                exception);
        }
    }

    private static void ValidateFile(ExecutionRuntimeLifecyclePolicyFileV1 file)
    {
        if (file.SchemaVersion != ExecutionRuntimeLifecyclePolicyFileV1.CurrentSchemaVersion
            || file.Generation < 0
            || file.Policies is null)
        {
            throw new InvalidDataException("The execution-runtime lifecycle policy document is invalid.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var entry in file.Policies)
        {
            ValidateEntry(entry);
            var key = entry.Identity.SingleFlightKey;
            if (!identities.Add(key)
                || previous is not null && string.Compare(previous, key, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException(
                    "The execution-runtime lifecycle policy document contains duplicate or unsorted identities.");
            }
            previous = key;
        }
    }

    private static void ValidateEntry(ExecutionRuntimeLifecyclePolicyEntryV1 entry)
    {
        if (entry is null || entry.Identity is null || entry.Lifecycle is null)
        {
            throw new InvalidDataException("An execution-runtime lifecycle policy entry is invalid.");
        }
        ValidateSegment(entry.Identity.RuntimeId, nameof(entry.Identity.RuntimeId));
        ValidateSegment(entry.Identity.Version, nameof(entry.Identity.Version));
        ValidateSegment(entry.Identity.Rid, nameof(entry.Identity.Rid));
        ValidateSegment(entry.Identity.ModuleAbi, nameof(entry.Identity.ModuleAbi));
        if (entry.Identity.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision
            || !IsSupportedRuntimePair(entry.Identity.RuntimeId, entry.Identity.ModuleAbi)
            || !string.Equals(entry.WorkerProtocol, SunderExecutionRuntimeFormat.WorkerProtocol, StringComparison.Ordinal)
            || entry.ArtifactSize is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes
            || entry.MinimumHostTrustGeneration <= 0
            || entry.RuntimePolicyRevision <= 0
            || entry.PolicyObservedAtUtc == default
            || entry.TrustDisposition is not (
                ExecutionRuntimeTrustDispositions.Trusted or ExecutionRuntimeTrustDispositions.Denied)
            || !IsValidLifecycle(entry.Lifecycle))
        {
            throw new InvalidDataException("An execution-runtime lifecycle policy entry is invalid.");
        }
        ValidateSha256(entry.ManifestSha256);
        ValidateSha256(entry.ArtifactSha256);
        ValidateSha256(entry.ContentIdentity);
        ValidateSha256(entry.NativeAttestationSha256);
        ValidateSegment(entry.SigningKeyId, nameof(entry.SigningKeyId));
        ValidateSegment(
            entry.NativeAttestationKeyId,
            nameof(entry.NativeAttestationKeyId));
        var origin = NormalizeOrigin(entry.RegistryOrigin);
        if (!string.Equals(origin, entry.RegistryOrigin, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An execution-runtime lifecycle policy origin is not canonical.");
        }
    }

    private static void ValidateSha256(string value)
    {
        try
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(value, nameof(value));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("An execution-runtime lifecycle policy digest is invalid.", exception);
        }
    }

    private static string NormalizeOrigin(string origin)
    {
        try
        {
            return RegistryOrigin.Normalize(origin).AbsoluteUri;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "An execution-runtime lifecycle policy origin is invalid.",
                exception);
        }
    }

    private static void ValidateSegment(string value, string name)
    {
        try
        {
            _ = ExecutionRuntimeStoreLayout.ValidateSegment(value, name);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "An execution-runtime lifecycle policy identifier is invalid.",
                exception);
        }
    }

    private static bool IsSupportedRuntimePair(string runtimeId, string moduleAbi)
        => runtimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => moduleAbi == SunderExecutionRuntimeFormat.DotNetModuleAbi,
            SunderExecutionRuntimeFormat.NodeRuntimeId => moduleAbi == SunderExecutionRuntimeFormat.NodeModuleAbi,
            _ => false,
        };

    private static bool IsValidLifecycle(RegistryExecutionRuntimeLifecycle lifecycle)
    {
        if (lifecycle.StagedAtUtc == default
            || lifecycle.SupportedFromUtc is not { } supported
            || supported < lifecycle.StagedAtUtc
            || lifecycle.DeprecatedAtUtc is { } deprecated && deprecated < supported)
        {
            return false;
        }
        var lifecycleFloor = lifecycle.DeprecatedAtUtc ?? supported;
        if (lifecycle.InstallBlockedAtUtc is { } installBlocked && installBlocked < lifecycleFloor
            || lifecycle.ActivationBlockedAtUtc is { } activationBlocked && activationBlocked < lifecycleFloor
            || lifecycle.RevokedAtUtc is { } revoked && revoked < lifecycleFloor)
        {
            return false;
        }
        return lifecycle.State switch
        {
            ExecutionRuntimeLifecycleStates.Supported => lifecycle.DeprecatedAtUtc is null
                                                         && lifecycle.InstallBlockedAtUtc is null
                                                         && lifecycle.ActivationBlockedAtUtc is null
                                                         && lifecycle.RevokedAtUtc is null,
            ExecutionRuntimeLifecycleStates.Deprecated => lifecycle.DeprecatedAtUtc is not null
                                                          && lifecycle.RevokedAtUtc is null,
            ExecutionRuntimeLifecycleStates.Retired => lifecycle.DeprecatedAtUtc is not null
                                                       && lifecycle.InstallBlockedAtUtc is not null
                                                       && lifecycle.RevokedAtUtc is null,
            ExecutionRuntimeLifecycleStates.Revoked => lifecycle.RevokedAtUtc is not null,
            _ => false,
        };
    }

    private static bool SamePolicy(
        ExecutionRuntimeLifecyclePolicyEntryV1 first,
        ExecutionRuntimeLifecyclePolicyEntryV1 second)
        => first with { PolicyObservedAtUtc = second.PolicyObservedAtUtc } == second;

    private static bool SameImmutableSubject(
        ExecutionRuntimeLifecyclePolicyEntryV1 first,
        ExecutionRuntimeLifecyclePolicyEntryV1 second)
        => first.Identity == second.Identity
           && string.Equals(first.RegistryOrigin, second.RegistryOrigin, StringComparison.Ordinal)
           && string.Equals(first.WorkerProtocol, second.WorkerProtocol, StringComparison.Ordinal)
           && string.Equals(first.ManifestSha256, second.ManifestSha256, StringComparison.Ordinal)
           && string.Equals(first.ArtifactSha256, second.ArtifactSha256, StringComparison.Ordinal)
           && first.ArtifactSize == second.ArtifactSize
           && string.Equals(first.ContentIdentity, second.ContentIdentity, StringComparison.Ordinal)
           && string.Equals(first.SigningKeyId, second.SigningKeyId, StringComparison.Ordinal)
           && first.MinimumHostTrustGeneration == second.MinimumHostTrustGeneration
           && string.Equals(
               first.NativeAttestationSha256,
               second.NativeAttestationSha256,
               StringComparison.Ordinal)
           && string.Equals(
               first.NativeAttestationKeyId,
               second.NativeAttestationKeyId,
               StringComparison.Ordinal);

    private static bool IsForwardTransition(string previous, string next)
        => previous == next
           || previous == ExecutionRuntimeLifecycleStates.Supported
           || previous == ExecutionRuntimeLifecycleStates.Deprecated
           && next is ExecutionRuntimeLifecycleStates.Retired or ExecutionRuntimeLifecycleStates.Revoked
           || previous == ExecutionRuntimeLifecycleStates.Retired
           && next == ExecutionRuntimeLifecycleStates.Revoked;

    private static bool BroadensLifecyclePermission(
        RegistryExecutionRuntimeLifecycle previous,
        RegistryExecutionRuntimeLifecycle next)
        => DeadlineMovedLater(previous.InstallBlockedAtUtc, next.InstallBlockedAtUtc)
           || DeadlineMovedLater(previous.ActivationBlockedAtUtc, next.ActivationBlockedAtUtc);

    private static bool DeadlineMovedLater(DateTimeOffset? previous, DateTimeOffset? next)
        => previous is not null && (next is null || next > previous);

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var objects = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    objects.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var properties = objects.Peek()
                                     ?? throw new JsonException("A JSON property appeared outside an object.");
                    if (!properties.Add(reader.GetString()!))
                    {
                        throw new JsonException("Duplicate JSON properties are not allowed.");
                    }
                    break;
            }
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
