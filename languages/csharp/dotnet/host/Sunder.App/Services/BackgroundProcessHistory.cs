using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class BackgroundProcessHistory
{
    public static void TrimCompleted(ICollection<BackgroundProcessWorkItem> processes, int maxCompletedHistory)
    {
        var completed = processes
            .Where(process => process.State is BackgroundProcessState.Completed or BackgroundProcessState.Failed or BackgroundProcessState.Cancelled)
            .OrderByDescending(process => process.CompletedAtUtc)
            .Skip(maxCompletedHistory)
            .ToArray();
        foreach (var item in completed)
        {
            processes.Remove(item);
        }
    }
}
