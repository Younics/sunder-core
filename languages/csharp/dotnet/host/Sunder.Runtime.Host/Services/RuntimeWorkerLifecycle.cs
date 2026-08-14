using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeWorkerLifecycle
{
    private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(10);

    public static async Task StopWorkersAsync(
        IReadOnlyList<IRuntimePackageWorker> workers,
        string packageId,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<Task>? operationStarted = null)
        => await StopWorkersAsync(
            workers.Select(worker => (packageId, worker)).ToArray(),
            logger,
            timeout ?? DefaultCleanupTimeout,
            cancellationToken,
            operationStarted);

    public static async Task StopWorkersAsync(
        IReadOnlyList<(string PackageId, IRuntimePackageWorker Worker)> workers,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<Task>? operationStarted = null)
    {
        var startedAt = Stopwatch.GetTimestamp();
        for (var index = 0; index < workers.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            var (packageId, worker) = workers[index];
            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            var workersRemaining = workers.Count - index;
            var workerTimeout = remaining > TimeSpan.Zero
                ? TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / workersRemaining))
                : TimeSpan.Zero;
            using var timeoutCancellation = new CancellationTokenSource();
            timeoutCancellation.CancelAfter(workerTimeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            var stopTask = Task.Run(
                () => worker.StopAsync(deadline.Token),
                CancellationToken.None);
            operationStarted?.Invoke(stopTask);
            try
            {
                await stopTask.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            {
                if (!stopTask.IsCompleted)
                {
                    _ = ObserveAsync(stopTask, packageId, logger, "stop");
                }
                logger.LogWarning(
                    exception,
                    "Worker cleanup for package {PackageId} exceeded its deadline or was cancelled",
                    packageId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to stop worker while rolling back package {PackageId}",
                    packageId);
            }
        }
    }

    public static async Task StopWorkersStrictAsync(
        IReadOnlyList<(string PackageId, IRuntimePackageWorker Worker)> workers,
        ILogger logger,
        TimeSpan warningTimeout,
        Action<Task>? operationStarted = null)
    {
        foreach (var (packageId, worker) in workers)
        {
            var stopTask = Task.Run(
                () => worker.StopAsync(CancellationToken.None),
                CancellationToken.None);
            operationStarted?.Invoke(stopTask);
            try
            {
                await stopTask.WaitAsync(warningTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(
                    exception,
                    "Worker quiescence for package {PackageId} exceeded its cleanup deadline; publication remains blocked until the worker exits",
                    packageId);
                await stopTask.ConfigureAwait(false);
            }
        }
    }

    private static async Task ObserveAsync(
        Task task,
        string packageId,
        ILogger? logger,
        string operation)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Deferred worker {Operation} failed for package {PackageId}",
                operation,
                packageId);
        }
    }

}
