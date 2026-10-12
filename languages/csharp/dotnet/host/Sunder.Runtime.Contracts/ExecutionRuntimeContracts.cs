using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimeExecutionRuntimeStatuses
{
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
    public const string Quarantined = "quarantined";
}

/// <summary>Host-configured official sources; callers cannot introduce another source or URL.</summary>
[JsonConverter(typeof(RuntimeContractStringEnumConverter<ConfiguredOfficialSourceId>))]
public enum ConfiguredOfficialSourceId
{
    [JsonStringEnumMemberName("dotnet")] DotNet = 1,
    [JsonStringEnumMemberName("node")] Node = 2,
}

[JsonConverter(typeof(RuntimeContractStringEnumConverter<RuntimeExecutionRuntimeChecksumAlgorithm>))]
public enum RuntimeExecutionRuntimeChecksumAlgorithm
{
    [JsonStringEnumMemberName("sha512")] Sha512 = 1,
    [JsonStringEnumMemberName("sha256")] Sha256 = 2,
}

[JsonConverter(typeof(RuntimeContractStringEnumConverter<RuntimeExecutionRuntimeArchiveFormat>))]
public enum RuntimeExecutionRuntimeArchiveFormat
{
    [JsonStringEnumMemberName("zip")] Zip = 1,
    [JsonStringEnumMemberName("tar.gz")] TarGzip = 2,
}

[JsonConverter(typeof(RuntimeContractStringEnumConverter<RuntimeExecutionRuntimeSupportState>))]
public enum RuntimeExecutionRuntimeSupportState
{
    Unknown,
    Supported,
    EndOfLife,
}

public static class RuntimeExecutionRuntimeOperationActions
{
    public const string Install = "install";
    public const string Ensure = "ensure";
    public const string Verify = "verify";
    public const string Repair = "repair";
    public const string GarbageCollect = "garbage-collect";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeRequirement(
    [property: JsonRequired] string RuntimeId,
    [property: JsonRequired] string VersionRange,
    [property: JsonRequired] string ModuleAbi);

/// <summary>Exact upstream bytes. DotNet uses SHA-512; Node uses SHA-256. Digests are lowercase hex.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeIdentity(
    [property: JsonRequired] ConfiguredOfficialSourceId SourceId,
    [property: JsonRequired] string RuntimeId,
    [property: JsonRequired] string VendorVersion,
    [property: JsonRequired] string Rid,
    [property: JsonRequired] string ModuleAbi,
    [property: JsonRequired] RuntimeExecutionRuntimeChecksumAlgorithm VendorChecksumAlgorithm,
    [property: JsonRequired] string VendorChecksumDigest);

/// <summary>Host-resolved official distribution evidence, never an acquisition request supplied by a client.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeDistribution(
    [property: JsonRequired] string DownloadUrl,
    [property: JsonRequired] string MetadataUrl,
    [property: JsonRequired] string MetadataSha256,
    [property: JsonRequired] string? ChecksumUrl,
    [property: JsonRequired] string? ChecksumDocumentSha256,
    [property: JsonRequired] RuntimeExecutionRuntimeArchiveFormat ArchiveFormat,
    [property: JsonRequired] string MaterializationProfile,
    [property: JsonRequired] long? DownloadSize);

/// <summary>Observed vendor support information, not an execution-policy lease or Registry authorization.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeSupportStatus(
    [property: JsonRequired] RuntimeExecutionRuntimeSupportState State,
    [property: JsonRequired] string SupportLine,
    [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] DateTimeOffset? EndOfLifeAtUtc,
    [property: JsonRequired] string? Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeVendorArtifact(
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity Identity,
    [property: JsonRequired] RuntimeExecutionRuntimeDistribution Distribution,
    [property: JsonRequired] RuntimeExecutionRuntimeSupportStatus Support);

/// <summary>Recorded only after a successful official-source download and checksum verification.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeProvenance(
    [property: JsonRequired] RuntimeExecutionRuntimeDistribution Distribution,
    [property: JsonRequired] string FinalDownloadUrl,
    [property: JsonRequired] DateTimeOffset RetrievedAtUtc);

