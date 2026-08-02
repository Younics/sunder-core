using System.Text.Json.Serialization;
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
    [property: JsonPropertyName("provenance")] InstalledPackageProvenanceRecord? Provenance = null);

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
    InstalledPackageRecord InstalledRecord);

internal sealed record PackageArchiveMutationPreparationResult(
    PreparedPackageArchiveMutation? Mutation,
    PackageOperationResult? Failure)
{
    public bool Success => Mutation is not null && Failure is null;
}
