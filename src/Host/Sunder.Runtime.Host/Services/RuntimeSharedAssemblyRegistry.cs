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

    private readonly SharedContractAssemblyRegistryCore _core;
    private readonly Dictionary<string, string> _sharedAssemblyPaths;
    private readonly Dictionary<string, AssemblyName> _sharedAssemblyNames;

    public RuntimeSharedAssemblyRegistry(IEnumerable<string> probeDirectories)
    {
        _core = new SharedContractAssemblyRegistryCore("Runtime", _hostSharedAssemblies);
        _sharedAssemblyPaths = _core.SharedAssemblyPaths;
        _sharedAssemblyNames = _core.SharedAssemblyNames;
        _core.AddProbeDirectories(probeDirectories);
    }

    public Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
        => _core.ResolveSharedAssembly(assemblyName);

    internal static bool IsSharedAssemblyReferenceSatisfiedBy(
        AssemblyName requestedAssemblyName,
        AssemblyName loadedAssemblyName)
        => SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName);

    public void Dispose() => _core.Dispose();

    private void TryRegisterSharedAssemblyPath(AssemblyCandidate candidate, AssemblyName? requestedAssemblyName = null)
        => _core.RegisterCandidate(new SharedAssemblyCandidate(candidate.Path, candidate.Name), requestedAssemblyName);

    private readonly record struct AssemblyCandidate(string Path, AssemblyName Name);
}
