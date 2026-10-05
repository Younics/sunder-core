using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sunder.Package.Projections;
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

internal interface IAppPolicySnapshotSource
{
    RuntimePackageSource? PolicySource(string snapshotId);
    bool IsPublished(string snapshotId) => true;
}

internal sealed class PackageUiSnapshotStore : IDisposable, IAppPolicySnapshotSource
{
    internal const long MaxUncompressedBytes = 256L * 1024 * 1024;
    internal const int MaxFileCount = 20_000;
    private readonly ConcurrentDictionary<string, SnapshotAlias> _aliases = new(StringComparer.Ordinal);
    private readonly PackageUiSnapshotObjectCache _objects;
    private readonly PackageProjectionCache _projections;
    private readonly Guid _hostInstanceId;
    private readonly object _promotionGate = new();
    private int _garbageCollectionScheduled;

    public PackageUiSnapshotStore(
        RuntimePackagePaths paths,
        ILogger<PackageUiSnapshotStore>? logger = null,
        PackageProjectionCache? projections = null,
        Guid? hostInstanceId = null)
    {
        Directory.CreateDirectory(paths.TransferRootPath);
        _objects = new PackageUiSnapshotObjectCache(paths.CacheRootPath, logger);
        _projections = projections ?? new PackageProjectionCache(paths.RootPath);
        _hostInstanceId = hostInstanceId is { } instanceId && instanceId != Guid.Empty
            ? instanceId
            : Guid.NewGuid();
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
            if (alias.ObjectPath is null)
            {
                return null;
            }
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
            if (string.Equals(alias.StageId, stageId, StringComparison.Ordinal)) Remove(id, force: true);
        }
    }

    public RuntimePackageSource? PolicySource(string snapshotId)
        => _aliases.TryGetValue(snapshotId, out var alias) ? alias.PolicySource : null;
    public bool IsPublished(string snapshotId) => _aliases.TryGetValue(snapshotId, out var alias) && alias.StageId is null;

    internal static RuntimePackagePolicySubject? PolicySubject(RuntimePackageSource source)
        => source.Manifest?.Version is { } version && (source.RegistryPolicyContentIdentity ?? source.ContentIdentity) is { } content
            ? new(source.PackageId, version, content) : null;

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
                SnapshotUri = descriptor.ProjectionReference is null
                    ? $"packages/ui-snapshots/{descriptor.SnapshotId}"
                    : descriptor.SnapshotUri,
            }).ToArray();
        }
    }

    public void RemoveOlderGenerations(long generation)
    {
        foreach (var (id, alias) in _aliases)
        {
            if (alias.StageId is null && alias.Generation != generation) Remove(id, force: true);
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
                _objects.CollectGarbage(_aliases.Values
                    .Where(static alias => alias.ObjectPath is not null)
                    .Select(static alias => alias.ContentHash)
                    .ToHashSet(StringComparer.Ordinal));
            }
            finally
            {
                Volatile.Write(ref _garbageCollectionScheduled, 0);
            }
        });
    }

    public void Dispose()
    {
        foreach (var id in _aliases.Keys)
        {
            Remove(id, force: true);
        }
    }

    private PackageUiSnapshotDescriptor CreateSnapshot(RuntimePackageTargetSource targetSource, long generation, string? stageId)
    {
        var source = targetSource.Source;
        if (source.Kind == PackageSourceKind.Installed)
        {
            return CreateInstalledProjectionSnapshot(targetSource, generation, stageId);
        }
        var snapshotObject = _objects.GetOrCreate(targetSource);
        var snapshotId = Guid.NewGuid().ToString("N");
        _aliases[snapshotId] = new SnapshotAlias(
            snapshotObject.Path,
            snapshotObject.ContentHash,
            snapshotObject.Length,
            generation,
            stageId,
            PolicySource: source);
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
            uri) { PolicySubject = PolicySubject(source) };
    }

    private PackageUiSnapshotDescriptor CreateInstalledProjectionSnapshot(
        RuntimePackageTargetSource targetSource,
        long generation,
        string? stageId)
    {
        var source = targetSource.Source;
        var request = PackageProjectionRequestFactory.CreateApp(targetSource);
        lock (_promotionGate)
        {
            foreach (var (existingId, existing) in _aliases)
            {
                if (Matches(existing, source.PackageId, generation, stageId, request))
                {
                    _aliases[existingId] = existing with
                    {
                        ReferenceCount = existing.ReferenceCount == int.MaxValue
                            ? int.MaxValue
                            : existing.ReferenceCount + 1,
                    };
                    return CreateInstalledDescriptor(targetSource, generation, existingId, existing);
                }
            }

            var acquisition = _projections.GetOrBuildAndLeaseAsync(
                    request,
                    new PackageProjectionOwner(
                        "runtime-host-app-source",
                        _hostInstanceId,
                        source.PackageId,
                        stageId ?? generation.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .GetAwaiter()
                .GetResult();
            var snapshotId = Guid.NewGuid().ToString("N");
            try
            {
                var alias = new SnapshotAlias(
                    ObjectPath: null,
                    acquisition.Reference.CacheKey,
                    Length: 0,
                    generation,
                    stageId,
                    acquisition,
                    source.PackageId,
                    PolicySource: source);
                _aliases[snapshotId] = alias;
                return CreateInstalledDescriptor(targetSource, generation, snapshotId, alias);
            }
            catch
            {
                acquisition.Dispose();
                throw;
            }
        }
    }

    private static bool Matches(
        SnapshotAlias alias,
        string packageId,
        long generation,
        string? stageId,
        PackageProjectionRequest request)
        => alias.Projection is { } projection
           && alias.Generation == generation
           && string.Equals(alias.StageId, stageId, StringComparison.Ordinal)
           && string.Equals(alias.PackageId, packageId, StringComparison.Ordinal)
           && projection.Reference.ConsumerRole == request.ConsumerRole
           && string.Equals(
               projection.Reference.SourceContentIdentity,
               request.SourceContentIdentity,
               StringComparison.Ordinal)
           && string.Equals(
               projection.Reference.ProjectionRulesIdentity,
               request.ProjectionRulesIdentity,
               StringComparison.Ordinal)
           && projection.Reference.TargetKeys.SequenceEqual(request.TargetKeys, StringComparer.Ordinal);

    private static PackageUiSnapshotDescriptor CreateInstalledDescriptor(
        RuntimePackageTargetSource targetSource,
        long generation,
        string snapshotId,
        SnapshotAlias alias)
        => new(
            targetSource.Source.PackageId,
            targetSource.Source.Kind,
            generation,
            PackageTargetSelection.ToDescriptor(targetSource.Target),
            alias.ContentHash,
            snapshotId,
            SnapshotUri: string.Empty)
        {
            ProjectionReference = ToDescriptor(alias.Projection!.Reference),
            PolicySubject = PolicySubject(targetSource.Source),
        };

    private void Remove(string id, bool force = false)
    {
        lock (_promotionGate)
        {
            if (!_aliases.TryGetValue(id, out var alias))
            {
                return;
            }
            if (!force && alias.ReferenceCount > 1)
            {
                _aliases[id] = alias with { ReferenceCount = alias.ReferenceCount - 1 };
                return;
            }
            alias.Projection?.Dispose();
            _aliases.TryRemove(new KeyValuePair<string, SnapshotAlias>(id, alias));
        }
    }

    private static PackageUiProjectionReferenceDescriptor ToDescriptor(PackageProjectionReference reference)
        => new(
            reference.FormatRevision,
            reference.CacheKey,
            PackageProjectionLayoutRole(reference.ConsumerRole),
            reference.SourceContentIdentity,
            reference.ProjectionRulesIdentity,
            reference.TargetKeys);

    private static string PackageProjectionLayoutRole(PackageProjectionRole role)
        => role switch
        {
            PackageProjectionRole.App => "app",
            PackageProjectionRole.Runtime => "runtime",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private sealed record SnapshotAlias(
        string? ObjectPath,
        string ContentHash,
        long Length,
        long Generation,
        string? StageId,
        PackageProjectionAcquisition? Projection = null,
        string? PackageId = null,
        int ReferenceCount = 1,
        RuntimePackageSource? PolicySource = null);
}
