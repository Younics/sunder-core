using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class BackgroundProcessQueueService : IBackgroundProcessQueue
{
    private const int DefaultMaxCompletedHistory = 200;
    private readonly object _syncRoot = new();
    private readonly List<BackgroundProcessWorkItem> _processes = [];
    private readonly Dictionary<Guid, Task> _runningTasks = [];
    private readonly int _maxParallelism;
    private readonly int _maxCompletedHistory;

    public BackgroundProcessQueueService(int maxParallelism = 4, int maxCompletedHistory = DefaultMaxCompletedHistory)
    {
        _maxParallelism = Math.Max(1, maxParallelism);
        _maxCompletedHistory = Math.Max(0, maxCompletedHistory);
    }

    public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged;

    public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Background process title is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.GroupKey))
        {
            throw new ArgumentException("Background process group key is required.", nameof(request));
        }

        if (request.ExecuteAsync is null)
        {
            throw new ArgumentException("Background process execute delegate is required.", nameof(request));
        }

        var normalizedRequest = request with { Metadata = NormalizeMetadata(request.Metadata) };
        var item = new BackgroundProcessWorkItem(Guid.NewGuid(), normalizedRequest, DateTimeOffset.UtcNow)
        {
            StatusText = "Queued",
        };

        BackgroundProcessSnapshot snapshot;
        lock (_syncRoot)
        {
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

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        BackgroundProcessSnapshot[] changedSnapshots;
        Task[] runningTasks;
        lock (_syncRoot)
        {
            changedSnapshots = CancelAllCore();
            runningTasks = _runningTasks.Values.ToArray();
        }

        foreach (var snapshot in changedSnapshots)
        {
            Publish(snapshot);
        }

        if (runningTasks.Length == 0)
        {
            TrimCompletedHistory();
            return;
        }

        try
        {
            await Task.WhenAll(runningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Individual process failures are reflected through process state snapshots.
        }

        TrimCompletedHistory();
    }

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

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                item.StatusText = statusText;
            }

            if (updateProgress)
            {
                item.ProgressPercent = progressPercent is null ? null : Math.Clamp(progressPercent.Value, 0, 100);
            }

            snapshot = item.ToSnapshot();
        }

        Publish(snapshot);
    }

    private bool TryCancel(Guid processId, bool force)
    {
        BackgroundProcessSnapshot? snapshot = null;
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
                item.State = BackgroundProcessState.Cancelled;
                item.StatusText = "Cancelled";
                item.CompletedAtUtc = DateTimeOffset.UtcNow;
                snapshot = item.ToSnapshot();
                shouldSchedule = true;
            }
            else
            {
                item.State = BackgroundProcessState.Cancelling;
                item.StatusText = "Cancelling...";
                item.CancellationTokenSource.Cancel();
                snapshot = item.ToSnapshot();
            }
        }

        Publish(snapshot);
        if (shouldSchedule)
        {
            ScheduleEligibleProcesses();
            TrimCompletedHistory();
        }

        return true;
    }

    private BackgroundProcessSnapshot[] CancelAllCore()
    {
        var changed = new List<BackgroundProcessSnapshot>();
        foreach (var item in _processes)
        {
            if (item.State == BackgroundProcessState.Queued)
            {
                item.State = BackgroundProcessState.Cancelled;
                item.StatusText = "Cancelled";
                item.CompletedAtUtc = DateTimeOffset.UtcNow;
                changed.Add(item.ToSnapshot());
                continue;
            }

            if (item.State == BackgroundProcessState.Running)
            {
                item.State = BackgroundProcessState.Cancelling;
                item.StatusText = "Cancelling...";
                item.CancellationTokenSource.Cancel();
                changed.Add(item.ToSnapshot());
            }
        }

        return changed.ToArray();
    }

    private void ScheduleEligibleProcesses()
    {
        List<BackgroundProcessWorkItem> processesToStart = [];
        List<BackgroundProcessSnapshot> changedSnapshots = [];

        lock (_syncRoot)
        {
            while (_runningTasks.Count + processesToStart.Count < _maxParallelism)
            {
                var candidate = _processes.FirstOrDefault(process =>
                    process.State == BackgroundProcessState.Queued
                    && !IsGroupBlocked(process, processesToStart));
                if (candidate is null)
                {
                    break;
                }

                candidate.State = BackgroundProcessState.Running;
                candidate.StatusText = "Starting...";
                candidate.StartedAtUtc = DateTimeOffset.UtcNow;
                processesToStart.Add(candidate);
                changedSnapshots.Add(candidate.ToSnapshot());
            }

            foreach (var item in processesToStart)
            {
                _runningTasks[item.ProcessId] = Task.Run(() => RunProcessAsync(item));
            }
        }

        foreach (var snapshot in changedSnapshots)
        {
            Publish(snapshot);
        }
    }

    private bool IsGroupBlocked(BackgroundProcessWorkItem candidate, IReadOnlyList<BackgroundProcessWorkItem> processesToStart)
    {
        return _processes.Concat(processesToStart)
            .Any(process =>
                process.ProcessId != candidate.ProcessId
                && process.State is BackgroundProcessState.Running or BackgroundProcessState.Cancelling
                && string.Equals(process.Request.GroupKey, candidate.Request.GroupKey, StringComparison.OrdinalIgnoreCase)
                && (process.Request.ConcurrencyMode == BackgroundProcessConcurrencyMode.SequentialWithinGroup
                    || candidate.Request.ConcurrencyMode == BackgroundProcessConcurrencyMode.SequentialWithinGroup));
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
                item.State = BackgroundProcessState.Completed;
                item.StatusText = "Completed";
                item.ProgressPercent = 100;
                item.CompletedAtUtc = DateTimeOffset.UtcNow;
                completedSnapshot = item.ToSnapshot();
            }
        }
        catch (OperationCanceledException) when (item.CancellationTokenSource.IsCancellationRequested)
        {
            lock (_syncRoot)
            {
                item.State = BackgroundProcessState.Cancelled;
                item.StatusText = "Cancelled";
                item.CompletedAtUtc = DateTimeOffset.UtcNow;
                completedSnapshot = item.ToSnapshot();
            }
        }
        catch (Exception ex)
        {
            lock (_syncRoot)
            {
                item.State = BackgroundProcessState.Failed;
                item.StatusText = "Failed";
                item.ErrorMessage = ex.Message;
                item.CompletedAtUtc = DateTimeOffset.UtcNow;
                completedSnapshot = item.ToSnapshot();
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _runningTasks.Remove(item.ProcessId);
            }

            item.CancellationTokenSource.Dispose();
        }

        Publish(completedSnapshot);
        ScheduleEligibleProcesses();
        TrimCompletedHistory();
    }

    private void TrimCompletedHistory()
    {
        if (_maxCompletedHistory == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            var completed = _processes
                .Where(process => process.State is BackgroundProcessState.Completed or BackgroundProcessState.Failed or BackgroundProcessState.Cancelled)
                .OrderByDescending(process => process.CompletedAtUtc)
                .Skip(_maxCompletedHistory)
                .ToArray();
            foreach (var item in completed)
            {
                _processes.Remove(item);
            }
        }
    }

    private void Publish(BackgroundProcessSnapshot? snapshot)
    {
        var handlers = ProcessChanged;
        if (snapshot is null || handlers is null)
        {
            return;
        }

        var args = new BackgroundProcessChangedEventArgs(snapshot);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<BackgroundProcessChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("A background process change subscriber failed.", ex);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

    private sealed class BackgroundProcessWorkItem(Guid processId, BackgroundProcessRequest request, DateTimeOffset queuedAtUtc)
    {
        public Guid ProcessId { get; } = processId;

        public BackgroundProcessRequest Request { get; } = request;

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public BackgroundProcessState State { get; set; } = BackgroundProcessState.Queued;

        public string StatusText { get; set; } = string.Empty;

        public double? ProgressPercent { get; set; }

        public string? ErrorMessage { get; set; }

        public DateTimeOffset QueuedAtUtc { get; } = queuedAtUtc;

        public DateTimeOffset? StartedAtUtc { get; set; }

        public DateTimeOffset? CompletedAtUtc { get; set; }

        public BackgroundProcessSnapshot ToSnapshot()
            => new(
                ProcessId,
                Request.Title,
                Request.GroupKey,
                Request.Indicator,
                Request.ConcurrencyMode,
                State,
                StatusText,
                ProgressPercent,
                Request.CanCancel,
                Request.Metadata!,
                ErrorMessage,
                QueuedAtUtc,
                StartedAtUtc,
                CompletedAtUtc);
    }
}
