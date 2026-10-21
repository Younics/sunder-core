using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageHostState(
    HashSet<string> disabledPackageIds,
    IReadOnlyList<object> ownedDisposables,
    IReadOnlyList<AppPackageLoadContext> loadContexts)
{
    private readonly Dictionary<string, AppLoadedPackageHandle> _loadedPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packageContentHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activatedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppPackageLoadContext> _loadContexts = loadContexts.ToList();
    private readonly List<object> _ownedDisposables = ownedDisposables.ToList();
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, AppPackageHostState> _retained = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ActivePackageDescriptor Package, PackageUiSnapshotDescriptor Source)> _retainedSources = new(StringComparer.OrdinalIgnoreCase);
    internal void RetainPackage(string packageId, AppPackageHostState owner)
    {
        lock (_syncRoot) { _retained.Add(packageId, owner); _activatedPackageIds.Add(packageId); }
    }
    internal void DetachRetainedPackages() { lock (_syncRoot) { _retained.Clear(); _retainedSources.Clear(); } }
    // Candidate-local source metadata is published/rolled back with its generation.
    // The birth owner keeps the original bytes, provider, lease and caller lifetime.
    internal void StageRetainedSource(ActivePackageDescriptor package, PackageUiSnapshotDescriptor source)
    {
        lock (_syncRoot) _retainedSources.Add(package.PackageId, (package, source));
    }
    internal string[] SnapshotOwnedPackageIds() { lock (_syncRoot) return _loadedPackages.Keys.ToArray(); }

    public int LoadedPackageCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _loadedPackages.Count + _retained.Count(pair => pair.Value.GetLoadedPackage(pair.Key) is not null);
            }
        }
    }

    public int OwnedDisposableCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _ownedDisposables.Count + _retained.Count(pair => pair.Value.GetLoadedPackage(pair.Key)?.ServiceProvider is not null);
            }
        }
    }

    public int LoadContextCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _loadContexts.Count + _retained.Count(pair => pair.Value.GetLoadedPackage(pair.Key)?.LoadContext is not null);
            }
        }
    }

    public IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        lock (_syncRoot)
        {
            return activePackages
                .Where(package => !IsPackageDisabled(package.PackageId))
                .ToArray();
        }
    }

    public HashSet<string> SnapshotDisabledPackageIds()
    {
        lock (_syncRoot)
        {
            return disabledPackageIds.Concat(_retained.Where(pair => pair.Value.IsPackageDisabled(pair.Key)).Select(pair => pair.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public string[] SnapshotLoadedPackageIds()
    {
        lock (_syncRoot)
        {
            return _loadedPackages.Keys.Concat(_retained.Keys).ToArray();
        }
    }

    public string[] SnapshotLoadedPackageIds(Func<string, bool> predicate)
    {
        lock (_syncRoot)
        {
            return SnapshotLoadedPackageIds().Where(predicate).ToArray();
        }
    }

    public AppLoadedPackageHandle? GetLoadedPackage(string packageId)
    {
        lock (_syncRoot)
        {
            if (_loadedPackages.TryGetValue(packageId, out var loadedPackage)) return loadedPackage;
            var retained = _retained.GetValueOrDefault(packageId)?.GetLoadedPackage(packageId);
            return retained is not null && _retainedSources.TryGetValue(packageId, out var current)
                ? retained with { Package = current.Package, Source = current.Source } : retained;
        }
    }

    public void SetLoadedPackage(string packageId, AppLoadedPackageHandle loadedPackage)
    {
        lock (_syncRoot)
        {
            disabledPackageIds.Remove(packageId);
            _activatedPackageIds.Add(packageId);
            _loadedPackages[packageId] = loadedPackage;
            _packageContentHashes[packageId] = loadedPackage.Source.ContentHash;
        }
    }

    public void SetPackageContentHash(string packageId, string contentHash)
    {
        lock (_syncRoot)
        {
            _packageContentHashes[packageId] = contentHash;
        }
    }

    public string? GetPackageContentHash(string packageId)
    {
        lock (_syncRoot)
        {
            return _packageContentHashes.GetValueOrDefault(packageId);
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
                _packageContentHashes.Remove(packageId);
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
            return disabledPackageIds.Contains(packageId) || _retained.GetValueOrDefault(packageId)?.IsPackageDisabled(packageId) == true;
        }
    }

    public bool HasActivatedPackage(string packageId)
    {
        lock (_syncRoot)
        {
            return _activatedPackageIds.Contains(packageId);
        }
    }

    public (object[] OwnedDisposables, AppPackageLoadContext[] LoadContexts) SnapshotOwnedResources()
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

    public IReadOnlyList<WeakReference> SnapshotLoadContextWeakReferences()
    {
        lock (_syncRoot)
        {
            return _loadContexts.Select(loadContext => new WeakReference(loadContext))
                .Concat(_retained.Select(pair => pair.Value.GetLoadedPackage(pair.Key)?.LoadContext).OfType<AppPackageLoadContext>().Select(context => new WeakReference(context))).ToArray();
        }
    }
}
