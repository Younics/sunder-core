namespace Sunder.App.Services;

internal sealed class AppPackageRuntimeWorkStopper(BackgroundProcessQueueService backgroundProcessQueue)
{
    public async Task StopPackageWorkAsync(string packageId, CancellationToken cancellationToken)
    {
        await backgroundProcessQueue.CancelPackageProcessesAsync(packageId, CancellationToken.None).ConfigureAwait(false);
    }
}
