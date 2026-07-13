namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedRuntimePackage(
    string SourceFolder,
    RuntimePackageSource Source,
    string ShadowFolder,
    string LibraryFolder,
    string PackageId,
    string Version,
    RuntimePackageActivationState Activation,
    string EntryAssemblyPath,
    IReadOnlyList<string> Dependencies);
