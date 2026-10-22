using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class BackgroundTaskScheduler
{
    public static BackgroundTaskWorkItem[] StartEligibleProcesses(
        IReadOnlyList<BackgroundTaskWorkItem> processes,
        int runningProcessCount,
        int maxParallelism)
    {
        List<BackgroundTaskWorkItem> processesToStart = [];
        while (runningProcessCount + processesToStart.Count < maxParallelism)
        {
            var candidate = processes.FirstOrDefault(process =>
                process.State == BackgroundTaskState.Queued
                && !IsGroupBlocked(process, processes, processesToStart));
            if (candidate is null)
            {
                break;
            }

            candidate.MarkStarting(DateTimeOffset.UtcNow);
            processesToStart.Add(candidate);
        }

        return processesToStart.ToArray();
    }

    private static bool IsGroupBlocked(
        BackgroundTaskWorkItem candidate,
        IEnumerable<BackgroundTaskWorkItem> processes,
        IReadOnlyList<BackgroundTaskWorkItem> processesToStart)
    {
        return processes.Concat(processesToStart)
            .Any(process =>
                process.TaskId != candidate.TaskId
                && process.State is BackgroundTaskState.Running or BackgroundTaskState.Cancelling
                && string.Equals(process.Request.GroupKey, candidate.Request.GroupKey, StringComparison.OrdinalIgnoreCase)
                && (process.Request.ConcurrencyMode == BackgroundTaskConcurrencyMode.SequentialWithinGroup
                    || candidate.Request.ConcurrencyMode == BackgroundTaskConcurrencyMode.SequentialWithinGroup));
    }
}
