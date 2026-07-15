using Sunder.Package.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageLoadContext(
    string packageId,
    string entryAssemblyPath,
    RuntimeSharedAssemblyRegistry sharedAssemblyRegistry)
    : CollectiblePackageLoadContext(
        $"Sunder.RuntimePackage.{packageId}.{Guid.NewGuid():N}",
        entryAssemblyPath,
        sharedAssemblyRegistry.ResolveSharedAssembly);