/// <summary>Local verified bytes; ContentIdentity commits the separate Host-owned extracted file inventory.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeInstalledArtifact(
    [property: JsonRequired] string ArchiveSha256,
    [property: JsonRequired] long ArchiveSize,
    [property: JsonRequired] string ContentIdentity,
    [property: JsonRequired] RuntimeExecutionRuntimeProvenance Provenance);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeHealth(
    [property: JsonRequired] string Status,
    [property: JsonRequired] DateTimeOffset CheckedAtUtc,
    [property: JsonRequired] bool RepairRecommended,
    [property: JsonRequired] string? Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstalledExecutionRuntimeSummary(
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity Identity,
    [property: JsonRequired] string Status,
    [property: JsonRequired] long Size,
    [property: JsonRequired] int PackageReferenceCount,
    [property: JsonRequired] RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact,
    [property: JsonRequired] RuntimeExecutionRuntimeHealth Health,
    [property: JsonRequired] RuntimeExecutionRuntimeSupportStatus Support,
    [property: JsonRequired] DateTimeOffset InstalledAtUtc,
    bool RetainedUntilUninstall = false);

/// <summary>Host installed-package state is the sole authority for this exact local binding.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageTargetExecutionRuntimeBinding(
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Rid,
    [property: JsonRequired] RuntimeExecutionRuntimeRequirement Requirement,
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity Runtime,
    [property: JsonRequired] RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact,
    [property: JsonRequired] DateTimeOffset ResolvedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionRuntimeRebindResult(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string PackageVersion,
    [property: JsonRequired] string PackageContentIdentity,
    [property: JsonRequired] PackageTargetExecutionRuntimeBinding? PreviousBinding,
    [property: JsonRequired] PackageTargetExecutionRuntimeBinding? NewBinding,
    [property: JsonRequired] bool RuntimeSessionApplied,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuntimeExecutionRuntimeImpact? Impact = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeEnsureRequest(
    [property: JsonRequired] RuntimeExecutionRuntimeRequirement Requirement,
    bool RetainUntilUninstall = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimePackageReference(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string PackageVersion,
    [property: JsonRequired] string PackageContentIdentity,
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Rid);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeImpact(
    [property: JsonRequired] int PackageReferenceCount,
    IReadOnlyList<string> ImpactedPackageIds,
    [property: JsonRequired] string Fingerprint)
{
    private IReadOnlyList<string> _impactedPackageIds = RuntimeContractCollections.FreezeBounded(
        ImpactedPackageIds, RuntimeExecutionRuntimeManagementLimits.MaximumItems);

    [JsonRequired]
    public IReadOnlyList<string> ImpactedPackageIds
    {
        get => _impactedPackageIds;
        init => _impactedPackageIds = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeActionRequest([property: JsonRequired] string ExpectedImpactFingerprint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeOperationResult(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] string Action,
    [property: JsonRequired] InstalledExecutionRuntimeSummary? Runtime,
    [property: JsonRequired] bool AlreadyPresent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuntimeExecutionRuntimeImpact? Impact = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeStoreFault(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Message,
    [property: JsonRequired] bool Retryable,
    [property: JsonRequired] string Operation,
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity? Runtime,
    [property: JsonRequired] string? PackageId,
    [property: JsonRequired] string? TargetRole,
    [property: JsonRequired] DateTimeOffset OccurredAtUtc);

public static partial class RuntimeApiRoutes
{
    public const string Root = "/api/v1";
    public const string ExecutionRuntimes = Root + "/execution-runtimes";
    public const string ExecutionRuntimeEnsure = ExecutionRuntimes + "/ensure";
    public const string ExecutionRuntimeGarbageCollection = ExecutionRuntimes + "/gc";

    public static string ExecutionRuntime(RuntimeExecutionRuntimeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var source = identity.SourceId switch
        {
            ConfiguredOfficialSourceId.DotNet => "dotnet",
            ConfiguredOfficialSourceId.Node => "node",
            _ => throw new ArgumentOutOfRangeException(nameof(identity), "Unknown official runtime source."),
        };
        var algorithm = identity.VendorChecksumAlgorithm switch
        {
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 => "sha512",
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 => "sha256",
            _ => throw new ArgumentOutOfRangeException(nameof(identity), "Unknown vendor checksum algorithm."),
        };
        return $"{ExecutionRuntimes}/sources/{source}/{Segment(identity.RuntimeId, nameof(identity.RuntimeId))}"
            + $"/versions/{Segment(identity.VendorVersion, nameof(identity.VendorVersion))}/{Segment(identity.Rid, nameof(identity.Rid))}"
            + $"/abis/{Segment(identity.ModuleAbi, nameof(identity.ModuleAbi))}/checksums/{algorithm}/{Segment(identity.VendorChecksumDigest, nameof(identity.VendorChecksumDigest))}";
    }

    public static string ExecutionRuntimeReferences(RuntimeExecutionRuntimeIdentity identity) => $"{ExecutionRuntime(identity)}/references";
    public static string ExecutionRuntimeImpact(RuntimeExecutionRuntimeIdentity identity) => $"{ExecutionRuntime(identity)}/impact";
    public static string ExecutionRuntimeVerify(RuntimeExecutionRuntimeIdentity identity) => $"{ExecutionRuntime(identity)}/verify";
    public static string ExecutionRuntimeRepair(RuntimeExecutionRuntimeIdentity identity) => $"{ExecutionRuntime(identity)}/repair";

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
}
