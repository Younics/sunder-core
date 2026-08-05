using Sunder.Package.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class RuntimePackageLoadContext(
    string packageId,
    string entryAssemblyPath,
    string runtimeIdentifier,
    RuntimeSharedAssemblyRegistry sharedAssemblyRegistry)
    : CollectiblePackageLoadContext(
        $"Sunder.RuntimePackage.{packageId}.{Guid.NewGuid():N}",
        entryAssemblyPath,
        runtimeIdentifier,
        sharedAssemblyRegistry.ResolveSharedAssembly);

internal sealed partial class RuntimePackageLoadContext
{
    public RuntimePackageLoadContext(
        string packageId,
        string entryAssemblyPath,
        RuntimeSharedAssemblyRegistry sharedAssemblyRegistry)
        : this(
            packageId,
            entryAssemblyPath,
            PackageTargetSelection.GetCurrentRuntimeIdentifier(),
            sharedAssemblyRegistry)
    {
    }
}
