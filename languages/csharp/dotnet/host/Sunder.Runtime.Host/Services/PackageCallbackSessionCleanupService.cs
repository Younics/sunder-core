using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageCallbackSessionCleanupService(
    RuntimeSessionOwner sessions,
    RuntimeAuthPolicyOptions policy,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(policy.SessionSweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            sessions.Callbacks.SweepExpired(timeProvider.GetUtcNow());
        }
    }
}
