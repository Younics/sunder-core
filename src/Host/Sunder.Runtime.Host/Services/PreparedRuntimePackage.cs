namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedRuntimePackage(
    string SourceFolder,
    Sunder.Protocol.PackageSourceDescriptor Source,
    string ShadowFolder,
    string LibraryFolder,
    string PackageId,
    string Version,
    RuntimePackageManifest Manifest,
    string EntryAssemblyPath,
    IReadOnlyList<string> Dependencies);
