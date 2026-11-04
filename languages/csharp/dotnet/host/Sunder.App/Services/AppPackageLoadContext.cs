using System.Reflection;
using Sunder.Package.Hosting;

namespace Sunder.App.Services;

internal sealed class AppPackageLoadContext(
    string packageId,
    string entryAssemblyPath,
    string runtimeIdentifier,
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    Action<string, Assembly> registerPackageAssembly)
    : CollectiblePackageLoadContext(
        $"Sunder.App.Package.{packageId}.{Guid.NewGuid():N}",
        entryAssemblyPath,
        runtimeIdentifier,
        sharedAssemblyRegistry.ResolveSharedAssembly,
        assembly => registerPackageAssembly(packageId, assembly))
{
    internal Assembly ResolveResourceAssembly(string name)
        => sharedAssemblyRegistry.ResolveResourceAssembly(name) ?? LoadFromAssemblyName(new AssemblyName(name));
}
