using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal static class PackageSessionLifecycle
{
    private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(10);

    public static async Task StopBackgroundServicesAsync(
        IReadOnlyList<IPackageBackgroundService> backgroundServices,
        string packageId,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<Task>? operationStarted = null)
        => await StopBackgroundServicesAsync(
            backgroundServices.Select(service => (packageId, service)).ToArray(),
            logger,
            timeout ?? DefaultCleanupTimeout,
            cancellationToken,
            operationStarted);

    public static async Task StopBackgroundServicesAsync(
        IReadOnlyList<(string PackageId, IPackageBackgroundService Service)> backgroundServices,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<Task>? operationStarted = null)
    {
        var startedAt = Stopwatch.GetTimestamp();
        for (var index = 0; index < backgroundServices.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            var (packageId, backgroundService) = backgroundServices[index];
            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            var servicesRemaining = backgroundServices.Count - index;
            var serviceTimeout = remaining > TimeSpan.Zero
                ? TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / servicesRemaining))
                : TimeSpan.Zero;
            using var timeoutCancellation = new CancellationTokenSource();
            timeoutCancellation.CancelAfter(serviceTimeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            var stopTask = Task.Run(
                () => backgroundService.StopAsync(deadline.Token),
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
                    "Background service cleanup for package {PackageId} exceeded its deadline or was cancelled",
                    packageId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to stop background service while rolling back package {PackageId}",
                    packageId);
            }
        }
    }

    public static async Task DisposeOwnedServiceProviderAsync(IServiceProvider serviceProvider)
    {
        var disposal = Task.Run(async () =>
        {
            if (serviceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
                return;
            }

            if (serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        });
        try
        {
            await disposal.WaitAsync(DefaultCleanupTimeout);
        }
        catch
        {
            if (!disposal.IsCompleted)
            {
                _ = ObserveAsync(disposal, "unknown", null, "dispose");
            }
            throw;
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
                "Deferred background service {Operation} failed for package {PackageId}",
                operation,
                packageId);
        }
    }

}
