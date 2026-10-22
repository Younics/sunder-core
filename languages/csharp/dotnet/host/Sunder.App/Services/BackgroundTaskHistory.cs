using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class BackgroundTaskHistory
{
    public static void TrimCompleted(ICollection<BackgroundTaskWorkItem> processes, int maxCompletedHistory)
    {
        var completed = processes
            .Where(process => process.State is BackgroundTaskState.Completed or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled)
            .OrderByDescending(process => process.CompletedAtUtc)
            .Skip(maxCompletedHistory)
            .ToArray();
        foreach (var item in completed)
        {
            processes.Remove(item);
        }
    }
}
