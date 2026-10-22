using System.Collections.ObjectModel;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Specifies the single shell surface that displays background-task activity.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundTaskIndicator
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
public enum BackgroundTaskConcurrencyMode
{
    /// <summary>Allows requests in the group to execute concurrently.</summary>
    ParallelWithinGroup,
    /// <summary>Executes requests in the group one at a time in queue order.</summary>
    SequentialWithinGroup,
}

/// <summary>Describes the host-managed lifecycle state of queued work.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public enum BackgroundTaskState
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

/// <summary>Defines host-queued work owned by the submitter.</summary>
/// <param name="Title">User-facing activity title.</param>
/// <param name="GroupKey">Submitter-defined key used only for scheduling and filtering.</param>
/// <param name="Indicator">Shell surface that displays activity.</param>
/// <param name="ConcurrencyMode">Scheduling behavior within <paramref name="GroupKey"/>.</param>
/// <param name="CanCancel">Whether the host may expose cancellation to the user.</param>
/// <param name="ExecuteAsync">Asynchronous work delegate; it owns no host resources beyond the supplied context and must observe its cancellation token.</param>
/// <param name="Metadata">Optional immutable descriptive values copied by the host; <see langword="null"/> means no metadata.</param>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed record BackgroundTaskRequest(
    string Title,
    string GroupKey,
    BackgroundTaskIndicator Indicator,
    BackgroundTaskConcurrencyMode ConcurrencyMode,
    bool CanCancel,
    Func<BackgroundTaskContext, Task> ExecuteAsync,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    private IReadOnlyDictionary<string, string>? _metadata = BackgroundTaskMetadata.FreezeNullable(Metadata);

    /// <summary>Gets the immutable optional descriptive values supplied by the submitter.</summary>
    public IReadOnlyDictionary<string, string>? Metadata
    {
        get => _metadata;
        init => _metadata = BackgroundTaskMetadata.FreezeNullable(value);
    }
}

/// <summary>Provides an immutable point-in-time view of host-managed background-task state.</summary>
/// <param name="TaskId">Host-generated identity for cancellation and change correlation.</param>
/// <param name="Title">User-facing activity title.</param>
/// <param name="GroupKey">Submitter-defined scheduling group.</param>
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
public sealed record BackgroundTaskSnapshot(
    Guid TaskId,
    string Title,
    string GroupKey,
    BackgroundTaskIndicator Indicator,
    BackgroundTaskConcurrencyMode ConcurrencyMode,
    BackgroundTaskState State,
    string StatusText,
    double? ProgressPercent,
    bool CanCancel,
    IReadOnlyDictionary<string, string> Metadata,
    string? ErrorMessage,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    private bool _canCancel = CanCancel;
    private IReadOnlyDictionary<string, string> _metadata = BackgroundTaskMetadata.Freeze(Metadata);

    /// <summary>Gets whether cancellation is both supported and still actionable.</summary>
    public bool CanCancel
    {
        get => _canCancel && State is BackgroundTaskState.Queued or BackgroundTaskState.Running;
        init => _canCancel = value;
    }

    /// <summary>Gets the immutable host-owned snapshot of request metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = BackgroundTaskMetadata.Freeze(value);
    }

    /// <summary>Gets whether work is queued, running, or cancelling.</summary>
    public bool IsActive => State is BackgroundTaskState.Queued or BackgroundTaskState.Running or BackgroundTaskState.Cancelling;

    /// <summary>Gets whether work has reached a completed, failed, or cancelled state.</summary>
    public bool IsTerminal => !IsActive;

    /// <summary>Gets whether no numeric progress is currently available.</summary>
    public bool IsIndeterminate => ProgressPercent is null;
}

/// <summary>Provides the latest snapshot after a task state or progress change.</summary>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed class BackgroundTaskChangedEventArgs(BackgroundTaskSnapshot snapshot) : EventArgs
{
    /// <summary>Gets the immutable changed-task snapshot.</summary>
    public BackgroundTaskSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Provides cancellation and thread-safe progress reporting to one executing request.</summary>
/// <remarks>The host owns the context. The delegate may report from any thread and must not retain it after completion.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public sealed class BackgroundTaskContext
{
    private readonly Action<string> _reportStatus;
    private readonly Action<double, string?> _reportProgress;
    private readonly Action<string> _reportIndeterminate;

    /// <summary>Creates an execution context backed by host-owned reporting callbacks.</summary>
    public BackgroundTaskContext(
        CancellationToken cancellationToken,
        Action<string> reportStatus,
        Action<double, string?> reportProgress,
        Action<string> reportIndeterminate)
    {
        ArgumentNullException.ThrowIfNull(reportStatus);
        ArgumentNullException.ThrowIfNull(reportProgress);
        ArgumentNullException.ThrowIfNull(reportIndeterminate);
        CancellationToken = cancellationToken;
        _reportStatus = reportStatus;
        _reportProgress = reportProgress;
        _reportIndeterminate = reportIndeterminate;
    }

    /// <summary>Gets the token signalled when host or user cancellation is requested.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Replaces status text without changing numeric progress.</summary>
    public void ReportStatus(string statusText) => _reportStatus(statusText);

    /// <summary>Reports clamped finite percentage progress and optionally replaces status text.</summary>
    public void ReportProgress(double progressPercent, string? statusText = null)
    {
        if (!double.IsFinite(progressPercent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(progressPercent),
                progressPercent,
                "Background task progress must be finite.");
        }

        _reportProgress(Math.Clamp(progressPercent, 0, 100), statusText);
    }

    /// <summary>Switches to indeterminate progress and replaces status text.</summary>
    public void ReportIndeterminate(string statusText) => _reportIndeterminate(statusText);
}

/// <summary>Schedules background work and exposes thread-safe immutable snapshots.</summary>
/// <remarks>Sunder uses this contract for shell work and supplies activation-scoped adapters to packages. A queued task is not an OS process, durable job, or worker-execution cleanup receipt.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundProcessesV1)]
public interface IBackgroundTaskQueue
{
    /// <summary>Occurs after task state, status, or progress changes; handlers must return promptly.</summary>
    event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged;

    /// <summary>Queues a request and returns its initial snapshot without waiting for execution.</summary>
    BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request);

    /// <summary>Returns an immutable snapshot list, optionally filtered by exact group key.</summary>
    IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null);

    /// <summary>Requests cancellation, returning <see langword="false"/> when absent, terminal, or not cancellable.</summary>
    bool Cancel(Guid taskId);
}

internal static class BackgroundTaskMetadata
{
    public static IReadOnlyDictionary<string, string>? FreezeNullable(
        IReadOnlyDictionary<string, string>? metadata)
        => metadata is null ? null : Freeze(metadata);

    public static IReadOnlyDictionary<string, string> Freeze(
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Count == 0)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
        {
            if (pair.Key is null || pair.Value is null)
            {
                throw new ArgumentException("Background task metadata keys and values cannot be null.", nameof(metadata));
            }

            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Background task metadata keys must be unique ignoring case.", nameof(metadata));
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
