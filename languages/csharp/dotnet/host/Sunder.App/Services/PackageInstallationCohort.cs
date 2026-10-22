using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed record PackageInstallationCompletion(bool Success, bool Cancelled, Exception? Failure);

/// <summary>Owns cancellation and resource cleanup for the two jobs of one approved installation.</summary>
internal sealed class PackageInstallationCohort
{
    private readonly BackgroundTaskQueueService _queue;
    private readonly Guid? _runtimeId;
    private readonly Guid _packageId = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BackgroundTaskSnapshot> _terminal = [];
    private readonly Func<ValueTask> _release;
    private readonly Func<Exception, Task>? _notifyFailure;
    private readonly Func<Exception?>? _resourceFailure;
    private readonly TaskCompletionSource<PackageInstallationCompletion> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _ownerRegistration;
    private Exception? _failure;
    private bool _cancelled;
    private bool _enrolled;
    private bool _finishing;

    private PackageInstallationCohort(BackgroundTaskQueueService queue, bool hasRuntime,
        Func<ValueTask> release, Func<Exception, Task>? notifyFailure, Func<Exception?>? resourceFailure)
    {
        _queue = queue;
        _runtimeId = hasRuntime ? Guid.NewGuid() : null;
        _release = release;
        _notifyFailure = notifyFailure;
        _resourceFailure = resourceFailure;
    }

    public BackgroundTaskSnapshot PackageJob { get; private set; } = null!;
    public BackgroundTaskSnapshot? RuntimeJob { get; private set; }
    public Task<PackageInstallationCompletion> Completion => _completion.Task;

    internal static PackageInstallationCohort Enqueue(BackgroundTaskQueueService queue, string title,
        IReadOnlyDictionary<string, string> metadata,
        Func<BackgroundTaskContext, Task>? installRuntime,
        Func<BackgroundTaskContext, Task> installPackage,
        CancellationToken ownerToken, Func<ValueTask> release,
        Func<Exception, Task>? notifyFailure = null, Func<Exception?>? resourceFailure = null,
        BackgroundTaskIndicator indicator = BackgroundTaskIndicator.Packages)
    {
        ownerToken.ThrowIfCancellationRequested();
        var cohort = new PackageInstallationCohort(queue, installRuntime is not null, release, notifyFailure, resourceFailure);
        var installationId = Guid.NewGuid().ToString("N");
        var requests = new List<QueuedBackgroundTask>();
        if (installRuntime is not null)
            requests.Add(new(cohort._runtimeId!.Value, Request("Download & install package runtimes", "runtime",
                context => cohort.RunAsync(context, installRuntime, ownerToken))));
        requests.Add(new(cohort._packageId, Request(title, "package",
            context => cohort.RunAsync(context, installPackage, ownerToken)),
            installRuntime is null ? "Queued" : "Queued — waiting for runtime"));

        queue.TaskChanged += cohort.OnChanged;
        cohort._ownerRegistration = ownerToken.Register(cohort.Cancel);
        try
        {
            var snapshots = queue.EnqueueBatch(requests, ownerToken);
            cohort.PackageJob = snapshots[^1];
            cohort.RuntimeJob = installRuntime is null ? null : snapshots[0];
            lock (cohort._gate) cohort._enrolled = true;
            if (ownerToken.IsCancellationRequested) cohort.Cancel();
            cohort.TryFinish();
            return cohort;
        }
        catch
        {
            queue.TaskChanged -= cohort.OnChanged;
            cohort._ownerRegistration.Unregister();
            // Preparation still owns release if enrollment is rejected.
            throw;
        }

        BackgroundTaskRequest Request(string jobTitle, string phase, Func<BackgroundTaskContext, Task> execute)
        {
            var values = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["sunder.installationId"] = installationId,
                ["sunder.installationPhase"] = phase,
            };
            return new(jobTitle, PackageOperationService.PackageStoreGroupKey, indicator,
                BackgroundTaskConcurrencyMode.SequentialWithinGroup, true, execute, values);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _failure ??= _resourceFailure?.Invoke();
            _cancelled = true;
        }
        CancelJobs();
    }

    private void CancelJobs()
    {
        var reason = _failure is null ? "Cancelled — installation cancelled" : $"Cancelled — {_failure.Message}";
        _queue.Cancel(_packageId, reason);
        if (_runtimeId is { } runtime) _queue.Cancel(runtime, reason);
    }

    private async Task RunAsync(BackgroundTaskContext context, Func<BackgroundTaskContext, Task> action, CancellationToken ownerToken)
    {
        try
        {
            ownerToken.ThrowIfCancellationRequested();
            context.CancellationToken.ThrowIfCancellationRequested();
            await action(context).ConfigureAwait(false); // API work runs inline in this queue slot.
            context.CancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _failure ??= _resourceFailure?.Invoke() ?? (error is OperationCanceledException ? null : error);
                _cancelled |= error is OperationCanceledException;
            }
            // Before this delegate returns/throws and its group slot becomes available.
            _queue.Cancel(_packageId, _failure is null ? "Cancelled — runtime installation cancelled" : $"Cancelled — {_failure.Message}");
            throw;
        }
    }

    private void OnChanged(object? sender, BackgroundTaskChangedEventArgs args)
    {
        var snapshot = args.Snapshot;
        if (snapshot.TaskId != _packageId && snapshot.TaskId != _runtimeId) return;
        var cancel = false;
        lock (_gate)
        {
            if (!snapshot.IsActive) _terminal[snapshot.TaskId] = snapshot;
            if (snapshot.State is BackgroundTaskState.Cancelled or BackgroundTaskState.Cancelling)
                _cancelled = cancel = true;
            if (snapshot.State == BackgroundTaskState.Failed)
            {
                _failure ??= new InvalidOperationException(snapshot.ErrorMessage ?? snapshot.StatusText);
                cancel = true;
            }
            if (snapshot.State == BackgroundTaskState.Queued && (_cancelled || _failure is not null)) cancel = true;
        }
        if (cancel) CancelJobs();
        TryFinish();
    }

    private void TryFinish()
    {
        lock (_gate)
        {
            if (!_enrolled || _finishing || !_terminal.ContainsKey(_packageId)
                || _runtimeId is { } runtime && !_terminal.ContainsKey(runtime)) return;
            _finishing = true;
        }
        _queue.TaskChanged -= OnChanged;
        _ownerRegistration.Unregister();
        _ = FinishAsync();
    }

    private async Task FinishAsync()
    {
        try
        {
            if (_failure is not null and not PackageOperationAlreadyReportedException && _notifyFailure is not null)
                await _notifyFailure(_failure).ConfigureAwait(false);
        }
        catch (Exception error) { AppSessionLog.WriteError("Could not publish an installation failure.", error); }
        finally
        {
            try { await _release().ConfigureAwait(false); }
            catch (Exception error) { AppSessionLog.WriteError("Installation resource cleanup failed.", error); }
            _completion.TrySetResult(new(_failure is null && !_cancelled, _cancelled, _failure));
        }
    }
}
