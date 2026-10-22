using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class BackgroundTaskWorkItem(Guid taskId, BackgroundTaskRequest request, DateTimeOffset queuedAtUtc, string? queuedStatus = null)
{
    private ExecutionContext? _submissionContext = ExecutionContext.Capture();

    public Guid TaskId { get; } = taskId;

    public BackgroundTaskRequest Request { get; } = request;

    internal Task ExecuteAsync(BackgroundTaskContext context)
    {
        // The scheduler may be running under a different task's completion context.
        // Only this submission's accepted authority may reach its delegate.
        var submission = Interlocked.Exchange(ref _submissionContext, null);
        if (submission is null) return Request.ExecuteAsync(context);
        Task? work = null;
        ExecutionContext.Run(submission, _ => work = Request.ExecuteAsync(context), null);
        return work!;
    }

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    private bool CancellationTokenSourceDisposed { get; set; }

    public BackgroundTaskState State { get; private set; } = BackgroundTaskState.Queued;

    public string StatusText { get; private set; } = string.IsNullOrWhiteSpace(queuedStatus) ? "Queued" : queuedStatus;

    public double? ProgressPercent { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset QueuedAtUtc { get; } = queuedAtUtc;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void MarkStarting(DateTimeOffset startedAtUtc)
    {
        State = BackgroundTaskState.Running;
        StatusText = "Starting...";
        StartedAtUtc = startedAtUtc;
    }

    public void MarkCancelling()
    {
        State = BackgroundTaskState.Cancelling;
        StatusText = "Cancelling...";
    }

    public void MarkQueuedCancelled(DateTimeOffset completedAtUtc, string? statusText = null)
    {
        State = BackgroundTaskState.Cancelled;
        StatusText = string.IsNullOrWhiteSpace(statusText) ? "Cancelled" : statusText;
        CompletedAtUtc = completedAtUtc;
        DisposeCancellationTokenSource();
    }

    public void Update(string? statusText, double? progressPercent, bool updateProgress)
    {
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusText = statusText;
        }

        if (updateProgress)
        {
            ProgressPercent = progressPercent is null ? null : Math.Clamp(progressPercent.Value, 0, 100);
        }
    }

    public void CompleteAfterSuccessfulExecution(DateTimeOffset completedAtUtc)
    {
        if (State == BackgroundTaskState.Cancelling || CancellationTokenSource.IsCancellationRequested)
        {
            State = BackgroundTaskState.Cancelled;
            StatusText = "Cancelled";
        }
        else
        {
            State = BackgroundTaskState.Completed;
            if (StatusText is "Queued" or "Starting..." || string.IsNullOrWhiteSpace(StatusText))
            {
                StatusText = "Completed";
            }

            ProgressPercent = 100;
        }

        CompletedAtUtc = completedAtUtc;
    }

    public void CompleteAfterCancellation(DateTimeOffset completedAtUtc)
    {
        State = BackgroundTaskState.Cancelled;
        StatusText = "Cancelled";
        CompletedAtUtc = completedAtUtc;
    }

    public void CompleteAfterFailure(Exception exception, DateTimeOffset completedAtUtc)
    {
        State = BackgroundTaskState.Failed;
        StatusText = "Failed";
        ErrorMessage = exception.Message;
        CompletedAtUtc = completedAtUtc;
    }

    public BackgroundTaskSnapshot ToSnapshot()
        => new(
            TaskId,
            Request.Title,
            Request.GroupKey,
            Request.Indicator,
            Request.ConcurrencyMode,
            State,
            StatusText,
            ProgressPercent,
            Request.CanCancel && State is BackgroundTaskState.Queued or BackgroundTaskState.Running,
            Request.Metadata!,
            ErrorMessage,
            QueuedAtUtc,
            StartedAtUtc,
            CompletedAtUtc);

    public void DisposeCancellationTokenSource()
    {
        _submissionContext = null; // Queued cancellation must not retain a package activation.
        if (CancellationTokenSourceDisposed)
        {
            return;
        }

        CancellationTokenSourceDisposed = true;
        CancellationTokenSource.Dispose();
    }
}
