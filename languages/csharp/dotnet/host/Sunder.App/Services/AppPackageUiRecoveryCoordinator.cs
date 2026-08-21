namespace Sunder.App.Services;

internal sealed record AppPackageUiRecoveryRequest(
    string PackageId,
    string ContentHash,
    Guid FaultedGenerationId,
    Task<AppPackageUiCleanupOutcome> CleanupCompletion,
    AppPackageFaultNoticeIncident? FaultNotice = null);

internal enum AppPackageUiRecoveryPresentationOutcome
{
    Applied,
    ContentChanged,
    TimedOut,
    Superseded,
}

internal enum AppPackageUiRecoveryScheduleOutcome
{
    Scheduled,
    JoinedExisting,
    AlreadyAttempted,
    Unavailable,
}

internal sealed class AppPackageUiRecoveryAttemptRegistry
{
    private readonly object _syncRoot = new();
    private readonly HashSet<string> _attemptedContent = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                return _attemptedContent.Count;
            }
        }
    }

    public bool TryBegin(string key)
    {
        lock (_syncRoot)
        {
            return _attemptedContent.Add(key);
        }
    }
}

internal sealed class AppPackageUiRecoveryCoordinator : IDisposable
{
    private static readonly AppPackageUiRecoveryAttemptRegistry ProcessRecoveryAttempts = new();
    private readonly object _syncRoot = new();
    private readonly PackageViewHostService _packageViewHostService;
    private readonly Func<string, string, CancellationToken, Task<AppPackageUiRecoveryPresentationOutcome>> _forcePresentationAsync;
    private readonly OwnedTaskObserver _tasks;
    private readonly AppPackageUiRecoveryAttemptRegistry _attemptRegistry;
    private readonly TimeSpan _cleanupWaitTimeout;
    private readonly Dictionary<string, ActiveRecovery> _activeRecoveries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public AppPackageUiRecoveryCoordinator(
        PackageViewHostService packageViewHostService,
        RuntimeEventSubscriptionService runtimeEventSubscription,
        OwnedTaskObserver tasks)
        : this(
            packageViewHostService,
            runtimeEventSubscription.ForcePackageRecoveryPresentationAsync,
            tasks,
            ProcessRecoveryAttempts,
            AppShutdownBudgets.UiCleanup)
    {
    }

    internal AppPackageUiRecoveryCoordinator(
        PackageViewHostService packageViewHostService,
        Func<string, string, CancellationToken, Task<AppPackageUiRecoveryPresentationOutcome>> forcePresentationAsync,
        OwnedTaskObserver tasks)
        : this(
            packageViewHostService,
            forcePresentationAsync,
            tasks,
            new AppPackageUiRecoveryAttemptRegistry(),
            AppShutdownBudgets.UiCleanup)
    {
    }

    internal AppPackageUiRecoveryCoordinator(
        PackageViewHostService packageViewHostService,
        Func<string, string, CancellationToken, Task<AppPackageUiRecoveryPresentationOutcome>> forcePresentationAsync,
        OwnedTaskObserver tasks,
        AppPackageUiRecoveryAttemptRegistry attemptRegistry)
        : this(
            packageViewHostService,
            forcePresentationAsync,
            tasks,
            attemptRegistry,
            AppShutdownBudgets.UiCleanup)
    {
    }

