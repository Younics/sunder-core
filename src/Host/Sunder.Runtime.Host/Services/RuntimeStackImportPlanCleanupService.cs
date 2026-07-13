using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackImportPlanCleanupService(
    RuntimeStackImportService stackImports,
    RuntimeStackPolicyOptions policy,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(policy.ImportPlanSweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            stackImports.SweepExpired();
        }
    }
}
