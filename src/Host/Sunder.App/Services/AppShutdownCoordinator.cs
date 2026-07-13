namespace Sunder.App.Services;

internal sealed class AppShutdownCoordinator(OwnedTaskObserver ownedTasks)
{
    private readonly object _syncRoot = new();
    private Task? _shutdownTask;

    public Task ShutdownAsync(Func<Task> shutdown)
    {
        lock (_syncRoot)
        {
            return _shutdownTask ??= ShutdownCoreAsync(shutdown);
        }
    }

    private async Task ShutdownCoreAsync(Func<Task> shutdown)
    {
        await ownedTasks.StopAsync();
        await shutdown();
    }
}
