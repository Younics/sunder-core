using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageSource(
    string PackageId,
    PackageSourceKind Kind,
    string SourceFolder,
    string? SnapshotFolder = null,
    PackageHostRoles HostRoles = PackageHostRoles.App | PackageHostRoles.Runtime,
    IReadOnlyList<string>? Dependencies = null)
{
    internal string Folder => SourceFolder;

    internal string EffectiveSnapshotFolder => string.IsNullOrWhiteSpace(SnapshotFolder) ? SourceFolder : SnapshotFolder;

    internal IReadOnlyList<string> PackageDependencies => Dependencies ?? [];
}
