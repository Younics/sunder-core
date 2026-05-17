using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class BackgroundProcessQueueService : IBackgroundProcessQueue, IDisposable, IAsyncDisposable
{
    private const int DefaultMaxCompletedHistory = 200;
    private readonly object _syncRoot = new();
    private readonly List<BackgroundProcessWorkItem> _processes = [];
    private readonly Dictionary<Guid, Task> _runningTasks = [];
    private readonly BackgroundProcessEventPublisher _events;
    private readonly int _maxParallelism;
    private readonly int _maxCompletedHistory;
    private bool _disposed;

    public BackgroundProcessQueueService(int maxParallelism = 4, int maxCompletedHistory = DefaultMaxCompletedHistory)
    {
        _events = new BackgroundProcessEventPublisher(this);
        _maxParallelism = Math.Max(1, maxParallelism);
        _maxCompletedHistory = Math.Max(0, maxCompletedHistory);
    }

    public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
    {
        add => _events.ProcessChanged += value;
        remove => _events.ProcessChanged -= value;
    }

    public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
    {
        BackgroundProcessRequestValidator.Validate(request);
        var normalizedRequest = request with { Metadata = NormalizeMetadata(request.Metadata) };
        var item = new BackgroundProcessWorkItem(Guid.NewGuid(), normalizedRequest, DateTimeOffset.UtcNow);

        BackgroundProcessSnapshot snapshot;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BackgroundProcessQueueService));
            }

            _processes.Add(item);
            snapshot = item.ToSnapshot();
        }

        Publish(snapshot);
        ScheduleEligibleProcesses();
        return snapshot;
    }

    public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null)
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

    public BackgroundProcessSnapshot? GetProcess(Guid processId)
    {
        lock (_syncRoot)
        {
            return _processes.FirstOrDefault(process => process.ProcessId == processId)?.ToSnapshot();
        }
    }

    public bool Cancel(Guid processId)
        => TryCancel(processId, force: false);

    public void Dispose()
    {
        Dispose(waitForRunningTasks: false);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        var runningTasks = Dispose(waitForRunningTasks: true);
        await BackgroundProcessCancellation.WaitForRunningTasksAsync(runningTasks, cancellationToken: default).ConfigureAwait(false);
        TrimCompletedHistory();
        GC.SuppressFinalize(this);
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        BackgroundProcessSnapshot[] changedSnapshots;
        CancellationTokenSource[] cancellationTokenSourcesToCancel;
        Task[] runningTasks;
        lock (_syncRoot)
        {
            (changedSnapshots, cancellationTokenSourcesToCancel) = CancelAllCore();
            runningTasks = _runningTasks.Values.ToArray();
        }

        BackgroundProcessCancellation.Deliver(cancellationTokenSourcesToCancel);
        foreach (var snapshot in changedSnapshots)
        {
            Publish(snapshot);
        }

        await BackgroundProcessCancellation.WaitForRunningTasksAsync(runningTasks, cancellationToken).ConfigureAwait(false);
        TrimCompletedHistory();
    }

    private Task[] Dispose(bool waitForRunningTasks)
    {
        BackgroundProcessSnapshot[] changedSnapshots;
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

        BackgroundProcessCancellation.Deliver(cancellationTokenSourcesToCancel);
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

    internal Task CancelPackageProcessesAsync(string packageId, CancellationToken cancellationToken = default)
        => CancelMatchingAsync(
            snapshot => PackageScopedBackgroundProcessMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase),
            cancellationToken);

    internal void CancelPackageProcesses(string packageId)
        => CancelMatching(
            snapshot => PackageScopedBackgroundProcessMetadata.TryCreate(snapshot.Metadata, out var metadata)
                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal void UpdateProcess(
        Guid processId,
        string? statusText,
        double? progressPercent,
        bool updateProgress)
    {
        BackgroundProcessSnapshot? snapshot = null;
        lock (_syncRoot)
        {
            var item = _processes.FirstOrDefault(process => process.ProcessId == processId);
            if (item is null || item.State is not (BackgroundProcessState.Running or BackgroundProcessState.Cancelling))
            {
                return;
            }

            item.Update(statusText, progressPercent, updateProgress);
            snapshot = item.ToSnapshot();
        }

        Publish(snapshot);
    }

    internal async Task CancelMatchingAsync(
        Func<BackgroundProcessSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        var runningTasks = CancelMatching(predicate);
        await BackgroundProcessCancellation.WaitForRunningTasksAsync(runningTasks, cancellationToken).ConfigureAwait(false);
        TrimCompletedHistory();
    }

    private Task[] CancelMatching(Func<BackgroundProcessSnapshot, bool> predicate)
    {
        BackgroundProcessSnapshot[] changedSnapshots;
        CancellationTokenSource[] cancellationTokenSourcesToCancel;
        Task[] runningTasks;
        var shouldSchedule = false;
        lock (_syncRoot)
        {
            var changed = new List<BackgroundProcessSnapshot>();
            var cancellationTokenSources = new List<CancellationTokenSource>();
            var tasks = new List<Task>();
            foreach (var item in _processes)
            {
                if (!predicate(item.ToSnapshot()))
                {
                    continue;
                }

                if (_runningTasks.TryGetValue(item.ProcessId, out var runningTask))
                {
                    tasks.Add(runningTask);
                }

                if (item.State == BackgroundProcessState.Queued)
                {
                    item.MarkQueuedCancelled(DateTimeOffset.UtcNow);
                    changed.Add(item.ToSnapshot());
                    shouldSchedule = true;
                    continue;
                }

                if (item.State == BackgroundProcessState.Running)
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

        BackgroundProcessCancellation.Deliver(cancellationTokenSourcesToCancel);
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

    private bool TryCancel(Guid processId, bool force)
    {
        BackgroundProcessSnapshot? snapshot = null;
        CancellationTokenSource? cancellationTokenSourceToCancel = null;
        var shouldSchedule = false;
        lock (_syncRoot)
        {
            var item = _processes.FirstOrDefault(process => process.ProcessId == processId);
            if (item is null || item.State is not (BackgroundProcessState.Queued or BackgroundProcessState.Running))
            {
                return false;
            }

            if (!force && !item.Request.CanCancel)
            {
                return false;
            }

            if (item.State == BackgroundProcessState.Queued)
            {
                item.MarkQueuedCancelled(DateTimeOffset.UtcNow);
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

        BackgroundProcessCancellation.Deliver(cancellationTokenSourceToCancel);
        Publish(snapshot);
        if (shouldSchedule)
        {
            ScheduleEligibleProcesses();
            TrimCompletedHistory();
        }

        return true;
    }

    private (BackgroundProcessSnapshot[] ChangedSnapshots, CancellationTokenSource[] CancellationTokenSourcesToCancel) CancelAllCore()
    {
        var changed = new List<BackgroundProcessSnapshot>();
        var cancellationTokenSources = new List<CancellationTokenSource>();
        foreach (var item in _processes)
        {
            if (item.State == BackgroundProcessState.Queued)
            {
                item.MarkQueuedCancelled(DateTimeOffset.UtcNow);
                changed.Add(item.ToSnapshot());
                continue;
            }

            if (item.State == BackgroundProcessState.Running)
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
        BackgroundProcessWorkItem[] processesToStart;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            processesToStart = BackgroundProcessScheduler.StartEligibleProcesses(_processes, _runningTasks.Count, _maxParallelism);
            foreach (var item in processesToStart)
            {
                _runningTasks[item.ProcessId] = Task.Run(() => RunProcessAsync(item));
            }
        }

        foreach (var snapshot in processesToStart.Select(process => process.ToSnapshot()))
        {
            Publish(snapshot);
        }
    }

    private async Task RunProcessAsync(BackgroundProcessWorkItem item)
    {
        BackgroundProcessSnapshot? completedSnapshot = null;
        try
        {
            var context = new BackgroundProcessContext(
                item.CancellationTokenSource.Token,
                statusText => UpdateProcess(item.ProcessId, statusText: statusText, progressPercent: null, updateProgress: false),
                (progressPercent, statusText) => UpdateProcess(item.ProcessId, statusText, Math.Clamp(progressPercent, 0, 100), updateProgress: true),
                statusText => UpdateProcess(item.ProcessId, statusText, progressPercent: null, updateProgress: true));
            await item.Request.ExecuteAsync(context).ConfigureAwait(false);
            lock (_syncRoot)
            {
                item.CompleteAfterSuccessfulExecution(DateTimeOffset.UtcNow);
                completedSnapshot = item.ToSnapshot();
            }
        }
        catch (OperationCanceledException) when (item.CancellationTokenSource.IsCancellationRequested)
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
                _runningTasks.Remove(item.ProcessId);
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
            BackgroundProcessHistory.TrimCompleted(_processes, _maxCompletedHistory);
        }
    }

    private void Publish(BackgroundProcessSnapshot? snapshot)
    {
        _events.Publish(snapshot);
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
}
