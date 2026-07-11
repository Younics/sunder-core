using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageSource(
    string PackageId,
    PackageSourceKind Kind,
    string SourceFolder,
    string? SnapshotFolder = null)
{
    internal string Folder => SourceFolder;

    internal string EffectiveSnapshotFolder => string.IsNullOrWhiteSpace(SnapshotFolder) ? SourceFolder : SnapshotFolder;
}
