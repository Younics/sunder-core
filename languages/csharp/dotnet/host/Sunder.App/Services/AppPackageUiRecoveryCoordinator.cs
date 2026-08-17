namespace Sunder.App.Services;

internal sealed record AppPackageUiRecoveryRequest(
    string PackageId,
    string ContentHash,
    Guid FaultedGenerationId,
    Task<AppPackageUiCleanupOutcome> CleanupCompletion);

internal enum AppPackageUiRecoveryPresentationOutcome
{
    Applied,
    ContentChanged,
    TimedOut,
    Superseded,
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
    private readonly Dictionary<string, TaskCompletionSource> _activeRecoveries =
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
            ProcessRecoveryAttempts)
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
            new AppPackageUiRecoveryAttemptRegistry())
    {
    }

    internal AppPackageUiRecoveryCoordinator(
        PackageViewHostService packageViewHostService,
        Func<string, string, CancellationToken, Task<AppPackageUiRecoveryPresentationOutcome>> forcePresentationAsync,
        OwnedTaskObserver tasks,
        AppPackageUiRecoveryAttemptRegistry attemptRegistry)
    {
        _packageViewHostService = packageViewHostService;
        _forcePresentationAsync = forcePresentationAsync;
        _tasks = tasks;
        _attemptRegistry = attemptRegistry;
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
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = CreateRecoveryKey(request.PackageId, request.ContentHash);
        lock (_syncRoot)
        {
            if (_disposed || key is null || !_attemptRegistry.TryBegin(key))
            {
                return false;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRecoveries.Add(key, completion);
            _tasks.Run(
                cancellationToken => RecoverAndCompleteAsync(
                    key,
                    request,
                    completion,
                    cancellationToken),
                $"recovering package App UI for '{request.PackageId}'");
            return true;
        }
    }

    internal Task WaitForRecoveryAsync(string packageId, string contentHash)
    {
        var key = CreateRecoveryKey(packageId, contentHash);
        lock (_syncRoot)
        {
            return key is not null && _activeRecoveries.TryGetValue(key, out var completion)
                ? completion.Task
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
        => TrySchedule(request);

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
                if (_activeRecoveries.TryGetValue(key, out var activeCompletion)
                    && ReferenceEquals(activeCompletion, completion))
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
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Automatic App UI recovery stopped because cleanup failed for '{request.PackageId}'.",
                exception);
            return;
        }

        if (cleanupOutcome != AppPackageUiCleanupOutcome.Proven)
        {
            AppSessionLog.WriteInfo(
                $"Automatic App UI recovery was not attempted because cleanup could not be proven for '{request.PackageId}'.");
            return;
        }

        try
        {
            await _forcePresentationAsync(
                    request.PackageId,
                    request.ContentHash,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Automatic App UI recovery failed for '{request.PackageId}'.",
                exception);
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
}
