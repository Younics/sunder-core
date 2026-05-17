using Sunder.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageDisableCoordinator(
    AppPackageViewRegistry viewRegistry,
    AppPackageExtensionCatalog extensionCatalog,
    AppPackageRuntimeWorkStopper runtimeWorkStopper,
    AppPackageFaultNotifier faultNotifier,
    Func<string, bool> markPackageDisabled)
{
    public async Task DisablePackageAsync(
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

        extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageFaulted);
        await viewRegistry.RemoveCachedViewsAsync(packageId, cancellationToken);
        await faultNotifier.NotifyPackageDisabledAsync(sender, packageId, message, origin, exception);
        return true;
    }
}
