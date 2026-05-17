using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class AppPackageHostState(
    HashSet<string> disabledPackageIds,
    IReadOnlyList<object> ownedDisposables,
    IReadOnlyList<AppPackageLoadContext> loadContexts)
{
    private readonly Dictionary<string, AppLoadedPackageHandle> _loadedPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppPackageLoadContext> _loadContexts = loadContexts.ToList();
    private readonly List<object> _ownedDisposables = ownedDisposables.ToList();
    private readonly object _syncRoot = new();

    public int LoadedPackageCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _loadedPackages.Count;
            }
        }
    }

    public int OwnedDisposableCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _ownedDisposables.Count;
            }
        }
    }

    public int LoadContextCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _loadContexts.Count;
            }
        }
    }

    public IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        lock (_syncRoot)
        {
            return activePackages
                .Where(package => !disabledPackageIds.Contains(package.PackageId))
                .ToArray();
        }
    }

    public string[] SnapshotLoadedPackageIds()
    {
        lock (_syncRoot)
        {
            return _loadedPackages.Keys.ToArray();
        }
    }

    public string[] SnapshotLoadedPackageIds(Func<string, bool> predicate)
    {
        lock (_syncRoot)
        {
            return _loadedPackages.Keys.Where(predicate).ToArray();
        }
    }

    public AppLoadedPackageHandle? GetLoadedPackage(string packageId)
    {
        lock (_syncRoot)
        {
            return _loadedPackages.TryGetValue(packageId, out var loadedPackage) ? loadedPackage : null;
        }
    }

    public void SetLoadedPackage(string packageId, AppLoadedPackageHandle loadedPackage)
    {
        lock (_syncRoot)
        {
            disabledPackageIds.Remove(packageId);
            _loadedPackages[packageId] = loadedPackage;
        }
    }

    public bool TryRemoveLoadedPackage(string packageId, bool preserveDisabled, out AppLoadedPackageHandle? handle)
    {
        lock (_syncRoot)
        {
            if (!_loadedPackages.Remove(packageId, out handle))
            {
                return false;
            }

            if (!preserveDisabled)
            {
                disabledPackageIds.Remove(packageId);
            }

            return true;
        }
    }

    public bool TryMarkPackageDisabled(string packageId)
    {
        lock (_syncRoot)
        {
            return disabledPackageIds.Add(packageId);
        }
    }

    public bool IsPackageDisabled(string packageId)
    {
        lock (_syncRoot)
        {
            return disabledPackageIds.Contains(packageId);
        }
    }

    public (object[] OwnedDisposables, AppPackageLoadContext[] LoadContexts) SnapshotLegacyResources()
    {
        lock (_syncRoot)
        {
            return (_ownedDisposables.ToArray(), _loadContexts.ToArray());
        }
    }

    public void TrackOwnedDisposable(object ownedDisposable)
    {
        lock (_syncRoot)
        {
            _ownedDisposables.Add(ownedDisposable);
        }
    }

    public void TrackLoadContext(AppPackageLoadContext loadContext)
    {
        lock (_syncRoot)
        {
            _loadContexts.Add(loadContext);
        }
    }

    public void RemoveOwnedDisposable(object ownedDisposable)
    {
        lock (_syncRoot)
        {
            _ownedDisposables.Remove(ownedDisposable);
        }
    }

    public void RemoveLoadContext(AppPackageLoadContext loadContext)
    {
        lock (_syncRoot)
        {
            _loadContexts.Remove(loadContext);
        }
    }
}
