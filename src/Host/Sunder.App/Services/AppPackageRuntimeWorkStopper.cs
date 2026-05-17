namespace Sunder.App.Services;

internal sealed class AppPackageRuntimeWorkStopper(
    AppPackageBackgroundServiceCoordinator backgroundServices,
    BackgroundProcessQueueService backgroundProcessQueue)
{
    public async Task StopPackageWorkAsync(string packageId, CancellationToken cancellationToken)
    {
        await backgroundServices.StopAsync(packageId, cancellationToken).ConfigureAwait(false);
        await backgroundProcessQueue.CancelPackageProcessesAsync(packageId, CancellationToken.None).ConfigureAwait(false);
    }
}
