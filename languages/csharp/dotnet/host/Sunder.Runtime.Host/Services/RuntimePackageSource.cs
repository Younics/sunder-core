using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageSource(
    string PackageId,
    PackageSourceKind Kind,
    string SourceFolder,
    string? SnapshotFolder = null,
    PackageHostRoles HostRoles = PackageHostRoles.App | PackageHostRoles.Runtime,
    IReadOnlyList<PackageDependencyDescriptor>? Dependencies = null,
    string? ContentIdentity = null,
    SunderPackageManifest? Manifest = null,
    SunderPackageContentIndex? ContentIndex = null,
    SunderPackageTargetKey? SelectedTargetKey = null,
    SunderPackageTargetManifest? SelectedTarget = null)
{
    // Host-owned snapshot lifetime; never serialized or part of package authority.
    internal RuntimeSessionDirectory? SnapshotDirectory { get; set; }
    internal RuntimeSessionDirectory? WorkerSnapshotDirectory { get; set; }
    // Comparison evidence produced only after strict development snapshot validation.
    // It never substitutes for ContentIdentity in policy, permits or RPC admission.
    internal string? DevelopmentWorkerContentIdentity { get; init; }
    internal string? RegistryPolicyOrigin { get; init; }
    internal string? RegistryPolicyContentIdentity { get; init; }
    internal InstalledPackagePolicyRecord? InstalledPolicy { get; init; }
    internal InstalledPackageCompatibilityRecord? Compatibility { get; init; }
    internal DevelopmentImportAuthority? DevelopmentImports { get; init; }
    internal IReadOnlyList<string> DevelopmentOmittedPackages { get; init; } = [];
    internal IReadOnlyList<SunderPackageTargetKey>? AcquiredTargets { get; init; }

    internal string Folder => SourceFolder;

    internal string EffectiveSnapshotFolder => string.IsNullOrWhiteSpace(SnapshotFolder) ? SourceFolder : SnapshotFolder;

    // Optional contract/artifact presence participates in compatibility evidence,
    // not Worker startup/recovery liveness. Do not turn an optional Agent import
    // into a requirement that Maker must stop when Agent becomes unavailable.
    internal IReadOnlyList<PackageDependencyDescriptor> PackageDependencies => (Dependencies ?? [])
        .Where(dependency => !(Manifest?.DependsOn ?? []).Any(declaration => declaration.PackageId == dependency.PackageId
            && declaration.Presence == "optional")).ToArray();

    internal RuntimePackageSource AsCommittedInstalledSource()
        => Kind == PackageSourceKind.Installed
            ? this with { SnapshotFolder = null }
            : this;
}

internal sealed record RuntimePackageTargetSource(
    RuntimePackageSource Source,
    SunderPackageTargetKey TargetKey,
    SunderPackageTargetManifest Target);
