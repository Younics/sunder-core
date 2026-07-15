using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStageCleanupService(
    PackageSessionLifecycleService sessions,
    InstalledPackageLifecycleService installedPackages,
    RuntimeLifecyclePolicyOptions policy,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(policy.StageSweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            await sessions.SweepStagesAsync(now, stoppingToken);
            await installedPackages.SweepStagesAsync(now, stoppingToken);
        }
    }
}
