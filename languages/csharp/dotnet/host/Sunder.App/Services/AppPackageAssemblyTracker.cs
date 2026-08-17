using System.Diagnostics;
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

    public void RemoveLoadContext(AssemblyLoadContext loadContext)
    {
        lock (_syncRoot)
        {
            foreach (var assembly in _assemblyPackageMap.Keys
                         .Where(assembly => ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), loadContext))
                         .ToArray())
            {
                _assemblyPackageMap.Remove(assembly);
            }
            _loadContextPackageMap.Remove(loadContext);
        }
    }

    public IReadOnlyList<(string PackageId, Assembly Assembly)> SnapshotPackageAssemblies()
    {
        lock (_syncRoot)
        {
            return _assemblyPackageMap
                .Select(entry => (entry.Value, entry.Key))
                .ToArray();
        }
    }

    public string? ResolvePackageId(Exception exception)
    {
        foreach (var current in EnumerateExceptions(exception))
        {
            var assembly = current.TargetSite?.DeclaringType?.Assembly;
            if (assembly is not null && TryResolvePackageId(assembly, out var packageId))
            {
                return packageId;
            }

            foreach (var frame in new StackTrace(current, fNeedFileInfo: false).GetFrames() ?? [])
            {
                assembly = frame.GetMethod()?.DeclaringType?.Assembly;
                if (assembly is not null && TryResolvePackageId(assembly, out packageId))
                {
                    return packageId;
                }
            }
        }

        return null;
    }

    public string? ResolveExclusivePackageId(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(Exception Exception, bool PathAttributed)>();
        pending.Push((exception, false));
        while (pending.TryPop(out var item))
        {
            var currentAttributed = CollectPackageIds(item.Exception, packageIds);
            if (packageIds.Count > 1)
            {
                return null;
            }

            var pathAttributed = item.PathAttributed || currentAttributed;
            if (item.Exception is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count == 0 && !pathAttributed)
                {
                    return null;
                }
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push((inner, pathAttributed));
                }
            }
            else if (item.Exception.InnerException is { } inner)
            {
                pending.Push((inner, pathAttributed));
            }
            else if (!pathAttributed)
            {
                return null;
            }
        }

        return packageIds.Count == 1 ? packageIds.Single() : null;
    }

    private bool CollectPackageIds(Exception exception, ISet<string> packageIds)
    {
        var attributed = false;
        var assembly = exception.TargetSite?.DeclaringType?.Assembly;
        if (assembly is not null && TryResolvePackageId(assembly, out var packageId))
        {
            packageIds.Add(packageId);
            attributed = true;
        }

        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames() ?? [])
        {
            assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly is not null && TryResolvePackageId(assembly, out packageId))
            {
                packageIds.Add(packageId);
                attributed = true;
            }
        }

        return attributed;
    }

    private bool TryResolvePackageId(Assembly assembly, out string packageId)
    {
        lock (_syncRoot)
        {
            if (_assemblyPackageMap.TryGetValue(assembly, out var resolvedPackageId))
            {
                packageId = resolvedPackageId;
                return true;
            }

            var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext is not null && _loadContextPackageMap.TryGetValue(loadContext, out resolvedPackageId))
            {
                packageId = resolvedPackageId;
                return true;
            }
        }

        packageId = string.Empty;
        return false;
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            yield return current;

            if (current is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.InnerExceptions)
                {
                    pending.Push(innerException);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
