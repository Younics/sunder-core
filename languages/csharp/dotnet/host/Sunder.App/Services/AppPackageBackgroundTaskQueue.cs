using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageBackgroundTaskQueue(
    PackageScopedBackgroundTaskQueue publishedQueue,
    AppPackageGenerationPublication publication)
    : IBackgroundTaskQueue, IDisposable, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, PendingProcess> _pending = [];
    private bool _disposed;

    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged;

    public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (publication.IsPublished)
        {
            return publishedQueue.Enqueue(request);
        }

        BackgroundTaskRequestValidator.Validate(request);
        var requestSnapshot = request with
        {
            Metadata = request.Metadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase),
        };
        var processId = Guid.NewGuid();
        var queuedAtUtc = DateTimeOffset.UtcNow;
        var snapshot = CreateQueuedSnapshot(processId, requestSnapshot, queuedAtUtc);

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (publication.IsRevoked)
            {
                throw new InvalidOperationException("The App package generation is no longer publishable.");
            }
            _pending[processId] = new PendingProcess(requestSnapshot, snapshot);
        }

        publication.Buffer(() => Publish(processId));
        TaskChanged?.Invoke(this, new BackgroundTaskChangedEventArgs(snapshot));
        return snapshot;
    }

    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null)
    {
        BackgroundTaskSnapshot[] pending;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return [];
            }

            pending = _pending.Values
                .Select(process => process.Snapshot)
                .Where(snapshot => string.IsNullOrWhiteSpace(groupKey)
                                   || string.Equals(snapshot.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return pending.Concat(publishedQueue.ListTasks(groupKey)).ToArray();
    }

    public bool Cancel(Guid processId)
    {
        PendingProcess? pending = null;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return false;
            }

            if (_pending.TryGetValue(processId, out var candidate) && candidate.Snapshot.CanCancel)
            {
                pending = candidate;
                _pending.Remove(processId);
            }
        }

        if (pending is null)
        {
            return publishedQueue.Cancel(processId);
        }

        var cancelled = pending.Snapshot with
        {
            State = BackgroundTaskState.Cancelled,
            StatusText = "Cancelled",
            CanCancel = false,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        TaskChanged?.Invoke(this, new BackgroundTaskChangedEventArgs(cancelled));
        return true;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending.Clear();
        }

        publishedQueue.TaskChanged -= PublishedQueue_OnTaskChanged;
        publishedQueue.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending.Clear();
        }

        publishedQueue.TaskChanged -= PublishedQueue_OnTaskChanged;
        await publishedQueue.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    internal void Start()
    {
        publishedQueue.TaskChanged += PublishedQueue_OnTaskChanged;
        publishedQueue.Start();
    }

    private void Publish(Guid processId)
    {
        PendingProcess? pending;
        lock (_syncRoot)
        {
            if (_disposed || !_pending.Remove(processId, out pending))
            {
                return;
            }
        }

        publishedQueue.Enqueue(pending.Request, processId);
    }

    private void PublishedQueue_OnTaskChanged(object? sender, BackgroundTaskChangedEventArgs e)
        => TaskChanged?.Invoke(this, e);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AppPackageBackgroundTaskQueue));
        }
    }

    private static BackgroundTaskSnapshot CreateQueuedSnapshot(
        Guid processId,
        BackgroundTaskRequest request,
        DateTimeOffset queuedAtUtc)
        => new(
            processId,
            request.Title,
            request.GroupKey,
            request.Indicator,
            request.ConcurrencyMode,
            BackgroundTaskState.Queued,
            "Queued",
            ProgressPercent: null,
            request.CanCancel,
            request.Metadata!,
            ErrorMessage: null,
            queuedAtUtc,
            StartedAtUtc: null,
            CompletedAtUtc: null);

    private sealed record PendingProcess(
        BackgroundTaskRequest Request,
        BackgroundTaskSnapshot Snapshot);
}
