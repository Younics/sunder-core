using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class BackgroundProcessScheduler
{
    public static BackgroundProcessWorkItem[] StartEligibleProcesses(
        IReadOnlyList<BackgroundProcessWorkItem> processes,
        int runningProcessCount,
        int maxParallelism)
    {
        List<BackgroundProcessWorkItem> processesToStart = [];
        while (runningProcessCount + processesToStart.Count < maxParallelism)
        {
            var candidate = processes.FirstOrDefault(process =>
                process.State == BackgroundProcessState.Queued
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
        BackgroundProcessWorkItem candidate,
        IEnumerable<BackgroundProcessWorkItem> processes,
        IReadOnlyList<BackgroundProcessWorkItem> processesToStart)
    {
        return processes.Concat(processesToStart)
            .Any(process =>
                process.ProcessId != candidate.ProcessId
                && process.State is BackgroundProcessState.Running or BackgroundProcessState.Cancelling
                && string.Equals(process.Request.GroupKey, candidate.Request.GroupKey, StringComparison.OrdinalIgnoreCase)
                && (process.Request.ConcurrencyMode == BackgroundProcessConcurrencyMode.SequentialWithinGroup
                    || candidate.Request.ConcurrencyMode == BackgroundProcessConcurrencyMode.SequentialWithinGroup));
    }
}
