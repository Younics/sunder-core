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
        if (cancellationToken.IsCancellationRequested) return;
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        var stopToken = deadline.Token;
        var stops = new List<(string PackageId, Task Operation)>(workers.Count);
        // Stop requests target independent execution boundaries. Admit them all
        // before observing completion under one unchanged batch deadline; dividing
        // that budget into serial slices can cancel healthy Workers simply because
        // many peers exist. Strict cutover quiescence and disposal stay separate.
        foreach (var (packageId, worker) in workers)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var stopTask = Task.Run(
                () => worker.StopAsync(stopToken),
                CancellationToken.None);
            operationStarted?.Invoke(stopTask);
            stops.Add((packageId, stopTask));
        }
        foreach (var (packageId, stopTask) in stops)
        {
            try
            {
                await stopTask.WaitAsync(stopToken);
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

    public static async Task ProveStoppedAndDisposedAsync(
        IRuntimePackageWorker worker,
        string packageId,
        ILogger logger,
        TimeSpan timeout)
    {
        var cleanup = CompleteWorkerCleanupAsync(worker, packageId, logger, timeout);
        try
        {
            await cleanup.WaitAsync(timeout + timeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            _ = ObserveAsync(cleanup, packageId, logger, "cleanup proof");
            throw new RuntimeWorkerCleanupUnprovenException(
                $"Worker cleanup for package '{packageId}' could not be proven within {(timeout + timeout).TotalSeconds:0.###} seconds.",
                cleanup,
                exception);
        }
        catch (Exception exception)
        {
            throw new RuntimeWorkerCleanupUnprovenException(
                $"Worker cleanup for package '{packageId}' could not be proven.",
                cleanup,
                exception);
        }
    }

    private static async Task CompleteWorkerCleanupAsync(
        IRuntimePackageWorker worker,
        string packageId,
        ILogger logger,
        TimeSpan timeout)
    {
        var stopTask = Task.Run(
            () => worker.StopAsync(CancellationToken.None),
            CancellationToken.None);
        try
        {
            await stopTask.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(
                exception,
                "Worker stop for package {PackageId} exceeded its cleanup deadline; disposal will attempt to prove termination",
                packageId);
            _ = ObserveAsync(stopTask, packageId, logger, "stop");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Worker stop for package {PackageId} failed; disposal will attempt to prove termination",
                packageId);
        }

        await worker.DisposeAsync().ConfigureAwait(false);
        var completion = worker.GenerationCompletion
                         ?? throw new InvalidOperationException(
                             $"Worker cleanup for package '{packageId}' returned no termination proof.");
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // A faulted or cancelled generation is still proof that execution terminated.
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

internal sealed class RuntimeWorkerCleanupUnprovenException(
    string message,
    Task cleanupCompletion,
    Exception innerException) : Exception(message, innerException)
{
    public Task CleanupCompletion { get; } = cleanupCompletion;
}
