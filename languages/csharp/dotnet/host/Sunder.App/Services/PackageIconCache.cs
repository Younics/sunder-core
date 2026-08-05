using Avalonia.Media;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class PackageIconCache : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Func<string, string?> _getPackageContentRoot;
    private readonly Func<string, CancellationToken, Task<PackageIconImageLoadResult>> _loadImageAsync;
    private readonly Dictionary<PackageIconGenerationStamp, Lazy<Task<PackageIconGeneration>>> _prewarms = [];
    private PackageIconGeneration _current = PackageIconGeneration.Empty();
    private bool _disposed;

    public PackageIconCache(Func<string, string?> getPackageContentRoot)
        : this(getPackageContentRoot, PackageIconImageLoader.LoadFileAsync)
    {
    }

    internal PackageIconCache(
        Func<string, string?> getPackageContentRoot,
        Func<string, CancellationToken, Task<PackageIconImageLoadResult>> loadImageAsync)
    {
        _getPackageContentRoot = getPackageContentRoot;
        _loadImageAsync = loadImageAsync;
    }

    public async Task PrewarmAsync(
        Guid runtimeInstanceId,
        long generation,
        IEnumerable<(string PackageId, PackageIconDescriptor? Icon)> icons,
        CancellationToken cancellationToken)
    {
        var stamp = ValidateStamp(runtimeInstanceId, generation);
        Lazy<Task<PackageIconGeneration>> prewarm;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_prewarms.TryGetValue(stamp, out prewarm!))
            {
                var iconSnapshot = icons.ToArray();
                prewarm = new Lazy<Task<PackageIconGeneration>>(
                    () => PrepareGenerationAsync(stamp, iconSnapshot, _getPackageContentRoot, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _prewarms.Add(stamp, prewarm);
            }
        }

        var prepared = await prewarm.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        PackageIconGeneration? retired = null;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_current, prepared))
            {
                return;
            }
            if (_current.Stamp.RuntimeInstanceId == stamp.RuntimeInstanceId
                && _current.Stamp.Generation > stamp.Generation)
            {
                throw new InvalidOperationException("Cannot prewarm a stale package icon generation.");
            }

            retired = _current;
            _current = prepared;
        }

        retired.Dispose();
    }

    public IImage? GetImage(string packageId, PackageIconDescriptor? icon)
        => Volatile.Read(ref _current).GetImage(packageId, icon);

    internal Task<PackageIconGeneration> PrepareGenerationAsync(
        Guid runtimeInstanceId,
        long generation,
        IEnumerable<(string PackageId, PackageIconDescriptor? Icon)> icons,
        Func<string, string?> getPackageContentRoot,
        CancellationToken cancellationToken)
        => PrepareGenerationAsync(
            ValidateStamp(runtimeInstanceId, generation),
            icons,
            getPackageContentRoot,
            cancellationToken);

    internal PackageIconGeneration Publish(PackageIconGeneration generation)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _current;
            _current = generation;
            return previous;
        }
    }

    internal void Restore(PackageIconGeneration generation)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current = generation;
        }
    }

    public void Dispose()
    {
        PackageIconGeneration current;
        Lazy<Task<PackageIconGeneration>>[] prewarms;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _current;
            prewarms = _prewarms.Values.Where(load => load.IsValueCreated).ToArray();
            _prewarms.Clear();
        }

        current.Dispose();
        foreach (var prewarm in prewarms)
        {
            if (prewarm.Value.IsCompletedSuccessfully)
            {
                prewarm.Value.Result.Dispose();
                continue;
            }

            _ = prewarm.Value.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        task.Result.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<PackageIconGeneration> PrepareGenerationAsync(
        PackageIconGenerationStamp stamp,
        IEnumerable<(string PackageId, PackageIconDescriptor? Icon)> icons,
        Func<string, string?> getPackageContentRoot,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<PackageIconKey, PackageIconImageLoadResult>();
        try
        {
            foreach (var item in icons
                         .Where(item => !string.IsNullOrWhiteSpace(item.Icon?.AssetPath))
                         .DistinctBy(item => new PackageIconKey(item.PackageId, item.Icon!.AssetPath!)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = new PackageIconKey(item.PackageId, item.Icon!.AssetPath!);
                results.Add(
                    key,
                    await LoadCoreAsync(
                        item.PackageId,
                        item.Icon.AssetPath!,
                        getPackageContentRoot,
                        cancellationToken).ConfigureAwait(false));
            }

            return new PackageIconGeneration(stamp, results);
        }
        catch
        {
            foreach (var result in results.Values)
            {
                DisposeImage(result.Image);
            }
            throw;
        }
    }

    private async Task<PackageIconImageLoadResult> LoadCoreAsync(
        string packageId,
        string assetPath,
        Func<string, string?> getPackageContentRoot,
        CancellationToken cancellationToken)
    {
        var contentRoot = getPackageContentRoot(packageId);
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            return PackageIconImageLoadResult.Failed($"Package icon content is unavailable for '{packageId}'.");
        }

        var root = Path.GetFullPath(contentRoot) + Path.DirectorySeparatorChar;
        var iconPath = Path.GetFullPath(Path.Combine(contentRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!iconPath.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(iconPath))
        {
            var error = $"Package icon asset '{assetPath}' is unavailable for '{packageId}'.";
            AppSessionLog.WriteError(error);
            return PackageIconImageLoadResult.Failed(error);
        }

        return await _loadImageAsync(iconPath, cancellationToken).ConfigureAwait(false);
    }

    private static PackageIconGenerationStamp ValidateStamp(Guid runtimeInstanceId, long generation)
    {
        if (runtimeInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Runtime instance id is required.", nameof(runtimeInstanceId));
        }

        return new PackageIconGenerationStamp(runtimeInstanceId, generation);
    }

    internal static void DisposeImage(IImage? image)
    {
        if (image is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal sealed class PackageIconGeneration(
    PackageIconGenerationStamp stamp,
    IReadOnlyDictionary<PackageIconKey, PackageIconImageLoadResult> images) : IDisposable
{
    private int _disposed;

    public PackageIconGenerationStamp Stamp { get; } = stamp;

    public static PackageIconGeneration Empty()
        => new(new PackageIconGenerationStamp(Guid.Empty, -1), new Dictionary<PackageIconKey, PackageIconImageLoadResult>());

    public IImage? GetImage(string packageId, PackageIconDescriptor? icon)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(icon?.AssetPath))
        {
            return null;
        }

        return images.TryGetValue(new PackageIconKey(packageId, icon.AssetPath!), out var result)
            ? result.Image
            : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var result in images.Values)
        {
            PackageIconCache.DisposeImage(result.Image);
        }
    }
}

internal readonly record struct PackageIconGenerationStamp(Guid RuntimeInstanceId, long Generation);

internal readonly record struct PackageIconKey(string PackageId, string AssetPath)
{
    public bool Equals(PackageIconKey other)
        => string.Equals(PackageId, other.PackageId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(AssetPath, other.AssetPath, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(PackageId),
            StringComparer.Ordinal.GetHashCode(AssetPath));
}
