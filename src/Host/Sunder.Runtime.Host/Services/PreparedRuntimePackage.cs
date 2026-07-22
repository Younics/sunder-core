using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedRuntimePackage(
    string SourceFolder,
    RuntimePackageSource Source,
    string ShadowFolder,
    string LibraryFolder,
    string PackageId,
    string Version,
    PackageHostRoles HostRoles,
    RuntimePackageActivationState Activation,
    string EntryAssemblyPath,
    IReadOnlyList<PackageDependencyDescriptor> Dependencies);
