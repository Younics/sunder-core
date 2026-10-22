using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class PackageScopedBackgroundTaskQueue(
    string packageId,
    string packageDisplayName,
    BackgroundTaskQueueService backgroundTasks,
    Guid? ownerId = null)
    : IBackgroundTaskQueue, IDisposable, IAsyncDisposable
{
    private readonly string _packageId = packageId;
    private readonly string _packageDisplayName = packageDisplayName;
    private readonly BackgroundTaskQueueService _backgroundTasks = backgroundTasks;
    private readonly Guid? _ownerId = ownerId;
    private readonly object _lifecycleGate = new();
    private bool _disposed;
    private bool _started;

    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged;

    public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
        => Enqueue(request, Guid.NewGuid());

    internal BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request, Guid processId)
    {
        BackgroundTaskRequestValidator.Validate(request);

        var metadata = PackageScopedBackgroundTaskMetadata.Create(
            _packageId,
            _packageDisplayName,
            request.GroupKey,
            request.Metadata,
            _ownerId);

        BackgroundTaskSnapshot snapshot;
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            snapshot = _backgroundTasks.Enqueue(new BackgroundTaskRequest(
                request.Title,
                BuildHostGroupKey(_packageId, request.GroupKey, _ownerId),
                request.Indicator,
                request.ConcurrencyMode,
                request.CanCancel,
                async context => await request.ExecuteAsync(context).ConfigureAwait(false),
                metadata.ToHostMetadata()), processId);
        }

        return ToPackageSnapshot(snapshot, metadata);
    }

    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return [];
            }
        }

        return ListTasksCore(groupKey);
    }

    public bool Cancel(Guid processId)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        var snapshot = _backgroundTasks.GetTask(processId);
        return snapshot is not null && TryMap(snapshot, out _) && _backgroundTasks.Cancel(processId);
    }

    public void Dispose()
    {
        if (StopCore())
        {
            if (_ownerId is { } ownerId)
            {
                _backgroundTasks.CancelPackageOwnerProcesses(_packageId, ownerId);
            }
            else
            {
                _backgroundTasks.CancelOwnerlessPackageProcesses(_packageId);
            }
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (StopCore())
        {
            if (_ownerId is { } ownerId)
            {
                await _backgroundTasks.CancelPackageOwnerProcessesAsync(_packageId, ownerId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _backgroundTasks.CancelOwnerlessPackageProcessesAsync(_packageId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal void Start()
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            if (_started)
            {
                return;
            }

            _started = true;
            _backgroundTasks.TaskChanged += BackgroundTasks_OnTaskChanged;
        }
    }

    private void BackgroundTasks_OnTaskChanged(object? sender, BackgroundTaskChangedEventArgs e)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        if (TryMap(e.Snapshot, out var packageSnapshot))
        {
            TaskChanged?.Invoke(this, new BackgroundTaskChangedEventArgs(packageSnapshot));
        }
    }

    private bool StopCore()
    {
        bool wasStarted;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            wasStarted = _started;
            _started = false;
        }

        if (wasStarted)
        {
            _backgroundTasks.TaskChanged -= BackgroundTasks_OnTaskChanged;
        }

        return true;
    }

    private IReadOnlyList<BackgroundTaskSnapshot> ListTasksCore(string? groupKey = null)
    {
        var snapshots = new List<BackgroundTaskSnapshot>();
        foreach (var snapshot in _backgroundTasks.ListTasks())
        {
            if (TryMap(snapshot, out var packageSnapshot)
                && (string.IsNullOrWhiteSpace(groupKey)
                    || string.Equals(packageSnapshot.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase)))
            {
                snapshots.Add(packageSnapshot);
            }
        }

        return snapshots;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PackageScopedBackgroundTaskQueue));
        }
    }

    private bool TryMap(BackgroundTaskSnapshot snapshot, out BackgroundTaskSnapshot packageSnapshot)
    {
        if (PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
            && string.Equals(metadata.PackageId, _packageId, StringComparison.OrdinalIgnoreCase)
            && metadata.OwnerId == _ownerId)
        {
            packageSnapshot = ToPackageSnapshot(snapshot, metadata);
            return true;
        }

        packageSnapshot = null!;
        return false;
    }

    private static BackgroundTaskSnapshot ToPackageSnapshot(
        BackgroundTaskSnapshot snapshot,
        PackageScopedBackgroundTaskMetadata metadata)
        => new(
            snapshot.TaskId,
            snapshot.Title,
            metadata.PackageGroupKey,
            snapshot.Indicator,
            snapshot.ConcurrencyMode,
            snapshot.State,
            snapshot.StatusText,
            snapshot.ProgressPercent,
            snapshot.CanCancel,
            metadata.PackageMetadata,
            snapshot.ErrorMessage,
            snapshot.QueuedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc);

    private static string BuildHostGroupKey(string packageId, string groupKey, Guid? ownerId)
        => ownerId is null
            ? $"package:{packageId}:{groupKey}"
            : $"package:{packageId}:{ownerId:N}:{groupKey}";
}

