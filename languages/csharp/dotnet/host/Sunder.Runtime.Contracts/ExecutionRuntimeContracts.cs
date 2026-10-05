using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimeExecutionRuntimeStatuses
{
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
    public const string Quarantined = "quarantined";
}

public static class RuntimeExecutionRuntimeLifecycleStates
{
    public const string PolicyRequired = "policy-required";
}

public static class RuntimeExecutionRuntimeAcquisitionSources
{
    public const string Registry = "registry";
    public const string LocalImport = "local-import";
    public const string Development = "development";
}

public static class RuntimeExecutionRuntimeOperationActions
{
    public const string Ensure = "ensure";
    public const string Import = "import";
    public const string Verify = "verify";
    public const string Repair = "repair";
    public const string GarbageCollect = "garbage-collect";
}

public sealed record RuntimeExecutionRuntimeRequirement(
    string RuntimeId,
    string VersionRange,
    string ModuleAbi);

public sealed record RuntimeExecutionRuntimeIdentity(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi,
    string ManifestSha256,
    string ArtifactSha256);

public sealed record RuntimeExecutionRuntimeLifecycle(
    string State,
    DateTimeOffset? SupportedFromUtc,
    DateTimeOffset? DeprecatedAtUtc,
    DateTimeOffset? InstallBlockedAtUtc,
    DateTimeOffset? ActivationBlockedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? PolicyMessage,
    long RuntimePolicyRevision,
    DateTimeOffset PolicyObservedAtUtc);

public sealed record RuntimeExecutionRuntimeHealth(
    string Status,
    DateTimeOffset CheckedAtUtc,
    bool RepairRecommended,
    string? Message);

public sealed record InstalledExecutionRuntimeSummary(
    RuntimeExecutionRuntimeIdentity Identity,
    string Status,
    long Size,
    int PackageReferenceCount,
    string AcquisitionSource,
    string SigningKeyId,
    long TrustGeneration,
    RuntimeExecutionRuntimeHealth Health,
    RuntimeExecutionRuntimeLifecycle Lifecycle,
    DateTimeOffset InstalledAtUtc);

public sealed record PackageTargetExecutionRuntimeBinding(
    string Role,
    string Rid,
    RuntimeExecutionRuntimeRequirement Requirement,
    RuntimeExecutionRuntimeIdentity Runtime,
    long ArtifactSize,
    string RegistryOrigin,
    string AcquisitionSource,
    string SigningKeyId,
    long TrustGeneration,
    RuntimeExecutionRuntimeLifecycle Lifecycle,
    DateTimeOffset ResolvedAtUtc);

public sealed record ExecutionRuntimeRebindResult(
    bool Success,
    string? ErrorCode,
    string Message,
    string PackageId,
    string PackageVersion,
    string PackageContentIdentity,
    PackageTargetExecutionRuntimeBinding? PreviousBinding,
    PackageTargetExecutionRuntimeBinding? NewBinding,
    bool RuntimeSessionApplied,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuntimeExecutionRuntimeImpact? Impact = null);

public sealed record RuntimeExecutionRuntimeEnsureRequest(
    string RegistryOrigin,
    RuntimeExecutionRuntimeRequirement Requirement);

public sealed record RuntimeExecutionRuntimeImportRequest(string UploadId);

public sealed record RuntimeExecutionRuntimePackageReference(
    string PackageId,
    string PackageVersion,
    string PackageContentIdentity,
    string Role,
    string Rid);

public sealed record RuntimeExecutionRuntimeImpact(
    int PackageReferenceCount,
    IReadOnlyList<string> ImpactedPackageIds,
    string Fingerprint)
{
    private IReadOnlyList<string> _impactedPackageIds =
        RuntimeContractCollections.Freeze(ImpactedPackageIds);

    public IReadOnlyList<string> ImpactedPackageIds
    {
        get => _impactedPackageIds;
        init => _impactedPackageIds = RuntimeContractCollections.Freeze(value);
    }
}

public sealed record RuntimeExecutionRuntimeActionRequest(string ExpectedImpactFingerprint);

public sealed record RuntimeExecutionRuntimeOperationResult(
    bool Success,
    string? ErrorCode,
    string Message,
    string Action,
    InstalledExecutionRuntimeSummary? Runtime,
    bool AlreadyPresent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuntimeExecutionRuntimeImpact? Impact = null);

public sealed record RuntimeExecutionRuntimeStoreFault(
    string Code,
    string Message,
    bool Retryable,
    string Operation,
    string? RuntimeId,
    string? Version,
    int? PackagingRevision,
    string? Rid,
    string? PackageId,
    string? TargetRole,
    DateTimeOffset OccurredAtUtc);

public static partial class RuntimeApiRoutes
{
    public const string Root = "/api/v1";
    public const string ExecutionRuntimes = Root + "/execution-runtimes";
    public const string ExecutionRuntimeEnsure = ExecutionRuntimes + "/ensure";
    public const string ExecutionRuntimeImport = ExecutionRuntimes + "/import";
    public const string ExecutionRuntimeGarbageCollection = ExecutionRuntimes + "/gc";

    public static string ExecutionRuntime(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi)
        => $"{ExecutionRuntimes}/{Segment(runtimeId, nameof(runtimeId))}/versions/{Segment(version, nameof(version))}/revisions/{PositiveRevision(packagingRevision)}/{Segment(rid, nameof(rid))}/abis/{Segment(moduleAbi, nameof(moduleAbi))}";

    public static string ExecutionRuntimeReferences(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi)
        => $"{ExecutionRuntime(runtimeId, version, packagingRevision, rid, moduleAbi)}/references";

    public static string ExecutionRuntimeImpact(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi)
        => $"{ExecutionRuntime(runtimeId, version, packagingRevision, rid, moduleAbi)}/impact";

    public static string ExecutionRuntimeVerify(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi)
        => $"{ExecutionRuntime(runtimeId, version, packagingRevision, rid, moduleAbi)}/verify";

    public static string ExecutionRuntimeRepair(
        string runtimeId,
        string version,
        int packagingRevision,
        string rid,
        string moduleAbi)
        => $"{ExecutionRuntime(runtimeId, version, packagingRevision, rid, moduleAbi)}/repair";

    public static string PackageExecutionRuntimeBindings(string packageId)
        => $"{Root}/packages/{Segment(packageId, nameof(packageId))}/execution-runtime-bindings";

    private static string Segment(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value is "." or "..")
        {
            throw new ArgumentException("A Runtime API route segment cannot be empty, '.' or '..'.", parameterName);
        }

        return Uri.EscapeDataString(value);
    }

    private static int PositiveRevision(int packagingRevision)
        => packagingRevision > 0
            ? packagingRevision
            : throw new ArgumentOutOfRangeException(nameof(packagingRevision), "A packaging revision must be positive.");
}
