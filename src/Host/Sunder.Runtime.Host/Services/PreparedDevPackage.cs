namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedDevPackage(
    string SourceFolder,
    Sunder.Protocol.PackageSourceDescriptor Source,
    string ShadowFolder,
    string LibraryFolder,
    string PackageId,
    string Version,
    DevPackageManifest Manifest,
    string EntryAssemblyPath,
    IReadOnlyList<string> Dependencies);
