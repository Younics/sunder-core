using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Specifies the single shell surface that displays process activity.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundProcessIndicator
{
    /// <summary>Runs without a shell activity indicator.</summary>
    Hidden,
    /// <summary>Displays activity in the main shell surface.</summary>
    Main,
    /// <summary>Displays activity in the packages surface.</summary>
    Packages,
    /// <summary>Displays activity in the settings surface.</summary>
    Settings,
}

/// <summary>Controls scheduling relative to requests with the same group key.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundProcessConcurrencyMode
{
    /// <summary>Allows requests in the group to execute concurrently.</summary>
    ParallelWithinGroup,
    /// <summary>Executes requests in the group one at a time in queue order.</summary>
    SequentialWithinGroup,
}

/// <summary>Describes the host-managed lifecycle state of queued work.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundProcessState
{
    /// <summary>The request is waiting for a scheduler slot.</summary>
    Queued,
    /// <summary>The request delegate is executing.</summary>
    Running,
    /// <summary>Cancellation was requested but execution has not stopped.</summary>
    Cancelling,
    /// <summary>The delegate completed successfully.</summary>
    Completed,
    /// <summary>The delegate ended with an unhandled exception.</summary>
    Failed,
    /// <summary>The delegate observed cancellation and stopped.</summary>
    Cancelled,
}

/// <summary>Defines host-queued work owned by the submitting package.</summary>
/// <param name="Title">User-facing activity title.</param>
/// <param name="GroupKey">Package-defined key used only for scheduling and filtering.</param>
/// <param name="Indicator">Shell surface that displays activity.</param>
/// <param name="ConcurrencyMode">Scheduling behavior within <paramref name="GroupKey"/>.</param>
/// <param name="CanCancel">Whether the host may expose cancellation to the user.</param>
/// <param name="ExecuteAsync">Asynchronous work delegate; it owns no host resources beyond the supplied context and must observe its cancellation token.</param>
/// <param name="Metadata">Optional immutable descriptive values copied by the host; <see langword="null"/> means no metadata.</param>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed record BackgroundProcessRequest(
    string Title,
    string GroupKey,
    BackgroundProcessIndicator Indicator,
    BackgroundProcessConcurrencyMode ConcurrencyMode,
    bool CanCancel,
    Func<BackgroundProcessContext, Task> ExecuteAsync,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Provides an immutable point-in-time view of host-managed process state.</summary>
/// <param name="ProcessId">Host-generated identity for cancellation and change correlation.</param>
/// <param name="Title">User-facing activity title.</param>
/// <param name="GroupKey">Package-defined scheduling group.</param>
/// <param name="Indicator">Shell surface displaying activity.</param>
/// <param name="ConcurrencyMode">Scheduling behavior within the group.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="StatusText">Latest user-facing status text.</param>
/// <param name="ProgressPercent">Progress from 0 through 100, or <see langword="null"/> for indeterminate work.</param>
/// <param name="CanCancel">Whether cancellation remains available in this snapshot.</param>
/// <param name="Metadata">Host-owned snapshot of request metadata.</param>
/// <param name="ErrorMessage">Failure message for failed work, otherwise <see langword="null"/>.</param>
/// <param name="QueuedAtUtc">Time the host accepted the request.</param>
/// <param name="StartedAtUtc">Execution start time, or <see langword="null"/> while queued.</param>
/// <param name="CompletedAtUtc">Terminal time, or <see langword="null"/> while active.</param>
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
    /// <summary>Gets whether work is queued, running, or cancelling.</summary>
    public bool IsActive => State is BackgroundProcessState.Queued or BackgroundProcessState.Running or BackgroundProcessState.Cancelling;

    /// <summary>Gets whether work has reached a completed, failed, or cancelled state.</summary>
    public bool IsTerminal => !IsActive;

    /// <summary>Gets whether no numeric progress is currently available.</summary>
    public bool IsIndeterminate => ProgressPercent is null;
}

/// <summary>Provides the latest snapshot after a process state or progress change.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed class BackgroundProcessChangedEventArgs(BackgroundProcessSnapshot snapshot) : EventArgs
{
    /// <summary>Gets the immutable changed-process snapshot.</summary>
    public BackgroundProcessSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Provides cancellation and thread-safe progress reporting to one executing request.</summary>
/// <remarks>The host owns the context. The delegate may report from any thread and must not retain it after completion.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed class BackgroundProcessContext(
    CancellationToken cancellationToken,
    Action<string> reportStatus,
    Action<double, string?> reportProgress,
    Action<string> reportIndeterminate)
{
    /// <summary>Gets the token signalled when host or user cancellation is requested.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Replaces status text without changing numeric progress.</summary>
    public void ReportStatus(string statusText) => reportStatus(statusText);

    /// <summary>Reports clamped percentage progress and optionally replaces status text.</summary>
    public void ReportProgress(double progressPercent, string? statusText = null) => reportProgress(progressPercent, statusText);

    /// <summary>Switches to indeterminate progress and replaces status text.</summary>
    public void ReportIndeterminate(string statusText) => reportIndeterminate(statusText);
}

/// <summary>Schedules package work and exposes thread-safe immutable snapshots.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public interface IBackgroundProcessQueue
{
    /// <summary>Occurs after process state, status, or progress changes; handlers must return promptly.</summary>
    event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged;

    /// <summary>Queues a request and returns its initial snapshot without waiting for execution.</summary>
    BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request);

    /// <summary>Returns an immutable snapshot list, optionally filtered by exact group key.</summary>
    IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null);

    /// <summary>Requests cancellation, returning <see langword="false"/> when absent, terminal, or not cancellable.</summary>
    bool Cancel(Guid processId);
}
