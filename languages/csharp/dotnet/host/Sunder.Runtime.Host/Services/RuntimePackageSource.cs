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
    internal string? RegistryPolicyOrigin { get; init; }
    internal string? RegistryPolicyContentIdentity { get; init; }
    internal InstalledPackagePolicyRecord? InstalledPolicy { get; init; }
    internal InstalledPackageCompatibilityRecord? Compatibility { get; init; }
    internal DevelopmentImportAuthority? DevelopmentImports { get; init; }
    internal IReadOnlyList<string> DevelopmentOmittedPackages { get; init; } = [];
    internal IReadOnlyList<SunderPackageTargetKey>? AcquiredTargets { get; init; }

    internal string Folder => SourceFolder;

    internal string EffectiveSnapshotFolder => string.IsNullOrWhiteSpace(SnapshotFolder) ? SourceFolder : SnapshotFolder;

    internal IReadOnlyList<PackageDependencyDescriptor> PackageDependencies => Dependencies ?? [];

    internal RuntimePackageSource AsCommittedInstalledSource()
        => Kind == PackageSourceKind.Installed
            ? this with { SnapshotFolder = null }
            : this;
}

internal sealed record RuntimePackageTargetSource(
    RuntimePackageSource Source,
    SunderPackageTargetKey TargetKey,
    SunderPackageTargetManifest Target);
