using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal enum AppPackageUiCleanupOutcome
{
    Proven,
    Unproven,
}

internal readonly record struct AppPackageUnloadResult(
    bool WasLoaded,
    AppPackageUiCleanupOutcome CleanupOutcome)
{
    public static AppPackageUnloadResult NotLoaded { get; } = new(
        WasLoaded: false,
        AppPackageUiCleanupOutcome.Proven);
}

internal sealed class AppPackageDisableCoordinator(
    AppPackageViewRegistry viewRegistry,
    AppPackageHostedViewFacade viewFacade,
    AppPackageRuntimeWorkStopper runtimeWorkStopper,
    AppPackageFaultNotifier faultNotifier,
    Func<string, bool> markPackageDisabled,
    Func<string, bool> isPackageDisabled)
{
    private readonly object _disableSyncRoot = new();
    private readonly Dictionary<string, TaskCompletionSource<AppPackageUiCleanupOutcome>> _disableOperations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppPackageUiCleanupOutcome> _completedCleanups =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task DisablePackageAsync(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception,
        Func<string, CancellationToken, bool, Task<AppPackageUnloadResult>> unloadPackageAsync,
        Func<Task<AppPackageUiCleanupOutcome>> quarantineBoundariesAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBeginDisablePackage(
                sender,
                packageId,
                message,
                origin,
                exception,
                unloadPackageAsync,
                quarantineBoundariesAsync,
                deferNotification: false,
                out var completion))
        {
            return;
        }

        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryBeginDisablePackage(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception,
        Func<string, CancellationToken, bool, Task<AppPackageUnloadResult>> unloadPackageAsync,
        Func<Task<AppPackageUiCleanupOutcome>> quarantineBoundariesAsync,
        bool deferNotification,
        out Task<AppPackageUiCleanupOutcome> completion)
    {
        TaskCompletionSource<AppPackageUiCleanupOutcome> operation;
        bool newlyDisabled;
        lock (_disableSyncRoot)
        {
            if (_disableOperations.TryGetValue(packageId, out operation!))
            {
                completion = operation.Task;
                return true;
            }
            newlyDisabled = markPackageDisabled(packageId);
            if (!newlyDisabled && !isPackageDisabled(packageId))
            {
                completion = Task.FromResult(AppPackageUiCleanupOutcome.Unproven);
                return false;
            }
            if (!newlyDisabled && _completedCleanups.TryGetValue(packageId, out var completedOutcome))
            {
                completion = Task.FromResult(completedOutcome);
                return true;
            }

            _completedCleanups.Remove(packageId);
            operation = new TaskCompletionSource<AppPackageUiCleanupOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disableOperations.Add(packageId, operation);
        }

        if (newlyDisabled)
        {
            faultNotifier.QueuePackageDisabled(sender, packageId, message, origin, exception);
            if (!deferNotification)
            {
                faultNotifier.PublishPackageDisabled(packageId);
            }
        }
        completion = operation.Task;
        _ = CompleteOperationAsync(
            packageId,
            operation,
            DisableAcceptedPackageAsync(
                packageId,
                unloadPackageAsync,
                quarantineBoundariesAsync));
        return true;
    }

    public Task CleanupDisabledPackageAsync(
        object sender,
        string packageId,
        Func<string, CancellationToken, bool, Task<AppPackageUnloadResult>> unloadPackageAsync,
        Func<Task<AppPackageUiCleanupOutcome>> quarantineBoundariesAsync)
    {
        if (!TryBeginDisablePackage(
                sender,
                packageId,
                "Inherited a package App UI quarantine.",
                PackageFailureOrigin.AppUnhandledUi,
                exception: null,
                unloadPackageAsync,
                quarantineBoundariesAsync,
                deferNotification: true,
                out var completion))
        {
            return Task.CompletedTask;
        }

        return completion;
    }

    private async Task<AppPackageUiCleanupOutcome> DisableAcceptedPackageAsync(
        string packageId,
        Func<string, CancellationToken, bool, Task<AppPackageUnloadResult>> unloadPackageAsync,
        Func<Task<AppPackageUiCleanupOutcome>> quarantineBoundariesAsync)
    {
        var boundaryCleanup = await TryQuarantineBoundariesAsync(
                packageId,
                quarantineBoundariesAsync)
            .ConfigureAwait(false);
        await viewFacade.CancelPackageViewOperationsAsync(packageId).ConfigureAwait(false);
        var viewsCleaned = await viewRegistry.RemoveCachedViewsAsync(
                packageId,
                CancellationToken.None)
            .ConfigureAwait(false);
        var unloadResult = await unloadPackageAsync(
            packageId,
            CancellationToken.None,
            true).ConfigureAwait(false);
        if (!unloadResult.WasLoaded)
        {
            await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        return boundaryCleanup == AppPackageUiCleanupOutcome.Proven && viewsCleaned
            ? unloadResult.CleanupOutcome
            : AppPackageUiCleanupOutcome.Unproven;
    }

    private async Task CompleteOperationAsync(
        string packageId,
        TaskCompletionSource<AppPackageUiCleanupOutcome> completion,
        Task<AppPackageUiCleanupOutcome> cleanup)
    {
        try
        {
            var outcome = await cleanup.ConfigureAwait(false);
            lock (_disableSyncRoot)
            {
                _completedCleanups[packageId] = outcome;
            }
            completion.TrySetResult(outcome);
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            completion.TrySetException(ex);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_disableSyncRoot)
            {
                if (_disableOperations.TryGetValue(packageId, out var activeCompletion)
                    && ReferenceEquals(activeCompletion, completion))
                {
                    _disableOperations.Remove(packageId);
                }
            }
        }
    }

    private static async Task<AppPackageUiCleanupOutcome> TryQuarantineBoundariesAsync(
        string packageId,
        Func<Task<AppPackageUiCleanupOutcome>> quarantineBoundariesAsync)
    {
        try
        {
            return await quarantineBoundariesAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Failed to replace package UI boundaries for '{packageId}'.",
                exception);
            return AppPackageUiCleanupOutcome.Unproven;
        }
    }
}
