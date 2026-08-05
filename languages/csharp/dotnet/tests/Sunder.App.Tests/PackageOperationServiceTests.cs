using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageOperationServiceTests
{
    [Fact]
    public async Task EnqueueMarketplaceInstall_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem("agent", "1.0.0")],
                [],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter,
            registryClientFactory: _ => registryClient);

        var operation = service.EnqueueMarketplaceInstall("agent", "Agent", new Uri("https://registry.example/"));

        Assert.Equal(BackgroundProcessIndicator.Packages, operation.Indicator);
        Assert.Equal(PackageOperationService.PackageStoreGroupKey, operation.GroupKey);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenArchiveChangedAfterReview_DoesNotStageInstall()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient { UploadContentHash = "changed-hash" };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueLocalInstall(
            Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"),
            "reviewed-hash",
            deleteAfterUse: false);

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Failed);

        var failed = queue.GetProcess(operation.ProcessId);
        Assert.Empty(runtimeClient.InstalledPackageIds);
        Assert.Empty(runtimeClient.CommittedStageIds);
        Assert.Contains("changed after review", failed?.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnqueueLocalInstall_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WaitsForEventPresentationAndWritesItOnce()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var stamp = new RuntimePackageStamp(runtimeInstanceId, 1);
        var runtimeClient = new FakeRuntimeApiClient { CommittedStamp = stamp };
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        await using var subscription = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var presentationCount = 0;
        subscription.InitializePresentation(
            new RuntimePackageSnapshot(runtimeInstanceId, 0, 0, RuntimeBootstrapState.Ready, [], [], [], []),
            [],
            (_, _, _, _) =>
            {
                presentationCount++;
                return Task.CompletedTask;
            });
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            subscription.WaitUntilAppliedAsync,
            notificationCenter);

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);
        await WaitForConditionAsync(() => runtimeClient.CommittedStageIds.Count == 1);
        Assert.NotEqual(BackgroundProcessState.Completed, queue.GetProcess(operation.ProcessId)?.State);

        var snapshot = new RuntimePackageSnapshot(runtimeInstanceId, 1, 1, RuntimeBootstrapState.Ready, [], [], [], []);
        await subscription.ApplySnapshotAsync(snapshot, [], ["agent"]);
        await subscription.ApplySnapshotAsync(snapshot, [], ["agent"]);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(1, presentationCount);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenLifecycleApplyFails_CompletesWithWarningNotification()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => throw new InvalidOperationException("shell refresh failed"),
            notificationCenter);

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var completed = queue.GetProcess(operation.ProcessId);
        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Contains("commit succeeded", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        var notification = Assert.Single(notificationCenter.ListNotifications());
        Assert.Equal("Package changes were not applied live", notification.Title);
        Assert.Equal(PackageNotificationSeverity.Warning, notification.Severity);
        Assert.Contains("shell refresh failed", notification.Message);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenLivePresentationIsUnavailable_ReportsCommittedStoreWarning()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            waitForPresentationAsync: (_, _) => Task.FromResult(PackagePresentationResult.Unavailable(
                "Sunder is running in the Core Shell without a live Runtime presentation.")));

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var completed = queue.GetProcess(operation.ProcessId);
        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Contains("commit succeeded", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Core Shell", completed?.StatusText, StringComparison.Ordinal);
        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Equal(PackageNotificationSeverity.Warning, Assert.Single(notificationCenter.ListNotifications()).Severity);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenPresentationWaitTimesOut_ReportsCommittedStoreWarning()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            waitForPresentationAsync: async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PackagePresentationResult.Applied;
            },
            presentationWaitTimeout: TimeSpan.FromMilliseconds(25));

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var completed = queue.GetProcess(operation.ProcessId);
        Assert.Contains("commit succeeded", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not apply it within", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtimeClient.DiscardedStageIds);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenCanceledAfterCommit_ReportsPresentationFailureWithoutDiscardingCommit()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var presentationWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            waitForPresentationAsync: async (_, cancellationToken) =>
            {
                presentationWaitStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PackagePresentationResult.Applied;
            });

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);
        await presentationWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(service.CancelOperation(operation.ProcessId));
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Failed);

        var failed = queue.GetProcess(operation.ProcessId);
        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Single(runtimeClient.CommittedStageIds);
        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Contains("commit succeeded", failed?.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("presentation", failed?.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PackageNotificationSeverity.Warning, Assert.Single(notificationCenter.ListNotifications()).Severity);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenRuntimeSessionNotApplied_DoesNotApplyShellLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            RuntimeSessionApplied = false,
            RequiresAppRestart = true,
        };
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var completed = queue.GetProcess(operation.ProcessId);
        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Empty(lifecycleApplications);
        Assert.Contains("could not load the package changes", completed?.StatusText);
        var notification = Assert.Single(notificationCenter.ListNotifications());
        Assert.Equal("Package changes were not loaded", notification.Title);
        Assert.Equal(PackageNotificationSeverity.Warning, notification.Severity);
    }

    [Fact]
    public async Task EnqueueEnable_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");

        Assert.Equal(BackgroundProcessIndicator.Packages, operation.Indicator);
        Assert.False(operation.CanCancel);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.EnabledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueDisable_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueDisable("agent", "Agent");

        Assert.Equal(BackgroundProcessIndicator.Packages, operation.Indicator);
        Assert.False(operation.CanCancel);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.DisabledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueMarketplaceUpdate_UsesRecordedRuntimeUpdatePolicy()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));

        var operation = service.EnqueueMarketplaceUpdate(
            "agent",
            "Agent",
            "2.0.0");
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var request = Assert.Single(runtimeClient.UpdateRequests);
        Assert.Equal("agent", request.PackageId);
        Assert.Null(request.RegistryOrigin);
    }

    [Fact]
    public async Task EnqueueUpdateAll_DoesNotSupplyAnOriginOverride()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));

        var operation = service.EnqueueUpdateAll();
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var request = Assert.Single(runtimeClient.UpdateRequests);
        Assert.Null(request.PackageId);
        Assert.Null(request.RegistryOrigin);
    }

    [Fact]
    public async Task EnqueueUninstall_PreflightsPlanWithoutImplicitCascadeConsent()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            UninstallPlan = new PackageUninstallPlan(
                "agent",
                [new PackageUninstallPlanPackage("agent", "Agent", "1.0.0")],
                [new PackageUninstallPlanPackage("agent.extension", "Extension", "1.0.0")],
                ["agent", "agent.extension"],
                new PackageLifecycleChangeSet(
                    ["agent", "agent.extension"],
                    ["agent", "agent.extension"],
                    ["agent", "agent.extension"],
                    ["agent", "agent.extension"],
                    false),
                PackageUninstallDataBehavior.Retain,
                ["agent", "agent.extension"],
                new string('b', 64)),
        };
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));

        var operation = service.EnqueueUninstall("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var mutation = Assert.Single(Assert.Single(runtimeClient.StagedRequests).Mutations);
        Assert.False(mutation.AllowCascade);
        Assert.Equal(new string('b', 64), mutation.ConfirmationToken);
    }

    [Fact]
    public async Task EnqueueEnable_WhenCommitAndDiscardFail_PreservesCommitFailure()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            CommitException = new InvalidOperationException("primary commit failure"),
            DiscardException = new InvalidOperationException("secondary discard failure"),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Failed);

        var failed = queue.GetProcess(operation.ProcessId);
        Assert.Contains("primary commit failure", failed?.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secondary discard failure", failed?.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueueEnable_WhenCommitResponseIsLostAfterServerCommit_ReconcilesWithoutDiscard(
        bool cancellation)
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            CommitException = cancellation
                ? new OperationCanceledException("response cancelled after commit")
                : new HttpRequestException("response lost after commit"),
            StageStatusAfterCommitException = RuntimePackageStageState.Committed,
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Equal(1, runtimeClient.StageStatusCallCount);
    }

    [Fact]
    public async Task EnqueueEnable_WhenAmbiguousCommitIsStillCommitting_LeavesStageIntact()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            CommitException = new HttpRequestException("response lost while committing"),
            StageStatusAfterCommitException = RuntimePackageStageState.Committing,
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Failed);

        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Equal(1, runtimeClient.StageStatusCallCount);
    }

    [Fact]
    public async Task EnqueueMarketplaceInstall_WhenPackageAlreadyHasActiveOperation_ReturnsExistingOperation()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem("agent", "1.0.0")],
                [],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient
        {
            InstallDelay = TimeSpan.FromSeconds(1),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            registryClientFactory: _ => registryClient);

        var first = service.EnqueueMarketplaceInstall("agent", "Agent", new Uri("https://registry.example/"));
        var second = service.EnqueueMarketplaceInstall("agent", "Agent", new Uri("https://registry.example/"));

        Assert.Equal(first.ProcessId, second.ProcessId);
        await service.CancelAllAsync();
    }

    [Fact]
    public async Task EnqueueEnable_WhenCalledConcurrentlyForSamePackage_ReturnsSingleOperation()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            EnableDelay = TimeSpan.FromSeconds(1),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var releaseCallers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                await releaseCallers.Task;
                return service.EnqueueEnable("agent", "Agent");
            }))
            .ToArray();

        releaseCallers.SetResult();
        var operations = await Task.WhenAll(callers);

        Assert.Single(operations.Select(operation => operation.ProcessId).Distinct());
        await service.CancelAllAsync();
    }

    [Fact]
    public async Task CancelAllAsync_DoesNotCancelNonPackageProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 2);
        var runtimeClient = new FakeRuntimeApiClient
        {
            InstallDelay = TimeSpan.FromMinutes(1),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var nonPackageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNonPackageCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonPackageProcess = queue.Enqueue(new BackgroundProcessRequest(
            "Non-package work",
            "non-package",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.ParallelWithinGroup,
            CanCancel: true,
            async _ =>
            {
                nonPackageStarted.SetResult();
                await allowNonPackageCompletion.Task;
            }));
        await nonPackageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var packageOperation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false);
        await WaitForConditionAsync(() => queue.GetProcess(packageOperation.ProcessId)?.State == BackgroundProcessState.Running);

        await service.CancelAllAsync();

        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(packageOperation.ProcessId)?.State);
        Assert.Equal(BackgroundProcessState.Running, queue.GetProcess(nonPackageProcess.ProcessId)?.State);
        allowNonPackageCompletion.SetResult();
        await WaitForConditionAsync(() => queue.GetProcess(nonPackageProcess.ProcessId)?.State == BackgroundProcessState.Completed);
    }

    [Fact]
    public async Task CancelOperation_DoesNotCancelNonPackageProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var nonPackageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNonPackageCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonPackageProcess = queue.Enqueue(new BackgroundProcessRequest(
            "Non-package work",
            "non-package",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.ParallelWithinGroup,
            CanCancel: true,
            async _ =>
            {
                nonPackageStarted.SetResult();
                await allowNonPackageCompletion.Task;
            }));
        await nonPackageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancelled = service.CancelOperation(nonPackageProcess.ProcessId);

        Assert.False(cancelled);
        Assert.Equal(BackgroundProcessState.Running, queue.GetProcess(nonPackageProcess.ProcessId)?.State);
        allowNonPackageCompletion.SetResult();
        await WaitForConditionAsync(() => queue.GetProcess(nonPackageProcess.ProcessId)?.State == BackgroundProcessState.Completed);
    }

    [Fact]
    public async Task CancelQueuedLocalInstall_DeletesOwnedReviewSnapshot()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.Enqueue(new BackgroundProcessRequest(
            "Blocking work",
            "blocking",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.ParallelWithinGroup,
            CanCancel: true,
            async _ =>
            {
                blockerStarted.SetResult();
                await releaseBlocker.Task;
            }));
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reviewDirectory = CreateTempDirectory();
        var reviewPath = Path.Combine(reviewDirectory, "package.sunderpkg");
        await File.WriteAllBytesAsync(reviewPath, [1, 2, 3]);
        var operation = service.EnqueueLocalInstall(
            reviewPath,
            "test-hash",
            deleteAfterUse: true);
        Assert.Equal(BackgroundProcessState.Queued, queue.GetProcess(operation.ProcessId)?.State);

        Assert.True(service.CancelOperation(operation.ProcessId));

        Assert.False(Directory.Exists(reviewDirectory));
        releaseBlocker.SetResult();
        await WaitForConditionAsync(() => queue.GetProcess(blocker.ProcessId)?.State == BackgroundProcessState.Completed);
    }

    [Fact]
    public void Dispose_RejectsFurtherPackageOperations()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.ListOperations());
        Assert.Throws<ObjectDisposedException>(() => service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "test-hash", deleteAfterUse: false));
        Assert.Throws<ObjectDisposedException>(() => service.CancelOperation(Guid.NewGuid()));
    }

    [Fact]
    public void Dispose_UnsubscribesFromBackgroundProcessChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var operationChangeCount = 0;
        service.OperationChanged += (_, _) => operationChangeCount++;

        service.Dispose();
        queue.Enqueue(new BackgroundProcessRequest(
            "Install Agent",
            PackageOperationService.PackageStoreGroupKey,
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask,
            new PackageOperationMetadata("agent", PackageOperationKind.InstallLocal, "Agent").ToMetadata()));

        Assert.Equal(0, operationChangeCount);
    }

    private static RegistryPackageInstallPlanItem CreatePlanItem(string packageId, string version)
        => new(
            packageId,
            CurrentVersion: null,
            version,
            IsUpdate: false,
            DeprecatedMessage: null,
            DependsOn: [],
            Targets: [],
            Artifacts:
            [
                new RegistryPackageProjectionArtifact(
                    "shared",
                    null,
                    "",
                    0,
                    $"download/{packageId}/{version}",
                    "",
                    "",
                    "",
                    1),
            ]);

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => (TClient)(object)runtimeApiClient;
    }

    private sealed class FakeRegistryApiClient : IRegistryClient
    {
        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], [], []);

        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public void Dispose() { }
    }

    private sealed class FakeRuntimeApiClient : IRuntimePackageChangeClient
    {
        private readonly Dictionary<string, PackageStoreStageRequest> _pendingStages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _uploadedPackageIds = new(StringComparer.Ordinal);
        private static readonly RuntimePackageStamp DefaultStamp = new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1);

        public List<string> InstalledPackageIds { get; } = [];

        public List<string> EnabledPackageIds { get; } = [];

        public List<string> DisabledPackageIds { get; } = [];

        public List<string> UninstalledPackageIds { get; } = [];

        public List<string> CommittedStageIds { get; } = [];

        public List<string> DiscardedStageIds { get; } = [];

        public List<PackageStoreStageRequest> StagedRequests { get; } = [];

        public TimeSpan InstallDelay { get; init; }

        public TimeSpan EnableDelay { get; init; }

        public bool RuntimeSessionApplied { get; init; } = true;

        public bool RequiresAppRestart { get; init; }

        public RuntimePackageStamp CommittedStamp { get; init; } = DefaultStamp;

        public string UploadContentHash { get; init; } = "test-hash";

        public Exception? CommitException { get; init; }

        public Exception? DiscardException { get; init; }

        public RuntimePackageStageState StageStatusAfterCommitException { get; init; } = RuntimePackageStageState.Pending;

        public int StageStatusCallCount { get; private set; }

        public List<RuntimeRegistryUpdateRequest> UpdateRequests { get; } = [];

        public PackageUninstallPlan UninstallPlan { get; init; } = CreateUninstallPlan("agent");

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);

        public Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(UninstallPlan);

        public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
        {
            InstalledPackageIds.Add(request.PackageId);
            return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                $"Installed {request.PackageId}.",
                RuntimeSessionApplied,
                RequiresAppRestart,
                [],
                [],
                [request.PackageId],
                [])
            {
                CommittedStamp = this.CommittedStamp,
            });
        }

        public Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            var uploadId = Guid.NewGuid().ToString("N");
            _uploadedPackageIds[uploadId] = Path.GetFileNameWithoutExtension(packagePath).Split('.')[0];
            return Task.FromResult(new ContentUploadDescriptor(
                uploadId,
                UploadContentHash,
                0,
                Path.GetFileName(packagePath),
                "application/vnd.sunder.package"));
        }

        public Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("stack-upload", "test-hash", 0, Path.GetFileName(stackPath), "application/vnd.sunder.stack"));

        public Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("media-upload", "test-hash", 0, Path.GetFileName(mediaPath), contentType));

        public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryResolveInstallPlanResponse(true, [], [], [], [], []));

        public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(RegistryChangeResult(request.Packages.Select(package => package.PackageId).ToArray()));

        public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
        {
            UpdateRequests.Add(request);
            return Task.FromResult(RegistryChangeResult([]));
        }

        public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryPackageStarResponse(true, null, null, []));

        public async Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default)
        {
            StagedRequests.Add(request);
            if (InstallDelay > TimeSpan.Zero && request.Mutations.Any(mutation => mutation.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade))
            {
                await Task.Delay(InstallDelay, cancellationToken);
            }

            if (EnableDelay > TimeSpan.Zero && request.Mutations.Any(mutation => mutation.Kind == PackageStoreMutationKind.Enable))
            {
                await Task.Delay(EnableDelay, cancellationToken);
            }

            var stageId = Guid.NewGuid().ToString("N");
            _pendingStages[stageId] = request;
            var impactedPackageIds = request.Mutations.Select(GetMutationPackageId).ToArray();
            return new PackageStoreStageResult(
                stageId,
                new PackageOperationResult(true, "staged", RuntimeSessionApplied: false, RequiresAppRestart: false, [], [])
                {
                    ImpactedPackageIds = impactedPackageIds,
                },
                impactedPackageIds.Select(packageId => new ActivePackageDescriptor(packageId, packageId, "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, [])).ToArray());
        }

        public Task<PackageOperationResult> CommitPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            if (CommitException is not null)
            {
                throw CommitException;
            }

            CommittedStageIds.Add(stageId);
            var request = _pendingStages[stageId];
            _pendingStages.Remove(stageId);
            var impactedPackageIds = request.Mutations.Select(GetMutationPackageId).ToArray();
            foreach (var mutation in request.Mutations)
            {
                switch (mutation.Kind)
                {
                    case PackageStoreMutationKind.Install:
                        InstalledPackageIds.Add(GetMutationPackageId(mutation));
                        break;
                    case PackageStoreMutationKind.Enable:
                        EnabledPackageIds.Add(GetMutationPackageId(mutation));
                        break;
                    case PackageStoreMutationKind.Disable:
                        DisabledPackageIds.Add(GetMutationPackageId(mutation));
                        break;
                    case PackageStoreMutationKind.Uninstall:
                        UninstalledPackageIds.Add(GetMutationPackageId(mutation));
                        break;
                }
            }

            return Task.FromResult(new PackageOperationResult(true, "committed", RuntimeSessionApplied, RequiresAppRestart, [], [])
            {
                ImpactedPackageIds = impactedPackageIds,
                CommittedStamp = this.CommittedStamp,
            });
        }

        public Task<RuntimePackageStageStatus> GetPackageStoreStageStatusAsync(
            string stageId,
            CancellationToken cancellationToken = default)
        {
            StageStatusCallCount++;
            var state = CommitException is null
                ? CommittedStageIds.Contains(stageId)
                    ? RuntimePackageStageState.Committed
                    : RuntimePackageStageState.Pending
                : StageStatusAfterCommitException;
            return Task.FromResult(new RuntimePackageStageStatus(
                stageId,
                RuntimePackageStageKind.PackageStore,
                state,
                DateTimeOffset.UtcNow,
                state == RuntimePackageStageState.Committed ? CommittedStamp : null,
                RuntimeSessionApplied,
                ReconciliationPending: false,
                null));
        }

        public Task DiscardPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            if (DiscardException is not null)
            {
                throw DiscardException;
            }

            DiscardedStageIds.Add(stageId);
            _pendingStages.Remove(stageId);
            return Task.CompletedTask;
        }

        public void Dispose() { }

        private string GetMutationPackageId(PackageStoreMutationRequest mutation)
            => !string.IsNullOrWhiteSpace(mutation.PackageId)
                ? mutation.PackageId
                : _uploadedPackageIds[mutation.UploadId ?? string.Empty];

        private RuntimeRegistryPackageChangeResult RegistryChangeResult(IReadOnlyList<string> packageIds)
            => new(true, RuntimeRegistryErrorCode.None, "Applied package changes.", RuntimeSessionApplied, RequiresAppRestart, [], [], packageIds, [])
            {
                CommittedStamp = this.CommittedStamp,
            };

        private static PackageUninstallPlan CreateUninstallPlan(string packageId)
            => new(
                packageId,
                [new PackageUninstallPlanPackage(packageId, packageId, "1.0.0")],
                [],
                [packageId],
                new PackageLifecycleChangeSet([packageId], [packageId], [packageId], [packageId], false),
                PackageUninstallDataBehavior.Retain,
                [packageId],
                new string('a', 64));
    }

    private sealed class ThrowingRuntimeClientFactory : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => throw new InvalidOperationException("Runtime API is not used by this presentation test.");
    }
}
