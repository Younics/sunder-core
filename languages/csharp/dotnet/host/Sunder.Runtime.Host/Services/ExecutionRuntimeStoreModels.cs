using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal enum ExecutionRuntimeTrustEnvironment
{
    Production,
    Development,
}

internal sealed record ExecutionRuntimeIdentity(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi)
{
    public string SingleFlightKey
        => $"{RuntimeId}\n{Version}\n{PackagingRevision}\n{Rid}\n{ModuleAbi}";
}

internal sealed record ExecutionRuntimeAcquisitionPlan(
    ExecutionRuntimeIdentity Identity,
    string ArtifactSha256,
    long ArtifactSize,
    string ManifestSha256,
    string? ContentIdentity,
    string SigningKeyId,
    long MinimumHostTrustGeneration,
    ExecutionRuntimeTrustEnvironment TrustEnvironment,
    string AcquisitionSource,
    string? RegistryOrigin = null,
    string? NativeAttestationSha256 = null,
    string? NativeAttestationKeyId = null)
{
    public void Validate(bool developmentAllowInsecureLoopbackRegistry = false)
    {
        ArgumentNullException.ThrowIfNull(Identity);
        ExecutionRuntimeStoreLayout.ValidateSegment(Identity.RuntimeId, nameof(Identity.RuntimeId));
        ExecutionRuntimeStoreLayout.ValidateSegment(Identity.Version, nameof(Identity.Version));
        ExecutionRuntimeStoreLayout.ValidateSegment(Identity.Rid, nameof(Identity.Rid));
        ExecutionRuntimeStoreLayout.ValidateSegment(Identity.ModuleAbi, nameof(Identity.ModuleAbi));
        ExecutionRuntimeStoreLayout.ValidateSegment(SigningKeyId, nameof(SigningKeyId));
        ExecutionRuntimeStoreLayout.ValidateSha256(ArtifactSha256, nameof(ArtifactSha256));
        ExecutionRuntimeStoreLayout.ValidateSha256(ManifestSha256, nameof(ManifestSha256));
        if (ContentIdentity is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(ContentIdentity, nameof(ContentIdentity));
        }
        if (NativeAttestationSha256 is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(
                NativeAttestationSha256,
                nameof(NativeAttestationSha256));
        }
        if (NativeAttestationKeyId is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSegment(
                NativeAttestationKeyId,
                nameof(NativeAttestationKeyId));
        }
        if (Identity.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Identity.PackagingRevision),
                "The execution runtime packaging revision is invalid.");
        }
        if (ArtifactSize is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArtifactSize),
                "The execution runtime artifact size is invalid.");
        }
        if (MinimumHostTrustGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumHostTrustGeneration),
                "The minimum Host trust generation must be positive.");
        }
        if (!Enum.IsDefined(TrustEnvironment))
        {
            throw new ArgumentOutOfRangeException(nameof(TrustEnvironment));
        }
        if (AcquisitionSource is not (
                RuntimeExecutionRuntimeAcquisitionSources.Registry
                or RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                or RuntimeExecutionRuntimeAcquisitionSources.Development))
        {
            throw new ArgumentException("The execution runtime acquisition source is invalid.", nameof(AcquisitionSource));
        }
        if ((TrustEnvironment == ExecutionRuntimeTrustEnvironment.Development)
            != (AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development))
        {
            throw new ArgumentException(
                "Development trust and the development acquisition source must be paired.",
                nameof(AcquisitionSource));
        }
        if (AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Registry
            && (!Uri.TryCreate(RegistryOrigin, UriKind.Absolute, out var origin)
                || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Registry acquisition requires an absolute HTTPS Registry origin.",
                nameof(RegistryOrigin));
        }
        if (AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development
            && RegistryOrigin is not null
            && (!Uri.TryCreate(RegistryOrigin, UriKind.Absolute, out var developmentOrigin)
                || !string.Equals(developmentOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                   && (!developmentAllowInsecureLoopbackRegistry
                       || !string.Equals(developmentOrigin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                       || !developmentOrigin.IsLoopback
                       || !string.IsNullOrEmpty(developmentOrigin.UserInfo))))
        {
            throw new ArgumentException(
                "Registry-associated development acquisition requires HTTPS or an explicitly allowed loopback HTTP Registry origin.",
                nameof(RegistryOrigin));
        }
    }
}

internal sealed record ExecutionRuntimeReceiptContentV1(
    string Path,
    string Sha256,
    long Size);

