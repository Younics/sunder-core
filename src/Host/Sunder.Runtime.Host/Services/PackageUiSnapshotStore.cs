using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageUiSnapshotLease(
    FileStream stream,
    string contentHash,
    long length) : IDisposable, IAsyncDisposable
{
    private FileStream? _stream = stream;

    public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(PackageUiSnapshotLease));

    public string ContentHash { get; } = contentHash;

    public long Length { get; } = length;

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stream, null) is { } owned) await owned.DisposeAsync();
    }
}

internal sealed class PackageUiSnapshotStore : IDisposable
{
    internal const long MaxUncompressedBytes = 256L * 1024 * 1024;
    internal const int MaxFileCount = 20_000;
    private readonly ConcurrentDictionary<string, SnapshotAlias> _aliases = new(StringComparer.Ordinal);
    private readonly PackageUiSnapshotObjectCache _objects;
    private readonly object _promotionGate = new();
    private int _garbageCollectionScheduled;

    public PackageUiSnapshotStore(
        RuntimePackagePaths paths,
        ILogger<PackageUiSnapshotStore>? logger = null)
    {
        Directory.CreateDirectory(paths.TransferRootPath);
        _objects = new PackageUiSnapshotObjectCache(paths.CacheRootPath, logger);
    }

    public IReadOnlyList<PackageUiSnapshotDescriptor> CreateSnapshots(
        IReadOnlyList<RuntimePackageTargetSource> sources,
        long generation,
        string? stageId = null)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var created = new PackageUiSnapshotDescriptor?[sources.Count];
        try
        {
            Parallel.For(
                0,
                sources.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4),
                },
                index => created[index] = CreateSnapshot(sources[index], generation, stageId));
            return created.Select(static snapshot => snapshot!).ToArray();
        }
        catch
        {
            RemoveSnapshots(created.OfType<PackageUiSnapshotDescriptor>());
            throw;
        }
    }

    public PackageUiSnapshotLease? Acquire(string snapshotId, long generation, string? stageId)
    {
        if (!_aliases.TryGetValue(snapshotId, out var alias)
            || alias.Generation != generation
            || !string.Equals(alias.StageId, stageId, StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            var stream = new FileStream(
                alias.ObjectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new PackageUiSnapshotLease(stream, alias.ContentHash, alias.Length);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _aliases.TryRemove(new KeyValuePair<string, SnapshotAlias>(snapshotId, alias));
            return null;
        }
    }

    public void RemoveStage(string stageId)
    {
        foreach (var (id, alias) in _aliases)
        {
            if (string.Equals(alias.StageId, stageId, StringComparison.Ordinal)) Remove(id);
        }
    }

    public IReadOnlyList<PackageUiSnapshotDescriptor> PromoteStage(
        string stageId,
        IReadOnlyList<PackageUiSnapshotDescriptor> descriptors)
    {
        lock (_promotionGate)
        {
            var aliases = descriptors.Select(descriptor =>
            {
                if (!_aliases.TryGetValue(descriptor.SnapshotId, out var alias)
                    || !string.Equals(alias.StageId, stageId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Package UI snapshot '{descriptor.SnapshotId}' does not belong to stage '{stageId}'.");
                }
                return (Descriptor: descriptor, Alias: alias);
            }).ToArray();

            foreach (var item in aliases) _aliases[item.Descriptor.SnapshotId] = item.Alias with { StageId = null };
            return descriptors.Select(descriptor => descriptor with
            {
                SnapshotUri = $"packages/ui-snapshots/{descriptor.SnapshotId}",
            }).ToArray();
        }
    }

    public void RemoveOlderGenerations(long generation)
    {
        foreach (var (id, alias) in _aliases)
        {
            if (alias.StageId is null && alias.Generation != generation) Remove(id);
        }
    }

    public void RemoveSnapshots(IEnumerable<PackageUiSnapshotDescriptor> snapshots)
    {
        foreach (var snapshot in snapshots) Remove(snapshot.SnapshotId);
    }

    public void ScheduleGarbageCollection()
    {
        if (Interlocked.Exchange(ref _garbageCollectionScheduled, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                _objects.CollectGarbage(_aliases.Values.Select(static alias => alias.ContentHash).ToHashSet(StringComparer.Ordinal));
            }
            finally
            {
                Volatile.Write(ref _garbageCollectionScheduled, 0);
            }
        });
    }

    public void Dispose() => _aliases.Clear();

    private PackageUiSnapshotDescriptor CreateSnapshot(RuntimePackageTargetSource targetSource, long generation, string? stageId)
    {
        var source = targetSource.Source;
        var snapshotObject = _objects.GetOrCreate(targetSource);
        var snapshotId = Guid.NewGuid().ToString("N");
        _aliases[snapshotId] = new SnapshotAlias(
            snapshotObject.Path,
            snapshotObject.ContentHash,
            snapshotObject.Length,
            generation,
            stageId);
        var uri = stageId is null
            ? $"packages/ui-snapshots/{snapshotId}"
            : $"packages/session/stage/{stageId}/ui-snapshots/{snapshotId}";
        return new PackageUiSnapshotDescriptor(
            source.PackageId,
            source.Kind,
            generation,
            PackageTargetSelection.ToDescriptor(targetSource.Target),
            snapshotObject.ContentHash,
            snapshotId,
            uri);
    }

    private void Remove(string id) => _aliases.TryRemove(id, out _);

    private sealed record SnapshotAlias(
        string ObjectPath,
        string ContentHash,
        long Length,
        long Generation,
        string? StageId);
}
