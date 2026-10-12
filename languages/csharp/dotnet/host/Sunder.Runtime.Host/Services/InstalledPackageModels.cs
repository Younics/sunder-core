using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
namespace Sunder.Runtime.Host.Services;

internal sealed record InstalledPackageStateFile(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packages")] IReadOnlyList<InstalledPackageRecord> Packages);

internal sealed record InstalledPackageRecord(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("installPath")] string InstallPath,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("contentIdentity")] string ContentIdentity,
    [property: JsonPropertyName("contentInventory")] IReadOnlyList<InstalledPackageContentRecord> ContentInventory,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<InstalledPackageDependencyRecord> DependsOn,
    [property: JsonPropertyName("isEnabled")] bool IsEnabled,
    [property: JsonPropertyName("installedAtUtc")] DateTimeOffset InstalledAtUtc,
    [property: JsonPropertyName("provenance")] InstalledPackageProvenanceRecord Provenance,
    [property: JsonPropertyName("capabilityGrants")] IReadOnlyList<InstalledPackageCapabilityGrant> CapabilityGrants,
    IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>? ExecutionRuntimeBindings = null)
{
    [JsonPropertyName("policy"), JsonRequired]
    public InstalledPackagePolicyRecord? Policy { get; init; }

    [JsonPropertyName("sourcePackageContentIdentity"), JsonRequired]
    public string? SourcePackageContentIdentity { get; init; }
    [JsonPropertyName("compatibility"), JsonRequired]
    public InstalledPackageCompatibilityRecord? Compatibility { get; init; }

    private IReadOnlyList<InstalledPackageTargetRecord>? _acquiredTargets;
    // null requires a complete archive; an empty array is explicit metadata-only
    // acquisition. Missing state is rejected, never inferred from available files.
    [JsonPropertyName("acquiredTargets"), JsonRequired]
    public IReadOnlyList<InstalledPackageTargetRecord>? AcquiredTargets
    {
        get => _acquiredTargets;
        init => _acquiredTargets = value is null ? null : Array.AsReadOnly(value.ToArray());
    }

    private IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> _executionRuntimeBindings
        = Array.AsReadOnly((ExecutionRuntimeBindings ?? []).ToArray());

    [JsonPropertyName("executionRuntimeBindings"), JsonRequired]
    public IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> ExecutionRuntimeBindings
    {
        get => _executionRuntimeBindings;
        init => _executionRuntimeBindings = Array.AsReadOnly(
            (value ?? throw new InvalidDataException("Installed package execution-runtime bindings cannot be null.")).ToArray());
    }
}

internal sealed record InstalledPackageTargetRecord(
    [property: JsonPropertyName("role"), JsonRequired] string Role,
    [property: JsonPropertyName("rid"), JsonRequired] string Rid)
{
    internal SunderPackageTargetKey ToKey() => new(Role, Rid);
}

internal sealed record InstalledPackageExecutionRuntimeBindingRecord(
    [property: JsonPropertyName("role"), JsonRequired] string Role,
    [property: JsonPropertyName("rid"), JsonRequired] string Rid,
    [property: JsonPropertyName("requirement"), JsonRequired] RuntimeExecutionRuntimeRequirement Requirement,
    [property: JsonPropertyName("runtime"), JsonRequired] RuntimeExecutionRuntimeIdentity Runtime,
    [property: JsonPropertyName("installedArtifact"), JsonRequired] RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact,
    [property: JsonPropertyName("resolvedAtUtc"), JsonRequired] DateTimeOffset ResolvedAtUtc);

internal sealed record InstalledPackageCapabilityGrant(
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("packageVersion")] string PackageVersion,
    [property: JsonPropertyName("manifestSha256")] string ManifestSha256);

internal sealed record InstalledPackageProvenanceRecord(
    [property: JsonPropertyName("sourceKind")] InstalledPackageSourceKind SourceKind,
    [property: JsonPropertyName("versionPolicy")] InstalledPackageVersionPolicy VersionPolicy,
    [property: JsonPropertyName("registryOrigin")] string? RegistryOrigin = null,
    [property: JsonPropertyName("sourcePackageId")] string? SourcePackageId = null,
    [property: JsonPropertyName("requestedTag")] string? RequestedTag = null,
    [property: JsonPropertyName("requestedVersion")] string? RequestedVersion = null,
    [property: JsonPropertyName("versionRange")] string? VersionRange = null,
    [property: JsonPropertyName("sourceIdentity")] string? SourceIdentity = null,
    [property: JsonPropertyName("includePrerelease")] bool IncludePrerelease = false)
{
    public static InstalledPackageProvenanceRecord Unknown { get; } = new(
        InstalledPackageSourceKind.Unknown,
        InstalledPackageVersionPolicy.Unmanaged);

    public static InstalledPackageProvenanceRecord LocalArchive(string sourceIdentity) => new(
        InstalledPackageSourceKind.LocalArchive,
        InstalledPackageVersionPolicy.Unmanaged,
        SourceIdentity: sourceIdentity);

    public InstalledPackageProvenance ToDescriptor() => new(
        SourceKind,
        VersionPolicy,
        RegistryOrigin,
        SourcePackageId,
        RequestedTag,
        RequestedVersion,
        VersionRange,
        SourceIdentity,
        IncludePrerelease);
}

internal sealed record InstalledPackageContentRecord(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

internal sealed record InstalledPackageDependencyRecord(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("versionRange")] string VersionRange);

internal sealed record PreparedPackageArchiveMutation(
    string StagingPath,
    string InstalledPath,
    SunderPackageManifest Manifest,
    InstalledPackageRecord InstalledRecord,
    string ConsentManifestSha256,
    string ActiveManifestSha256,
    IReadOnlyList<string> SensitiveCapabilities);

internal sealed record PackageArchiveMutationPreparationResult(
    PreparedPackageArchiveMutation? Mutation,
    PackageOperationResult? Failure)
{
    public bool Success => Mutation is not null && Failure is null;
}
