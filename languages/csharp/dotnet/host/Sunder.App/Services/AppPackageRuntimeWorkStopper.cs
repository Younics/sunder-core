namespace Sunder.App.Services;

internal sealed class AppPackageRuntimeWorkStopper(
    BackgroundProcessQueueService backgroundProcessQueue,
    Guid? ownerId = null)
{
    public async Task StopPackageWorkAsync(string packageId, CancellationToken cancellationToken)
    {
        if (ownerId is { } generationOwnerId)
        {
            await backgroundProcessQueue
                .CancelPackageOwnerProcessesAsync(packageId, generationOwnerId, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await backgroundProcessQueue
            .CancelAllPackageProcessesAsync(packageId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public Task StopAllOwnedWorkAsync(CancellationToken cancellationToken)
        => ownerId is { } generationOwnerId
            ? backgroundProcessQueue.CancelOwnerProcessesAsync(generationOwnerId, CancellationToken.None)
            : Task.CompletedTask;
}