    internal AppPackageUiRecoveryCoordinator(
        PackageViewHostService packageViewHostService,
        Func<string, string, CancellationToken, Task<AppPackageUiRecoveryPresentationOutcome>> forcePresentationAsync,
        OwnedTaskObserver tasks,
        AppPackageUiRecoveryAttemptRegistry attemptRegistry,
        TimeSpan cleanupWaitTimeout)
    {
        if (cleanupWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupWaitTimeout));
        }
        _packageViewHostService = packageViewHostService;
        _forcePresentationAsync = forcePresentationAsync;
        _tasks = tasks;
        _attemptRegistry = attemptRegistry;
        _cleanupWaitTimeout = cleanupWaitTimeout;
        _packageViewHostService.PackageUiRecoveryRequested += OnPackageUiRecoveryRequested;
    }

    internal int AttemptedRecoveryCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _attemptRegistry.Count;
            }
        }
    }

    internal bool TrySchedule(AppPackageUiRecoveryRequest request)
        => Schedule(request) == AppPackageUiRecoveryScheduleOutcome.Scheduled;

    private AppPackageUiRecoveryScheduleOutcome Schedule(AppPackageUiRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = CreateRecoveryKey(request.PackageId, request.ContentHash);
        lock (_syncRoot)
        {
            if (_disposed || key is null)
            {
                return AppPackageUiRecoveryScheduleOutcome.Unavailable;
            }
            if (_activeRecoveries.TryGetValue(key, out var activeRecovery))
            {
                return activeRecovery.FaultedGenerationId == request.FaultedGenerationId
                    ? AppPackageUiRecoveryScheduleOutcome.JoinedExisting
                    : AppPackageUiRecoveryScheduleOutcome.AlreadyAttempted;
            }
            if (!_attemptRegistry.TryBegin(key))
            {
                return AppPackageUiRecoveryScheduleOutcome.AlreadyAttempted;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRecoveries.Add(
                key,
                new ActiveRecovery(request.FaultedGenerationId, completion));
            _tasks.Run(
                cancellationToken => RecoverAndCompleteAsync(
                    key,
                    request,
                    completion,
                cancellationToken),
                $"recovering package App UI for '{request.PackageId}'");
            return AppPackageUiRecoveryScheduleOutcome.Scheduled;
        }
    }

    internal Task WaitForRecoveryAsync(string packageId, string contentHash)
    {
        var key = CreateRecoveryKey(packageId, contentHash);
        lock (_syncRoot)
        {
            return key is not null && _activeRecoveries.TryGetValue(key, out var recovery)
                ? recovery.Completion.Task
                : Task.CompletedTask;
        }
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
        }

        _packageViewHostService.PackageUiRecoveryRequested -= OnPackageUiRecoveryRequested;
    }

    private void OnPackageUiRecoveryRequested(AppPackageUiRecoveryRequest request)
    {
        var outcome = Schedule(request);
        if (outcome is AppPackageUiRecoveryScheduleOutcome.AlreadyAttempted
            or AppPackageUiRecoveryScheduleOutcome.Unavailable)
        {
            request.FaultNotice?.Publish();
        }
    }

    private async Task RecoverAndCompleteAsync(
        string key,
        AppPackageUiRecoveryRequest request,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecoverAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_activeRecoveries.TryGetValue(key, out var activeRecovery)
                    && ReferenceEquals(activeRecovery.Completion, completion))
                {
                    _activeRecoveries.Remove(key);
                }
            }
            completion.TrySetResult();
        }
    }

    private async Task RecoverAsync(
        AppPackageUiRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        AppPackageUiCleanupOutcome cleanupOutcome;
        try
        {
            cleanupOutcome = await request.CleanupCompletion
                .WaitAsync(_cleanupWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) when (!request.CleanupCompletion.IsCompleted)
        {
            AppSessionLog.WriteError(
                $"Automatic App UI recovery stopped because cleanup timed out for '{request.PackageId}'.");
            AppCleanupQuarantine.Retain(
                request.CleanupCompletion,
                $"finishing timed-out App UI recovery cleanup for '{request.PackageId}'");
            request.FaultNotice?.Publish();
            return;
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            request.FaultNotice?.Resolve();
            throw;
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Automatic App UI recovery stopped because cleanup failed for '{request.PackageId}'.",
                exception);
            request.FaultNotice?.Publish();
            return;
        }

        if (cleanupOutcome != AppPackageUiCleanupOutcome.Proven)
        {
            AppSessionLog.WriteInfo(
                $"Automatic App UI recovery was not attempted because cleanup could not be proven for '{request.PackageId}'.");
            request.FaultNotice?.Publish();
            return;
        }

        try
        {
            var outcome = await _forcePresentationAsync(
                    request.PackageId,
                    request.ContentHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome == AppPackageUiRecoveryPresentationOutcome.TimedOut)
            {
                request.FaultNotice?.Publish();
            }
            else
            {
                request.FaultNotice?.Resolve();
            }
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            request.FaultNotice?.Resolve();
            throw;
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Automatic App UI recovery failed for '{request.PackageId}'.",
                exception);
            request.FaultNotice?.Publish();
        }
    }

    private static string? CreateRecoveryKey(string? packageId, string? contentHash)
    {
        if (string.IsNullOrWhiteSpace(packageId)
            || string.IsNullOrWhiteSpace(contentHash))
        {
            return null;
        }
        var normalizedPackageId = packageId.Trim();
        var normalizedContentHash = contentHash.Trim();
        return $"{normalizedPackageId}\0{normalizedContentHash}";
    }

    private sealed record ActiveRecovery(
        Guid FaultedGenerationId,
        TaskCompletionSource Completion);
}
