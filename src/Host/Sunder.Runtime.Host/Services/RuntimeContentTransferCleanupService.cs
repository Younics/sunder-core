using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeContentTransferCleanupService(
    RuntimeContentTransferStore transfers,
    RuntimeTransportPolicyOptions policy,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(policy.ContentTransferSweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            transfers.SweepExpired(timeProvider.GetUtcNow());
        }
    }
}
