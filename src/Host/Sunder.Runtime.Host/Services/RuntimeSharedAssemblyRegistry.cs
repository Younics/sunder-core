using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Hosting;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSharedAssemblyRegistry : IDisposable
{
    private readonly Dictionary<string, Assembly> _hostSharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        [typeof(IServiceCollection).Assembly.GetName().Name!] = typeof(IServiceCollection).Assembly,
        [typeof(ILoggerFactory).Assembly.GetName().Name!] = typeof(ILoggerFactory).Assembly,
        [typeof(ISunderRuntimePackageModule).Assembly.GetName().Name!] = typeof(ISunderRuntimePackageModule).Assembly,
        [typeof(IPackageStackExporter).Assembly.GetName().Name!] = typeof(IPackageStackExporter).Assembly,
    };

    private readonly HostSharedAssemblyRegistryCore _core;

    public RuntimeSharedAssemblyRegistry(IEnumerable<string> probeDirectories)
    {
        _core = new HostSharedAssemblyRegistryCore(_hostSharedAssemblies);
    }

    public Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
        => _core.ResolveSharedAssembly(assemblyName);

    internal static bool IsSharedAssemblyReferenceSatisfiedBy(
        AssemblyName requestedAssemblyName,
        AssemblyName loadedAssemblyName)
        => HostSharedAssemblyRegistryCore.IsReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName);

    public void Dispose() => _core.Dispose();

}
