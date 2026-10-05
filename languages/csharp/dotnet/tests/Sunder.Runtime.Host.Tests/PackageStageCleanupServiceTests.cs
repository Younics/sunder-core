using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStageCleanupServiceTests
{
    [Fact]
    public async Task Sweep_WhenOperationGateRejectsAfterApplicationStopping_CompletesNormally()
    {
        using var applicationStopping = new CancellationTokenSource();
        applicationStopping.Cancel();
        var installedSweepCalled = false;

        await PackageStageCleanupService.RunSweepAsync(
            () => throw new RuntimeOperationRejectedException(),
            () =>
            {
                installedSweepCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            applicationStopping.Token);

        Assert.False(installedSweepCalled);
    }

    [Fact]
    public async Task Sweep_WhenOperationGateRejectsAfterBackgroundServiceStopping_CompletesNormally()
    {
        using var backgroundServiceStopping = new CancellationTokenSource();
        backgroundServiceStopping.Cancel();

        await PackageStageCleanupService.RunSweepAsync(
            () => throw new RuntimeOperationRejectedException(),
            () => Task.CompletedTask,
            backgroundServiceStopping.Token,
            CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_WhenOperationGateRejectsWhileRunning_PropagatesFailure()
    {
        await Assert.ThrowsAsync<RuntimeOperationRejectedException>(() =>
            PackageStageCleanupService.RunSweepAsync(
                () => throw new RuntimeOperationRejectedException(),
                () => Task.CompletedTask,
                CancellationToken.None,
                CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_WhenDifferentFailureOccursDuringShutdown_PropagatesFailure()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PackageStageCleanupService.RunSweepAsync(
                () => throw new InvalidOperationException("Unexpected sweep failure."),
                () => Task.CompletedTask,
                stopping.Token,
                CancellationToken.None));
    }
}
