namespace Sunder.App.Services;

internal sealed class AppPackageRuntimeWorkStopper(
    BackgroundTaskQueueService backgroundTaskQueue,
    Guid? ownerId = null,
    Func<string, CancellationToken, Task?>? retainedPackageStopper = null)
{
    public async Task StopPackageWorkAsync(string packageId, CancellationToken cancellationToken)
    {
        if (retainedPackageStopper?.Invoke(packageId, cancellationToken) is { } retained)
        {
            await retained.ConfigureAwait(false);
            return;
        }
        if (ownerId is { } generationOwnerId)
        {
            await backgroundTaskQueue
                .CancelPackageOwnerProcessesAsync(packageId, generationOwnerId, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await backgroundTaskQueue
            .CancelAllPackageProcessesAsync(packageId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public Task StopAllOwnedWorkAsync(CancellationToken cancellationToken)
        => ownerId is { } generationOwnerId
            ? backgroundTaskQueue.CancelOwnerProcessesAsync(generationOwnerId, CancellationToken.None)
            : Task.CompletedTask;
}
