using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class BackgroundProcessWorkItem(Guid processId, BackgroundProcessRequest request, DateTimeOffset queuedAtUtc)
{
    public Guid ProcessId { get; } = processId;

    public BackgroundProcessRequest Request { get; } = request;

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    private bool CancellationTokenSourceDisposed { get; set; }

    public BackgroundProcessState State { get; private set; } = BackgroundProcessState.Queued;

    public string StatusText { get; private set; } = "Queued";

    public double? ProgressPercent { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset QueuedAtUtc { get; } = queuedAtUtc;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void MarkStarting(DateTimeOffset startedAtUtc)
    {
        State = BackgroundProcessState.Running;
        StatusText = "Starting...";
        StartedAtUtc = startedAtUtc;
    }

    public void MarkCancelling()
    {
        State = BackgroundProcessState.Cancelling;
        StatusText = "Cancelling...";
    }

    public void MarkQueuedCancelled(DateTimeOffset completedAtUtc)
    {
        State = BackgroundProcessState.Cancelled;
        StatusText = "Cancelled";
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
        if (State == BackgroundProcessState.Cancelling || CancellationTokenSource.IsCancellationRequested)
        {
            State = BackgroundProcessState.Cancelled;
            StatusText = "Cancelled";
        }
        else
        {
            State = BackgroundProcessState.Completed;
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
        State = BackgroundProcessState.Cancelled;
        StatusText = "Cancelled";
        CompletedAtUtc = completedAtUtc;
    }

    public void CompleteAfterFailure(Exception exception, DateTimeOffset completedAtUtc)
    {
        State = BackgroundProcessState.Failed;
        StatusText = "Failed";
        ErrorMessage = exception.Message;
        CompletedAtUtc = completedAtUtc;
    }

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

    public void DisposeCancellationTokenSource()
    {
        if (CancellationTokenSourceDisposed)
        {
            return;
        }

        CancellationTokenSourceDisposed = true;
        CancellationTokenSource.Dispose();
    }
}
