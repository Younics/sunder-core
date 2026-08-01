using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageDisableCoordinator(
    AppPackageViewRegistry viewRegistry,
    AppPackageHostedViewFacade viewFacade,
    AppPackageRuntimeWorkStopper runtimeWorkStopper,
    AppPackageFaultNotifier faultNotifier,
    Func<string, bool> markPackageDisabled)
{
    private readonly object _disableSyncRoot = new();
    private readonly Dictionary<string, TaskCompletionSource> _disableOperations =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task DisablePackageAsync(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception,
        Func<string, CancellationToken, bool, Task<bool>> unloadPackageAsync,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        var ownsOperation = false;
        lock (_disableSyncRoot)
        {
            if (!_disableOperations.TryGetValue(packageId, out completion!))
            {
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disableOperations.Add(packageId, completion);
                ownsOperation = true;
            }
        }

        if (!ownsOperation)
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await DisablePackageCoreAsync(
                sender,
                packageId,
                message,
                origin,
                exception,
                unloadPackageAsync,
                cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            throw;
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

    private async Task DisablePackageCoreAsync(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception,
        Func<string, CancellationToken, bool, Task<bool>> unloadPackageAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newlyDisabled = await TryMarkPackageDisabledAsync(sender, packageId, message, origin, exception, cancellationToken);
        var unloadedPackage = await unloadPackageAsync(packageId, CancellationToken.None, true);
        if (!newlyDisabled && !unloadedPackage)
        {
            return;
        }

        if (!unloadedPackage)
        {
            await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None);
        }
    }

    private async Task<bool> TryMarkPackageDisabledAsync(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!markPackageDisabled(packageId))
        {
            return false;
        }

        await viewFacade.CancelPackageViewOperationsAsync(packageId).ConfigureAwait(false);
        await viewRegistry.RemoveCachedViewsAsync(packageId, CancellationToken.None);
        await faultNotifier.NotifyPackageDisabledAsync(sender, packageId, message, origin, exception);
        return true;
    }
}
