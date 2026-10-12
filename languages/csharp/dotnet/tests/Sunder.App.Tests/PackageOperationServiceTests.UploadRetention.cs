using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;

namespace Sunder.App.Tests;

public sealed partial class PackageOperationServiceTests
{
    [Fact]
    public async Task RetentionCoversCombinedConfirmationQueuedWaitAndRuntimeDownload()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var clock = new ManualTimeProvider();
        var engine = new RuntimeManagementTestClient();
        var client = new FakeRuntimeApiClient { PrerequisiteClient = engine };
        var ready = false;
        var requests = new List<RuntimePackageRuntimePrerequisiteRequest>();
        engine.PreviewPrerequisites = (request, _) => { requests.Add(request); return Task.FromResult(RuntimeManagementTestClient.Prerequisites(ready)); };
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.InstallHandler = async (_, token) =>
        {
            downloading.SetResult();
            await download.Task.WaitAsync(token);
            ready = true;
            return new(true, null, "Ready", "install", null, false);
        };
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(queue, new FakeRuntimeApiClientFactory(client), (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")),
            confirmLocalPackage: (_, _) => { shown.SetResult(); return approve.Task; }, uploadRetentionClock: clock);
        var preparing = service.EnqueueLocalInstallAsync(LocalSelection("agent.sunderpkg"));
        await shown.Task;
        Assert.Empty(queue.ListProcesses());
        Assert.Single(client.UploadRetention.Retains);
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForConditionAsync(() => client.UploadRetention.Renewals.Count == 1 && clock.PendingTimerCount == 1);
        approve.SetResult(true);
        var package = (await preparing)!;
        await downloading.Task;
        Assert.Equal(BackgroundProcessState.Queued, queue.GetProcess(package.ProcessId)!.State);
        Assert.Empty(client.StagedRequests);
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForConditionAsync(() => client.UploadRetention.Renewals.Count == 2 && clock.PendingTimerCount == 1);
        download.SetResult();
        await WaitForConditionAsync(() => client.UploadRetention.Releases.Count == 1);
        Assert.Equal(BackgroundProcessState.Completed, queue.GetProcess(package.ProcessId)!.State);
        var stage = Assert.Single(client.StagedRequests);
        var handle = Assert.Single(client.UploadRetention.Releases);
        Assert.All(requests, request => Assert.Equal(handle, request.Local!.Mutations[0].UploadRetention));
        Assert.Equal(handle, stage.Mutations[0].UploadRetention);
        Assert.Equal(RuntimeManagementTestClient.Hash, stage.RuntimePrerequisiteExpectation!.SourceFingerprint);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedQueuedPackageCancelledWithoutDelegateStillReleases(bool ownerShutdown)
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new("Other work", "other", BackgroundProcessIndicator.Main, BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true, context => blocker.Task.WaitAsync(context.CancellationToken)));
        var clock = new ManualTimeProvider();
        var client = new FakeRuntimeApiClient();
        using var service = CreateService(queue, new FakeRuntimeApiClientFactory(client), (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")), uploadRetentionClock: clock);
        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection("agent.sunderpkg")))!;
        Assert.Equal(BackgroundProcessState.Queued, queue.GetProcess(operation.ProcessId)!.State);
        if (ownerShutdown) service.Dispose(); else service.CancelOperation(operation.ProcessId);
        await WaitForConditionAsync(() => client.UploadRetention.Releases.Count == 1);
        Assert.Empty(client.StagedRequests);
        Assert.Equal(0, clock.PendingTimerCount);
        Assert.Equal(0, client.UploadRetention.LiveHandleCount);
        blocker.SetResult();
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("restart")]
    [InlineData("conflict")]
    public async Task RetainedQueuedSourceCannotBeResurrectedAfterAvailabilityFailure(string failure)
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new("Other work", "other", BackgroundProcessIndicator.Main, BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true, context => blocker.Task.WaitAsync(context.CancellationToken)));
        var client = new FakeRuntimeApiClient();
        using var service = CreateService(queue, new FakeRuntimeApiClientFactory(client), (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));
        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection("agent.sunderpkg")))!;
        client.UploadRetention.Status = (_, _) => failure == "restart"
            ? throw new RuntimeClientException(System.Net.HttpStatusCode.Gone, "Host restarted", errorCode: RuntimePackageUploadRetentionErrorCodes.Unavailable)
            : Task.FromResult(PackageUploadRetentionTestClient.Unavailable(failure,
                failure == "conflict" ? RuntimePackageUploadRetentionErrorCodes.Conflict : RuntimePackageUploadRetentionErrorCodes.Unavailable));
        blocker.SetResult();
        await WaitForConditionAsync(() => client.UploadRetention.Releases.Count == 1);
        Assert.Equal(BackgroundProcessState.Failed, queue.GetProcess(operation.ProcessId)!.State);
        Assert.Empty(client.StagedRequests);
        Assert.Single(client.UploadRetention.Retains);
        Assert.Contains("Select the original package file again", queue.GetProcess(operation.ProcessId)!.ErrorMessage);
    }

    [Fact]
    public async Task RetentionRenewalFailureCancelsQueuedPackageAndDoesNotTouchUnrelatedWork()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = queue.Enqueue(new("Other work", "other", BackgroundProcessIndicator.Main, BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true, context => blocker.Task.WaitAsync(context.CancellationToken)));
        var clock = new ManualTimeProvider();
        var client = new FakeRuntimeApiClient();
        using var service = CreateService(queue, new FakeRuntimeApiClientFactory(client), (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")), uploadRetentionClock: clock);
        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection("agent.sunderpkg")))!;
        client.UploadRetention.Renew = (_, _) => Task.FromResult(PackageUploadRetentionTestClient.Unavailable("Absolute expiry"));
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForConditionAsync(() => client.UploadRetention.Releases.Count == 1);
        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(operation.ProcessId)!.State);
        Assert.Equal(BackgroundProcessState.Running, queue.GetProcess(other.ProcessId)!.State);
        Assert.Empty(client.StagedRequests);
        Assert.Equal(0, clock.PendingTimerCount);
        blocker.SetResult();
    }

    [Fact]
    public async Task ConsumptionStopsRenewalAndReleaseFailureCannotUndoCompletedPackage()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var clock = new ManualTimeProvider();
        var client = new FakeRuntimeApiClient
        {
            BeforeStage = _ => { Assert.Equal(0, clock.PendingTimerCount); clock.Advance(TimeSpan.FromHours(3)); return Task.CompletedTask; },
        };
        client.UploadRetention.Release = (_, _) => throw new IOException("Lost cleanup acknowledgement");
        using var service = CreateService(queue, new FakeRuntimeApiClientFactory(client), (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")), uploadRetentionClock: clock);
        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection("agent.sunderpkg")))!;
        await WaitForConditionAsync(() => client.UploadRetention.Releases.Count == 1);
        Assert.Equal(BackgroundProcessState.Completed, queue.GetProcess(operation.ProcessId)!.State);
        Assert.Equal(["agent"], client.InstalledPackageIds);
        Assert.Empty(client.UploadRetention.Renewals);
    }
}
