using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.App.Services;

internal sealed class AppPackageAssemblyTracker
{
    private readonly Dictionary<Assembly, string> _assemblyPackageMap = [];
    private readonly Dictionary<AssemblyLoadContext, string> _loadContextPackageMap = [];
    private readonly object _syncRoot = new();

    public void RegisterPackageAssembly(string packageId, Assembly assembly)
    {
        lock (_syncRoot)
        {
            _assemblyPackageMap[assembly] = packageId;
            var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext is not null)
            {
                _loadContextPackageMap[loadContext] = packageId;
            }
        }
    }

    public void RemovePackage(string packageId)
    {
        lock (_syncRoot)
        {
            foreach (var assembly in _assemblyPackageMap.Where(entry => string.Equals(entry.Value, packageId, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
            {
                _assemblyPackageMap.Remove(assembly);
            }

            foreach (var loadContext in _loadContextPackageMap.Where(entry => string.Equals(entry.Value, packageId, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
            {
                _loadContextPackageMap.Remove(loadContext);
            }
        }
    }

    public string? ResolvePackageId(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var assembly = current.TargetSite?.DeclaringType?.Assembly;
            if (assembly is null)
            {
                continue;
            }

            lock (_syncRoot)
            {
                if (_assemblyPackageMap.TryGetValue(assembly, out var packageId))
                {
                    return packageId;
                }

                var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
                if (loadContext is not null && _loadContextPackageMap.TryGetValue(loadContext, out packageId))
                {
                    return packageId;
                }
            }
        }

        return null;
    }
}
