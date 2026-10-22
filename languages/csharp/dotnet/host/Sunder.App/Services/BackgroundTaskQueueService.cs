using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class BackgroundTaskQueueService : IBackgroundTaskQueue, IDisposable, IAsyncDisposable
{
    private const int DefaultMaxCompletedHistory = 200;
    private readonly object _syncRoot = new();
    private readonly List<BackgroundTaskWorkItem> _processes = [];
    private readonly Dictionary<Guid, Task> _runningTasks = [];
    private readonly BackgroundTaskEventPublisher _events;
    private readonly int _maxParallelism;
    private readonly int _maxCompletedHistory;
    private bool _disposed;

    public BackgroundTaskQueueService(int maxParallelism = 4, int maxCompletedHistory = DefaultMaxCompletedHistory)
    {
        _events = new BackgroundTaskEventPublisher(this);
        _maxParallelism = Math.Max(1, maxParallelism);
        _maxCompletedHistory = Math.Max(0, maxCompletedHistory);
    }

    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged
    {
        add => _events.TaskChanged += value;
        remove => _events.TaskChanged -= value;
    }

    public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
        => Enqueue(request, Guid.NewGuid());

    internal BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request, Guid taskId)
        => EnqueueBatch([new(taskId, request)])[0];

    // Publish the complete ordered group before any delegate can start. The SDK API and
    // scheduler stay unchanged; this also closes the fast-first-job-failure enrollment race.
    internal IReadOnlyList<BackgroundTaskSnapshot> EnqueueBatch(IReadOnlyList<QueuedBackgroundTask> requests, CancellationToken cancellationToken = default)
    {
        var items = requests.Select(entry =>
        {
            BackgroundTaskRequestValidator.Validate(entry.Request);
            return new BackgroundTaskWorkItem(entry.TaskId,
                entry.Request with { Metadata = NormalizeMetadata(entry.Request.Metadata) }, DateTimeOffset.UtcNow, entry.StatusText);
        }).ToArray();
        BackgroundTaskSnapshot[] snapshots;
        try
        {
            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (items.Select(item => item.TaskId).Distinct().Count() != items.Length
                    || items.Any(item => _processes.Any(existing => existing.TaskId == item.TaskId)))
                    throw new ArgumentException("Background task IDs must be unique.", nameof(requests));
                _processes.AddRange(items);
                snapshots = items.Select(item => item.ToSnapshot()).ToArray();
            }
        }
        catch { foreach (var item in items) item.DisposeCancellationTokenSource(); throw; }

        foreach (var snapshot in snapshots) Publish(snapshot);
        ScheduleEligibleProcesses();
        return snapshots;
    }

    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null)
    {
        lock (_syncRoot)
        {
            return _processes
                .Where(process => string.IsNullOrWhiteSpace(groupKey)
                                  || string.Equals(process.Request.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                .Select(process => process.ToSnapshot())
                .ToArray();
        }
    }

    public BackgroundTaskSnapshot? GetTask(Guid taskId)
    {
        lock (_syncRoot)
        {
            return _processes.FirstOrDefault(process => process.TaskId == taskId)?.ToSnapshot();
        }
    }

    public bool Cancel(Guid taskId)
        => TryCancel(taskId, force: false);

    internal bool Cancel(Guid taskId, string queuedStatus)
        => TryCancel(taskId, force: false, queuedStatus);

    public void Dispose()
    {
        Dispose(waitForRunningTasks: false);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        var runningTasks = Dispose(waitForRunningTasks: true);
        await BackgroundTaskCancellation.WaitForRunningTasksAsync(runningTasks, cancellationToken: default).ConfigureAwait(false);
        TrimCompletedHistory();
        GC.SuppressFinalize(this);
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        BackgroundTaskSnapshot[] changedSnapshots;
        CancellationTokenSource[] cancellationTokenSourcesToCancel;
        Task[] runningTasks;
        lock (_syncRoot)
        {
            (changedSnapshots, cancellationTokenSourcesToCancel) = CancelAllCore();
            runningTasks = _runningTasks.Values.ToArray();
        }

        BackgroundTaskCancellation.Deliver(cancellationTokenSourcesToCancel);
        foreach (var snapshot in changedSnapshots)
        {
            Publish(snapshot);
        }

        await BackgroundTaskCancellation.WaitForRunningTasksAsync(runningTasks, cancellationToken).ConfigureAwait(false);
        TrimCompletedHistory();
    }

    private Task[] Dispose(bool waitForRunningTasks)
    {
        BackgroundTaskSnapshot[] changedSnapshots;
        CancellationTokenSource[] cancellationTokenSourcesToCancel;
        Task[] runningTasks;
        lock (_syncRoot)
        {
            if (_disposed && _runningTasks.Count == 0)
            {
                return [];
            }

            _disposed = true;
            (changedSnapshots, cancellationTokenSourcesToCancel) = CancelAllCore();
            runningTasks = _runningTasks.Values.ToArray();
        }

        BackgroundTaskCancellation.Deliver(cancellationTokenSourcesToCancel);
        foreach (var snapshot in changedSnapshots)
        {
            Publish(snapshot);
        }

        if (!waitForRunningTasks)
        {
            TrimCompletedHistory();
            return [];
        }

        return runningTasks;
    }

    internal Task CancelAllPackageProcessesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => CancelMatchingAsync(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase),
            cancellationToken);

    internal void CancelAllPackageProcesses(string packageId)
        => CancelMatching(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal Task CancelPackageOwnerProcessesAsync(
        string packageId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
        => CancelMatchingAsync(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                        && metadata.OwnerId == ownerId,
            cancellationToken);

    internal Task CancelOwnerProcessesAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
        => CancelMatchingAsync(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && metadata.OwnerId == ownerId,
            cancellationToken);

    internal void CancelPackageOwnerProcesses(string packageId, Guid ownerId)
        => CancelMatching(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                        && metadata.OwnerId == ownerId);

    internal Task CancelOwnerlessPackageProcessesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => CancelMatchingAsync(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                        && metadata.OwnerId is null,
            cancellationToken);

    internal void CancelOwnerlessPackageProcesses(string packageId)
        => CancelMatching(
            snapshot => PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                        && metadata.OwnerId is null);

    internal void UpdateProcess(
        Guid taskId,
        string? statusText,
        double? progressPercent,
        bool updateProgress)
    {
        BackgroundTaskSnapshot? snapshot = null;
        lock (_syncRoot)
        {
            var item = _processes.FirstOrDefault(process => process.TaskId == taskId);
            if (item is null || item.State is not (BackgroundTaskState.Running or BackgroundTaskState.Cancelling))
            {
                return;
            }

            item.Update(statusText, progressPercent, updateProgress);
            snapshot = item.ToSnapshot();
        }

        Publish(snapshot);
    }

    internal async Task CancelMatchingAsync(
        Func<BackgroundTaskSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        var runningTasks = CancelMatching(predicate);
        await BackgroundTaskCancellation.WaitForRunningTasksAsync(runningTasks, cancellationToken).ConfigureAwait(false);
        TrimCompletedHistory();
    }

    private Task[] CancelMatching(Func<BackgroundTaskSnapshot, bool> predicate)
    {
        BackgroundTaskSnapshot[] changedSnapshots;
        CancellationTokenSource[] cancellationTokenSourcesToCancel;
        Task[] runningTasks;
        var shouldSchedule = false;
        lock (_syncRoot)
        {
            var changed = new List<BackgroundTaskSnapshot>();
            var cancellationTokenSources = new List<CancellationTokenSource>();
            var tasks = new List<Task>();
            foreach (var item in _processes)
            {
                if (!predicate(item.ToSnapshot()))
                {
                    continue;
                }

                if (_runningTasks.TryGetValue(item.TaskId, out var runningTask))
                {
                    tasks.Add(runningTask);
                }

                if (item.State == BackgroundTaskState.Queued)
                {
                    item.MarkQueuedCancelled(DateTimeOffset.UtcNow);
                    changed.Add(item.ToSnapshot());
                    shouldSchedule = true;
                    continue;
                }

                if (item.State == BackgroundTaskState.Running)
                {
                    item.MarkCancelling();
                    cancellationTokenSources.Add(item.CancellationTokenSource);
                    changed.Add(item.ToSnapshot());
                }
            }

            changedSnapshots = changed.ToArray();
            cancellationTokenSourcesToCancel = cancellationTokenSources.ToArray();
            runningTasks = tasks.Distinct().ToArray();
        }

        BackgroundTaskCancellation.Deliver(cancellationTokenSourcesToCancel);
        foreach (var snapshot in changedSnapshots)
        {
            Publish(snapshot);
        }

        if (shouldSchedule)
        {
            ScheduleEligibleProcesses();
        }

        TrimCompletedHistory();
        return runningTasks;
    }

    private bool TryCancel(Guid taskId, bool force, string? queuedStatus = null)
    {
        BackgroundTaskSnapshot? snapshot = null;
        CancellationTokenSource? cancellationTokenSourceToCancel = null;
        var shouldSchedule = false;
        lock (_syncRoot)
        {
            var item = _processes.FirstOrDefault(process => process.TaskId == taskId);
            if (item is null || item.State is not (BackgroundTaskState.Queued or BackgroundTaskState.Running))
            {
                return false;
            }

            if (!force && !item.Request.CanCancel)
            {
                return false;
            }

            if (item.State == BackgroundTaskState.Queued)
            {
                item.MarkQueuedCancelled(DateTimeOffset.UtcNow, queuedStatus);
                snapshot = item.ToSnapshot();
                shouldSchedule = true;
            }
            else
            {
                item.MarkCancelling();
                cancellationTokenSourceToCancel = item.CancellationTokenSource;
                snapshot = item.ToSnapshot();
            }
        }

        BackgroundTaskCancellation.Deliver(cancellationTokenSourceToCancel);
        Publish(snapshot);
        if (shouldSchedule)
        {
            ScheduleEligibleProcesses();
            TrimCompletedHistory();
        }

        return true;
    }

    private (BackgroundTaskSnapshot[] ChangedSnapshots, CancellationTokenSource[] CancellationTokenSourcesToCancel) CancelAllCore()
    {
        var changed = new List<BackgroundTaskSnapshot>();
        var cancellationTokenSources = new List<CancellationTokenSource>();
        foreach (var item in _processes)
        {
            if (item.State == BackgroundTaskState.Queued)
            {
                item.MarkQueuedCancelled(DateTimeOffset.UtcNow);
                changed.Add(item.ToSnapshot());
                continue;
            }

            if (item.State == BackgroundTaskState.Running)
            {
                item.MarkCancelling();
                cancellationTokenSources.Add(item.CancellationTokenSource);
                changed.Add(item.ToSnapshot());
            }
        }

        return (changed.ToArray(), cancellationTokenSources.ToArray());
    }

    private void ScheduleEligibleProcesses()
    {
        BackgroundTaskWorkItem[] processesToStart;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            processesToStart = BackgroundTaskScheduler.StartEligibleProcesses(_processes, _runningTasks.Count, _maxParallelism);
            foreach (var item in processesToStart)
            {
                using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
                _runningTasks[item.TaskId] = Task.Run(() => RunProcessAsync(item));
            }
        }

        foreach (var snapshot in processesToStart.Select(process => process.ToSnapshot()))
        {
            Publish(snapshot);
        }
    }

    private async Task RunProcessAsync(BackgroundTaskWorkItem item)
    {
        BackgroundTaskSnapshot? completedSnapshot = null;
        try
        {
            var context = new BackgroundTaskContext(
                item.CancellationTokenSource.Token,
                statusText => UpdateProcess(item.TaskId, statusText: statusText, progressPercent: null, updateProgress: false),
                (progressPercent, statusText) => UpdateProcess(item.TaskId, statusText, Math.Clamp(progressPercent, 0, 100), updateProgress: true),
                statusText => UpdateProcess(item.TaskId, statusText, progressPercent: null, updateProgress: true));
            await item.ExecuteAsync(context).ConfigureAwait(false);
            lock (_syncRoot)
            {
                item.CompleteAfterSuccessfulExecution(DateTimeOffset.UtcNow);
                completedSnapshot = item.ToSnapshot();
            }
        }
        catch (OperationCanceledException error) when (item.CancellationTokenSource.IsCancellationRequested
            && error.CancellationToken == item.CancellationTokenSource.Token)
        {
            lock (_syncRoot)
            {
                item.CompleteAfterCancellation(DateTimeOffset.UtcNow);
                completedSnapshot = item.ToSnapshot();
            }
        }
        catch (Exception ex)
        {
            lock (_syncRoot)
            {
                item.CompleteAfterFailure(ex, DateTimeOffset.UtcNow);
                completedSnapshot = item.ToSnapshot();
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _runningTasks.Remove(item.TaskId);
            }

            item.DisposeCancellationTokenSource();
        }

        Publish(completedSnapshot);
        ScheduleEligibleProcesses();
        TrimCompletedHistory();
    }

    private void TrimCompletedHistory()
    {
        lock (_syncRoot)
        {
            BackgroundTaskHistory.TrimCompleted(_processes, _maxCompletedHistory);
        }
    }

    private void Publish(BackgroundTaskSnapshot? snapshot)
    {
        _events.Publish(snapshot);
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
}

internal sealed record QueuedBackgroundTask(Guid TaskId, BackgroundTaskRequest Request, string? StatusText = null);
