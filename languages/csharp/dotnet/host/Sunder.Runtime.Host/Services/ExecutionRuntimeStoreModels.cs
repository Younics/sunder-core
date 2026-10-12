using System.Text.Json.Serialization;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeIdentity(
    ConfiguredOfficialSourceId SourceId,
    string RuntimeId,
    string VendorVersion,
    string Rid,
    string ModuleAbi,
    RuntimeExecutionRuntimeChecksumAlgorithm VendorChecksumAlgorithm,
    string VendorChecksumDigest)
{
    [JsonIgnore]
    public string SingleFlightKey
        => $"{SourceId}\n{RuntimeId}\n{VendorVersion}\n{Rid}\n{ModuleAbi}\n{VendorChecksumAlgorithm}\n{VendorChecksumDigest}";

    [JsonIgnore]
    public string VersionKey
        => $"{SourceId}\n{RuntimeId}\n{VendorVersion}\n{Rid}\n{ModuleAbi}\n{VendorChecksumAlgorithm}";

    public static ExecutionRuntimeIdentity FromContract(RuntimeExecutionRuntimeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            identity.SourceId,
            identity.RuntimeId,
            identity.VendorVersion,
            identity.Rid,
            identity.ModuleAbi,
            identity.VendorChecksumAlgorithm,
            identity.VendorChecksumDigest);
    }

    public RuntimeExecutionRuntimeIdentity ToContract()
        => new(SourceId, RuntimeId, VendorVersion, Rid, ModuleAbi, VendorChecksumAlgorithm, VendorChecksumDigest);
}

internal sealed record ExecutionRuntimeAcquisitionPlan(
    RuntimeExecutionRuntimeVendorArtifact Artifact,
    string? ExpectedArchiveSha256 = null,
    long? ExpectedArchiveSize = null,
    string? ExpectedContentIdentity = null,
    RuntimeExecutionRuntimeProvenance? RetainedProvenance = null)
{
    public ExecutionRuntimeIdentity Identity => ExecutionRuntimeIdentity.FromContract(Artifact.Identity);

    public void Validate()
    {
        OfficialRuntimeSourcePolicy.ValidateArtifact(Artifact);
        if (ExpectedArchiveSha256 is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(ExpectedArchiveSha256, nameof(ExpectedArchiveSha256));
        }
        if (ExpectedArchiveSize is <= 0 or > OfficialRuntimeSourcePolicy.MaxArchiveBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedArchiveSize),
                "The expected execution runtime archive size is invalid.");
        }
        if (ExpectedContentIdentity is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(ExpectedContentIdentity, nameof(ExpectedContentIdentity));
        }
        if (RetainedProvenance is not null)
        {
            if (ExpectedArchiveSha256 is null || ExpectedArchiveSize is null || ExpectedContentIdentity is null)
                throw new InvalidDataException("Retained runtime provenance requires complete exact installed evidence.");
            OfficialRuntimeSourcePolicy.ValidateInstalledArtifact(Artifact.Identity,
                new(ExpectedArchiveSha256, ExpectedArchiveSize.Value, ExpectedContentIdentity, RetainedProvenance));
            if (RetainedProvenance.Distribution.MaterializationProfile != Artifact.Distribution.MaterializationProfile)
                throw new InvalidDataException("Exact recovery cannot change the materialization profile.");
        }
    }
}

internal sealed record ExecutionRuntimeReceiptContentV1(
    string Path,
    string Sha256,
    long Size,
    int UnixMode);

internal sealed class ExecutionRuntimeReceiptV1
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ExecutionRuntimeReceiptV1(
        int schemaVersion,
        ExecutionRuntimeIdentity identity,
        string archiveSha256,
        long archiveSize,
        string contentIdentity,
        string launcherPath,
        string launcherSha256,
        long launcherSize,
        IReadOnlyList<string> launcherArguments,
        IReadOnlyList<ExecutionRuntimeReceiptContentV1> content,
        RuntimeExecutionRuntimeProvenance provenance,
        RuntimeExecutionRuntimeSupportStatus support,
        DateTimeOffset installedAtUtc)
    {
        SchemaVersion = schemaVersion;
        Identity = identity;
        ArchiveSha256 = archiveSha256;
        ArchiveSize = archiveSize;
        ContentIdentity = contentIdentity;
        LauncherPath = launcherPath;
        LauncherSha256 = launcherSha256;
        LauncherSize = launcherSize;
        LauncherArguments = launcherArguments is null ? [] : Array.AsReadOnly(launcherArguments.ToArray());
        Content = content is null ? [] : Array.AsReadOnly(content.ToArray());
        Provenance = provenance;
        Support = support;
        InstalledAtUtc = installedAtUtc;
    }

    public int SchemaVersion { get; }
    public ExecutionRuntimeIdentity Identity { get; }
    public string ArchiveSha256 { get; }
    public long ArchiveSize { get; }
    public string ContentIdentity { get; }
    public string LauncherPath { get; }
    public string LauncherSha256 { get; }
    public long LauncherSize { get; }
    public IReadOnlyList<string> LauncherArguments { get; }
    public IReadOnlyList<ExecutionRuntimeReceiptContentV1> Content { get; }
    public RuntimeExecutionRuntimeProvenance Provenance { get; }
    public RuntimeExecutionRuntimeSupportStatus Support { get; }
    public DateTimeOffset InstalledAtUtc { get; }

    [JsonIgnore]
    public RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact => new(
        ArchiveSha256,
        ArchiveSize,
        ContentIdentity,
        Provenance);
}

internal sealed record ExecutionRuntimeEnsureResult(
    ExecutionRuntimeReceiptV1 Receipt,
    bool AlreadyPresent);

internal sealed class ExecutionRuntimeOwnedArchive(string path, Action release, string? finalDownloadUrl = null) : IDisposable
{
    private Action? _release = release ?? throw new ArgumentNullException(nameof(release));

    public string Path { get; } = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("An owned execution-runtime archive path is required.", nameof(path))
            : path);

    public string? FinalDownloadUrl { get; } = finalDownloadUrl;

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
    string ReceiptSha256,
    IReadOnlyList<ExecutionRuntimeFileObservationV1> Files)
{
    public const int CurrentSchemaVersion = 3;
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
    RootPublished = 3,
    ReceiptPublished = 4,
    Completed = 5,
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

internal sealed class ExecutionRuntimePlanMismatchException(string message) : Exception(message);

internal sealed class ExecutionRuntimeStoreConflictException(string message) : Exception(message);

internal sealed class ExecutionRuntimeRootInUseException(string message) : IOException(message);

internal sealed record ExecutionRuntimeGarbageCollectionResult(
    int RootsRemoved,
    int ArchivesRemoved);

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
