namespace Sunder.App.Services;

internal sealed class AppShutdownCoordinator
{
    private readonly object _syncRoot = new();
    private readonly OwnedTaskObserver _ownedTasks;
    private readonly TimeSpan _ownedTaskBudget;
    private Task? _shutdownTask;

    public bool OwnedTasksDrained { get; private set; }

    public AppShutdownCoordinator(
        OwnedTaskObserver ownedTasks,
        TimeSpan? ownedTaskBudget = null)
    {
        _ownedTasks = ownedTasks;
        _ownedTaskBudget = ownedTaskBudget ?? AppShutdownBudgets.OwnedTasks;
    }

    public Task ShutdownAsync(Func<Task> shutdown)
    {
        lock (_syncRoot)
        {
            return _shutdownTask ??= ShutdownCoreAsync(shutdown);
        }
    }

    private async Task ShutdownCoreAsync(Func<Task> shutdown)
    {
        // Closing and ShutdownRequested handlers must return before cleanup can re-enter Window.Close.
        await Task.Yield();
        OwnedTasksDrained = await BoundedCleanup.RunAsync(
            "stopping application startup tasks",
            _ownedTasks.StopAsync,
            _ownedTaskBudget).ConfigureAwait(false);
        await shutdown().ConfigureAwait(false);
    }
}
