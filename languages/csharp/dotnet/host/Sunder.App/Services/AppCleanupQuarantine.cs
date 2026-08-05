using System.Collections.Concurrent;

namespace Sunder.App.Services;

internal static class AppCleanupQuarantine
{
    private static readonly ConcurrentDictionary<Task, string> PendingTasks = new();
    private static readonly ConcurrentDictionary<object, string> RetainedResources = new();

    internal static int Count => PendingTasks.Count;

    public static void Retain(object resource, string reason)
    {
        ArgumentNullException.ThrowIfNull(resource);
        RetainedResources.TryAdd(resource, reason);
    }

    public static void Retain(Task task, string operation)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!PendingTasks.TryAdd(task, operation))
        {
            return;
        }

        task.ContinueWith(
            completed =>
            {
                PendingTasks.TryRemove(completed, out var retainedOperation);
                if (completed.Exception is { } exception)
                {
                    AppSessionLog.WriteError(
                        $"Quarantined cleanup failed after its budget expired while {retainedOperation ?? operation}.",
                        exception.Flatten());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal static class AppShutdownBudgets
{
    public static readonly TimeSpan OwnedTasks = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RuntimeSubscription = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BackgroundProcesses = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan UiCleanup = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PackageHost = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ServiceProvider = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GenerationRetirement = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetirementDrain = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LogFlush = TimeSpan.FromSeconds(2);
}

internal static class BoundedCleanup
{
    public static async Task<bool> RunAsync(
        string operation,
        Func<Task> cleanup,
        TimeSpan budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        Task cleanupTask;
        try
        {
            cleanupTask = cleanup();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed while {operation}.", ex);
            return false;
        }

        try
        {
            await cleanupTask.WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException ex)
        {
            AppSessionLog.WriteError(
                $"Cleanup exceeded its {budget.TotalSeconds:0.###} second budget while {operation}; it was quarantined.",
                ex);
            AppCleanupQuarantine.Retain(cleanupTask, operation);
            return false;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed while {operation}.", ex);
            return false;
        }
    }
}
