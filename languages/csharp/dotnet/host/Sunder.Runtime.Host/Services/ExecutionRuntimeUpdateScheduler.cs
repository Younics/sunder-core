using System.Security.Cryptography;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeUpdateScheduler(ExecutionRuntimeUpdateService updates, ExecutionRuntimeManagementState state,
    IPolicyContinuousClock continuousClock, TimeProvider clock, IHostApplicationLifetime lifetime,
    ILogger<ExecutionRuntimeUpdateScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        PolicyClockReading? previous = null;
        var next = clock.GetUtcNow();
        var retries = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reading = continuousClock.Read();
                var resumed = previous is null || !reading.SameEpoch(previous) || reading.Ticks < previous.Ticks
                    || (Int128)reading.Ticks - previous.Ticks > (Int128)reading.Frequency * 120;
                previous = reading;
                var settings = (await state.ReadAsync(stoppingToken)).Settings;
                if (settings.AutomaticUpdatesEnabled)
                {
                    await updates.ApplyPreparedWhenStoppedAsync(stoppingToken);
                    if (resumed || clock.GetUtcNow() >= next)
                    {
                        await updates.CheckAutomaticallyAsync(stoppingToken);
                        var result = await state.ReadAsync(stoppingToken);
                        retries = result.LastErrorCode is null ? 0 : Math.Min(retries + 1, 8);
                        var delay = retries == 0 ? TimeSpan.FromHours(6) + TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(601))
                            : TimeSpan.FromSeconds(Math.Min(900, 5 * (1 << retries)));
                        next = clock.GetUtcNow() + delay;
                        await state.ChangeAsync(value => value with { NextCheckAtUtc = next }, stoppingToken);
                    }
                }
                else next = clock.GetUtcNow();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* Turning binary updates off cancels acquisition, not security refresh. */ }
            catch (Exception error)
            {
                logger.LogWarning("Automatic runtime maintenance remains deferred ({FailureType}).", error.GetType().Name);
                next = clock.GetUtcNow().AddMinutes(15);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
        }
    }
}