internal sealed class ExecutionRuntimeReceiptV1
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ExecutionRuntimeReceiptV1(
        int schemaVersion,
        ExecutionRuntimeIdentity identity,
        string workerProtocol,
        string artifactSha256,
        long artifactSize,
        string manifestSha256,
        string contentIdentity,
        string launcherPath,
        string launcherSha256,
        long launcherSize,
        IReadOnlyList<string> launcherArguments,
        IReadOnlyList<ExecutionRuntimeReceiptContentV1> content,
        string nativeAttestationSha256,
        string nativeAttestationKeyId,
        string signingKeyId,
        long trustGeneration,
        ExecutionRuntimeTrustEnvironment trustEnvironment,
        string acquisitionSource,
        string? registryOrigin,
        DateTimeOffset installedAtUtc)
    {
        SchemaVersion = schemaVersion;
        Identity = identity;
        WorkerProtocol = workerProtocol;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
        ManifestSha256 = manifestSha256;
        ContentIdentity = contentIdentity;
        LauncherPath = launcherPath;
        LauncherSha256 = launcherSha256;
        LauncherSize = launcherSize;
        LauncherArguments = launcherArguments is null ? [] : Array.AsReadOnly(launcherArguments.ToArray());
        Content = content is null ? [] : Array.AsReadOnly(content.ToArray());
        NativeAttestationSha256 = nativeAttestationSha256;
        NativeAttestationKeyId = nativeAttestationKeyId;
        SigningKeyId = signingKeyId;
        TrustGeneration = trustGeneration;
        TrustEnvironment = trustEnvironment;
        AcquisitionSource = acquisitionSource;
        RegistryOrigin = registryOrigin;
        InstalledAtUtc = installedAtUtc;
    }

    public int SchemaVersion { get; }
    public ExecutionRuntimeIdentity Identity { get; }
    public string WorkerProtocol { get; }
    public string ArtifactSha256 { get; }
    public long ArtifactSize { get; }
    public string ManifestSha256 { get; }
    public string ContentIdentity { get; }
    public string LauncherPath { get; }
    public string LauncherSha256 { get; }
    public long LauncherSize { get; }
    public IReadOnlyList<string> LauncherArguments { get; }
    public IReadOnlyList<ExecutionRuntimeReceiptContentV1> Content { get; }
    public string NativeAttestationSha256 { get; }
    public string NativeAttestationKeyId { get; }
    public string SigningKeyId { get; }
    public long TrustGeneration { get; }
    public ExecutionRuntimeTrustEnvironment TrustEnvironment { get; }
    public string AcquisitionSource { get; }
    public string? RegistryOrigin { get; }
    public DateTimeOffset InstalledAtUtc { get; }
}

internal sealed record ExecutionRuntimeEnsureResult(
    ExecutionRuntimeReceiptV1 Receipt,
    bool AlreadyPresent);

internal sealed class ExecutionRuntimeOwnedArchive(string path, Action release) : IDisposable
{
    private Action? _release = release ?? throw new ArgumentNullException(nameof(release));

    public string Path { get; } = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("An owned execution-runtime archive path is required.", nameof(path))
            : path);

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null)
        {
            return;
        }
        try
        {
            release();
        }
        catch
        {
            // Owned archive cleanup is best effort and must not replace the flight result.
        }
    }
}

internal sealed record ExecutionRuntimeFileObservationV1(
    string RelativePath,
    long Size,
    long LastWriteTimeUtcTicks);

internal sealed record ExecutionRuntimeHealthMarkerV1(
    int SchemaVersion,
    string ContentIdentity,
    string Status,
    bool DeepVerified,
    DateTimeOffset CheckedAtUtc,
    string? Message,
    string TrustProfileId,
    long TrustGeneration,
    ExecutionRuntimeTrustEnvironment TrustEnvironment,
    string SigningKeyId,
    string? SigningKeySha256,
    string ReceiptSha256,
    IReadOnlyList<ExecutionRuntimeFileObservationV1> Files)
{
    public const int CurrentSchemaVersion = 2;
}

internal sealed record ExecutionRuntimeVerificationResult(
    bool Healthy,
    bool DeepVerified,
    DateTimeOffset CheckedAtUtc,
    string? Message);

internal interface IExecutionRuntimeVerificationIoObserver
{
    void RecordPayloadContent(string relativePath, long bytesRead, long bytesHashed);
}

internal enum ExecutionRuntimeStoreTransactionPhase
{
    Prepared = 0,
    ArchivePublished = 1,
    ManifestPublished = 2,
    RootPublished = 3,
    ReceiptPublished = 4,
    Completed = 5,
    // Journals persist enum values numerically, so new phases must be appended.
    RootSealed = 6,
}

internal sealed record ExecutionRuntimeStoreTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    ExecutionRuntimeStoreTransactionPhase Phase,
    ExecutionRuntimeReceiptV1 Receipt)
{
    public const int CurrentSchemaVersion = 1;
}

internal enum ExecutionRuntimeStoreFaultPoint
{
    ArchiveSourceOpening,
    Validated,
    JournalPrepared,
    ArchivePublished,
    ManifestPublished,
    RootSealed,
    RootMoved,
    RootPublished,
    ReceiptPublished,
    HealthPublished,
    Cleanup,
    TombstoneRootMoved,
    TombstoneDeletionCommitted,
    TombstoneCommittedRootDeleted,
    TombstoneRootMovable,
}

internal interface IExecutionRuntimeStoreFaultInjector
{
    void Hit(ExecutionRuntimeStoreFaultPoint point);
}

internal sealed class ExecutionRuntimeStoreSimulatedCrashException(ExecutionRuntimeStoreFaultPoint point)
    : Exception($"Simulated execution runtime store crash at {point}.")
{
    public ExecutionRuntimeStoreFaultPoint Point { get; } = point;
}

internal sealed class ExecutionRuntimeTrustException(string message) : Exception(message);

internal sealed class ExecutionRuntimePlanMismatchException(string message) : Exception(message);

internal sealed class ExecutionRuntimeStoreConflictException(string message) : Exception(message);

internal sealed class ExecutionRuntimeRootInUseException(string message) : IOException(message);

internal sealed record ExecutionRuntimeGarbageCollectionResult(
    int RootsRemoved,
    int ArchivesRemoved,
    int ManifestsRemoved);

internal sealed record ExecutionRuntimeGarbageCollectionMarkV1(
    int SchemaVersion,
    string ContentIdentity,
    DateTimeOffset UnreferencedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed record ExecutionRuntimeRootTombstoneV1(
    int SchemaVersion,
    string TombstoneId,
    ExecutionRuntimeRootTombstonePhase Phase,
    string ContentIdentity,
    IReadOnlyList<ExecutionRuntimeIdentity> ReceiptIdentities,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

internal enum ExecutionRuntimeRootTombstonePhase
{
    Prepared,
    DeletionCommitted,
}