internal sealed record PackageScopedBackgroundTaskMetadata(
    string PackageId,
    string PackageDisplayName,
    string PackageGroupKey,
    IReadOnlyDictionary<string, string> PackageMetadata,
    Guid? OwnerId = null)
{
    private const string PackageIdMetadataKey = "sunder.package.id";
    private const string PackageDisplayNameMetadataKey = "sunder.package.displayName";
    private const string PackageGroupKeyMetadataKey = "sunder.package.groupKey";
    private const string PackageMetadataKeyPrefix = "sunder.package.metadata.";
    private const string OwnerIdMetadataKey = "sunder.package.ownerId";

    public static PackageScopedBackgroundTaskMetadata Create(
        string packageId,
        string packageDisplayName,
        string packageGroupKey,
        IReadOnlyDictionary<string, string>? packageMetadata,
        Guid? ownerId = null)
        => new(
            packageId,
            packageDisplayName,
            packageGroupKey,
            packageMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(packageMetadata, StringComparer.OrdinalIgnoreCase),
            ownerId);

    public static bool TryCreate(IReadOnlyDictionary<string, string> hostMetadata, out PackageScopedBackgroundTaskMetadata metadata)
    {
        if (hostMetadata.TryGetValue(PackageIdMetadataKey, out var packageId)
            && hostMetadata.TryGetValue(PackageDisplayNameMetadataKey, out var packageDisplayName)
            && hostMetadata.TryGetValue(PackageGroupKeyMetadataKey, out var packageGroupKey))
        {
            Guid? ownerId = hostMetadata.TryGetValue(OwnerIdMetadataKey, out var ownerIdText)
                            && Guid.TryParse(ownerIdText, out var parsedOwnerId)
                ? parsedOwnerId
                : null;
            metadata = new PackageScopedBackgroundTaskMetadata(
                packageId,
                packageDisplayName,
                packageGroupKey,
                hostMetadata
                    .Where(pair => pair.Key.StartsWith(PackageMetadataKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        pair => pair.Key[PackageMetadataKeyPrefix.Length..],
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase),
                ownerId);
            return true;
        }

        metadata = null!;
        return false;
    }

    public IReadOnlyDictionary<string, string> ToHostMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PackageIdMetadataKey] = PackageId,
            [PackageDisplayNameMetadataKey] = PackageDisplayName,
            [PackageGroupKeyMetadataKey] = PackageGroupKey,
        };

        foreach (var pair in PackageMetadata)
        {
            metadata[$"{PackageMetadataKeyPrefix}{pair.Key}"] = pair.Value;
        }

        if (OwnerId is { } ownerId)
        {
            metadata[OwnerIdMetadataKey] = ownerId.ToString("D");
        }

        return metadata;
    }
}
