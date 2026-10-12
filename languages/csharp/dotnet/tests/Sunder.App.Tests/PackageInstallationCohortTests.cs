using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageInstallationCohortTests
{
    private static readonly IReadOnlyDictionary<string, string> Metadata = new PackageOperationMetadata("demo", PackageOperationKind.InstallMarketplace, "Demo").ToMetadata();

    [Fact]
    public async Task RuntimeFirst_PackageWaitsQueuedWithoutStartingOrLosingItsStatus()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packageRan = false;
        var cleanup = 0;
        var installation = PackageInstallationCohort.Enqueue(queue, "Install Demo", Metadata, async context =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(context.CancellationToken);
        }, _ => { packageRan = true; return Task.CompletedTask; }, default, () => { cleanup++; return ValueTask.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var package = queue.GetProcess(installation.PackageJob.ProcessId)!;
        Assert.False(packageRan);
        Assert.Equal(BackgroundProcessState.Queued, package.State);
        Assert.Null(package.StartedAtUtc);
        Assert.Equal("Queued — waiting for runtime", package.StatusText);
        Assert.Equal(package.StatusText, new BackgroundProcessItemViewModel(package, _ => { }).StatusText);
        Assert.Equal(package.StatusText, PackageOperationStateProjector.FormatStatus(package));
        Assert.Single(queue.ListProcesses(), item => item.State == BackgroundProcessState.Running);
        release.SetResult();
        Assert.True((await installation.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.True(packageRan);
        Assert.Equal(1, cleanup);
    }

    [Fact]
    public async Task ImmediateRuntimeFailureSeesPackageAlreadyEnrolledAndCancelsItWithoutInvocation()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var failure = new IOException("Exact vendor download failure");
        var packageRan = false;
        var notifications = new List<Exception>();
        var cleanup = 0;
        var installation = PackageInstallationCohort.Enqueue(queue, "Install Demo", Metadata, _ =>
        {
            Assert.Single(queue.ListProcesses(), item => item.Metadata.GetValueOrDefault("sunder.installationPhase") == "package");
            throw failure;
        }, _ => { packageRan = true; return Task.CompletedTask; }, default,
            () => { cleanup++; return ValueTask.CompletedTask; }, error => { notifications.Add(error); return Task.CompletedTask; });
        var result = await installation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Same(failure, result.Failure);
        Assert.False(packageRan);
        Assert.Equal(BackgroundProcessState.Failed, queue.GetProcess(installation.RuntimeJob!.ProcessId)!.State);
        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(installation.PackageJob.ProcessId)!.State);
        Assert.Same(failure, Assert.Single(notifications));
        Assert.Equal(1, cleanup);
    }

    [Fact]
    public async Task RuntimeFailureDoesNotCancelUnrelatedQueuedMutation()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installation = PackageInstallationCohort.Enqueue(queue, "Install Demo", Metadata,
            async _ => { await fail.Task; throw new IOException("Download failed"); },
            _ => throw new InvalidOperationException("Package must never start"), default, () => ValueTask.CompletedTask);
        queue.Enqueue(new("Another user's change", PackageOperationService.PackageStoreGroupKey, BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup, true,
            context => { Assert.False(context.CancellationToken.IsCancellationRequested); unrelated.SetResult(); return Task.CompletedTask; }));
        fail.SetResult();
        Assert.False((await installation.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        await unrelated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingEitherQueuedMemberReleasesResourcesWithoutRunningDelegates(bool cancelRuntime)
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new("Unrelated running work", "other", BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup, true, context => blocker.Task.WaitAsync(context.CancellationToken)));
        var invoked = 0;
        var cleanup = 0;
        Task Run(BackgroundProcessContext _) { invoked++; return Task.CompletedTask; }
        var installation = PackageInstallationCohort.Enqueue(queue, "Install Demo", Metadata, Run, Run, default,
            () => { cleanup++; return ValueTask.CompletedTask; });
        Assert.True(queue.Cancel(cancelRuntime ? installation.RuntimeJob!.ProcessId : installation.PackageJob.ProcessId));
        var result = await installation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Equal(0, invoked);
        Assert.Equal(1, cleanup);
        blocker.SetResult();
    }

    [Fact]
    public async Task CancellingWaitingPackageCancelsItsRunningRuntimeAndDrainsBeforeRelease()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var installation = PackageInstallationCohort.Enqueue(queue, "Install Demo", Metadata, async context =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
            finally { stopped = true; }
        }, _ => throw new InvalidOperationException("Package must never start"), default,
            () => { Assert.True(stopped); return ValueTask.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Cancel(installation.PackageJob.ProcessId);
        Assert.True((await installation.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Cancelled);
        Assert.True(stopped);
    }

    [Fact]
    public void CancelledBeforeEnrollmentCreatesNoJobs()
    {
        using var queue = new BackgroundProcessQueueService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PackageInstallationCohort.Enqueue(queue, "Install Demo", Metadata,
            _ => Task.CompletedTask, _ => Task.CompletedTask, cancellation.Token, () => ValueTask.CompletedTask));
        Assert.Empty(queue.ListProcesses());
    }
}
