using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStageCleanupService(
    PackageSessionLifecycleService sessions,
    InstalledPackageLifecycleService installedPackages,
    RuntimeLifecyclePolicyOptions policy,
    TimeProvider timeProvider,
    IHostApplicationLifetime? applicationLifetime = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(policy.StageSweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            await RunSweepAsync(
                () => sessions.SweepStagesAsync(now, stoppingToken),
                () => installedPackages.SweepStagesAsync(now, stoppingToken),
                stoppingToken,
                applicationLifetime?.ApplicationStopping ?? CancellationToken.None);
        }
    }

    internal static async Task RunSweepAsync(
        Func<Task> sweepPackageStages,
        Func<Task> sweepInstalledPackageStages,
        CancellationToken backgroundServiceStopping,
        CancellationToken applicationStopping)
    {
        try
        {
            await sweepPackageStages();
            await sweepInstalledPackageStages();
        }
        catch (RuntimeOperationRejectedException) when (
            backgroundServiceStopping.IsCancellationRequested
            || applicationStopping.IsCancellationRequested)
        {
            // A timer tick can already be in flight when the Runtime operation gate begins shutdown.
        }
    }
}
