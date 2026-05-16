using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundProcessIndicator
{
    Hidden,
    Main,
    Packages,
    Settings,
}

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundProcessConcurrencyMode
{
    ParallelWithinGroup,
    SequentialWithinGroup,
}

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundProcessState
{
    Queued,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
}

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed record BackgroundProcessRequest(
    string Title,
    string GroupKey,
    BackgroundProcessIndicator Indicator,
    BackgroundProcessConcurrencyMode ConcurrencyMode,
    bool CanCancel,
    Func<BackgroundProcessContext, Task> ExecuteAsync,
    IReadOnlyDictionary<string, string>? Metadata = null);

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed record BackgroundProcessSnapshot(
    Guid ProcessId,
    string Title,
    string GroupKey,
    BackgroundProcessIndicator Indicator,
    BackgroundProcessConcurrencyMode ConcurrencyMode,
    BackgroundProcessState State,
    string StatusText,
    double? ProgressPercent,
    bool CanCancel,
    IReadOnlyDictionary<string, string> Metadata,
    string? ErrorMessage,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public bool IsActive => State is BackgroundProcessState.Queued or BackgroundProcessState.Running or BackgroundProcessState.Cancelling;

    public bool IsTerminal => !IsActive;

    public bool IsIndeterminate => ProgressPercent is null;
}

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed class BackgroundProcessChangedEventArgs(BackgroundProcessSnapshot snapshot) : EventArgs
{
    public BackgroundProcessSnapshot Snapshot { get; } = snapshot;
}

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed class BackgroundProcessContext(
    CancellationToken cancellationToken,
    Action<string> reportStatus,
    Action<double, string?> reportProgress,
    Action<string> reportIndeterminate)
{
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public void ReportStatus(string statusText) => reportStatus(statusText);

    public void ReportProgress(double progressPercent, string? statusText = null) => reportProgress(progressPercent, statusText);

    public void ReportIndeterminate(string statusText) => reportIndeterminate(statusText);
}

[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public interface IBackgroundProcessQueue
{
    event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged;

    BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request);

    IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null);

    bool Cancel(Guid processId);
}
