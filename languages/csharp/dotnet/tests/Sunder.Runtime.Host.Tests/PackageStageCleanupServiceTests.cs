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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_WhenRuntimeShutdownClosesAdmissionBeforeHostStops_CompletesNormally(bool alreadyQueued)
    {
        await using var gate = new RuntimeOperationGate();
        await using var current = await gate.EnterAsync();
        var installedSweepCalled = false;
        var waitingSweep = alreadyQueued ? RunSweep() : null;
        if (waitingSweep is not null) Assert.False(waitingSweep.IsCompleted);
        var shutdown = gate.ShutdownAsync(() => Task.CompletedTask);
        try
        {
            Assert.True(current.CancellationToken.IsCancellationRequested);
            await (waitingSweep ?? RunSweep()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(installedSweepCalled);
        }
        finally
        {
            await current.DisposeAsync();
            await shutdown;
        }

        Task RunSweep() => PackageStageCleanupService.RunSweepAsync(
            async () => { await using var operation = await gate.EnterAsync(); },
            () =>
            {
                installedSweepCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None,
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sweep_WhenGateIsFenced_PropagatesFailureEvenDuringShutdown(
        bool alreadyQueued, bool signalShutdown)
    {
        await using var gate = new RuntimeOperationGate();
        await using var current = await gate.EnterAsync();
        using var applicationStopping = new CancellationTokenSource();
        if (signalShutdown) applicationStopping.Cancel();
        var waitingSweep = alreadyQueued ? RunSweep() : null;
        if (waitingSweep is not null) Assert.False(waitingSweep.IsCompleted);
        gate.FenceForIntegrityFailure();
        var shutdown = signalShutdown ? gate.ShutdownAsync(() => Task.CompletedTask) : Task.CompletedTask;
        try
        {
            var failure = await Assert.ThrowsAsync<RuntimeOperationRejectedException>(() =>
                (waitingSweep ?? RunSweep()).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(RuntimeOperationRejection.IntegrityFence, failure.Reason);
        }
        finally
        {
            await current.DisposeAsync();
            await shutdown;
        }

        Task RunSweep() => PackageStageCleanupService.RunSweepAsync(
            async () => { await using var operation = await gate.EnterAsync(); },
            () => throw new InvalidOperationException("The installed sweep must not run after a fence."),
            CancellationToken.None,
            applicationStopping.Token);
    }

    [Fact]
    public async Task Sweep_WhenQueuedCallerCancels_PropagatesCancellation()
    {
        await using var gate = new RuntimeOperationGate();
        await using var current = await gate.EnterAsync();
        using var caller = new CancellationTokenSource();
        var sweep = PackageStageCleanupService.RunSweepAsync(
            async () => { await using var operation = await gate.EnterAsync(caller.Token); },
            () => throw new InvalidOperationException("The installed sweep must not run after cancellation."),
            CancellationToken.None,
            CancellationToken.None);
        Assert.False(sweep.IsCompleted);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(current.CancellationToken.IsCancellationRequested);
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
