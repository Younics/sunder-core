using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageOperationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCombinedConsentPrecedesRuntimeAndPackageQueueing(bool cancel)
    {
        using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var engine = new TestSupport.RuntimeManagementTestClient();
        var ready = false;
        var sources = new List<RuntimePackageRuntimePrerequisiteRequest>();
        engine.PreviewPrerequisites = (request, _) =>
        {
            sources.Add(request);
            return Task.FromResult(TestSupport.RuntimeManagementTestClient.Prerequisites(ready));
        };
        engine.InstallHandler = (_, _) =>
        {
            ready = true;
            return Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Installed", "install", null, false));
        };
        var client = new FakeRuntimeApiClient { PrerequisiteClient = engine };
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var packageConsentCount = 0;
        using var service = CreateService(queue, new FakeRuntimeApiClientFactory(client), (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")),
            confirmLocalPackage: (combined, _) =>
            {
                Assert.False(ready);
                Assert.True(combined.HasRuntimeDownloads);
                Assert.Empty(queue.ListTasks());
                packageConsentCount++;
                shown.TrySetResult();
                return consent.Task;
            });
        var review = Sunder.App.ViewModels.LocalPackageInstallConsentViewModel.Valid("agent.sunderpkg",
            new Sunder.Package.Format.SunderPackageArchiveValidationResult(new()
            {
                Id = "agent", Name = "Agent", Version = "1.0.0", UsesContracts = [],
                Targets = [new() { Role = "runtime", RequiredHostCapabilities = [Sunder.Sdk.Compatibility.SunderSdkCapabilities.DockerEnvironmentV1] }],
            }, [], [])
            {
                ContentIndex = new(1, [new(Sunder.Package.Format.SunderPackageFormat.ManifestPath, new('a', 64), 42)]),
            });
        var capabilityConsent = new PackageCapabilityConsent("agent", "1.0.0", new('a', 64), [Sunder.Sdk.Compatibility.SunderSdkCapabilities.DockerEnvironmentV1]);
        var preparing = service.EnqueueLocalInstallAsync(new PackageArchiveSelection("agent.sunderpkg", "test-hash", false, capabilityConsent, review));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = Assert.Single(client.UploadRetention.Retains);
        Assert.Equal("test-hash", retained.ExpectedContentHash);
        Assert.NotNull(Assert.Single(sources).Local!.Mutations[0].UploadRetention);
        Assert.Empty(client.StagedRequests);
        Assert.Empty(client.InstalledPackageIds);
        Assert.Equal(1, packageConsentCount);
        consent.SetResult(!cancel);
        var operation = await preparing;
        if (operation is not null) await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);
        await WaitForConditionAsync(() => client.UploadRetention.Releases.Count == 1);
        Assert.Single(client.UploadRetention.Releases);
        Assert.All(sources, source => Assert.Null(source.Local!.Mutations[0].CapabilityConsent));
        if (cancel)
        {
            Assert.Empty(client.StagedRequests);
            Assert.Empty(client.InstalledPackageIds);
            Assert.Equal(cancel, engine.InstallConfirmation is null);
            Assert.Equal(1, packageConsentCount);
            Assert.Null(operation);
            Assert.Empty(queue.ListTasks());
        }
        else
        {
            var stage = Assert.Single(client.StagedRequests);
            Assert.Equal(capabilityConsent, stage.Mutations[0].CapabilityConsent);
            Assert.Equal(TestSupport.RuntimeManagementTestClient.Hash, stage.RuntimePrerequisiteExpectation!.SourceFingerprint);
            Assert.Equal(sources[0].Local!.Mutations[0].UploadId, sources[1].Local!.Mutations[0].UploadId);
            Assert.Equal(sources[1].Local!.Mutations[0].UploadId, stage.Mutations[0].UploadId);
            Assert.Equal(sources[1].Local!.Mutations[0].UploadRetention, stage.Mutations[0].UploadRetention);
            Assert.Equal(["agent"], client.InstalledPackageIds);
            Assert.Equal(1, packageConsentCount);
        }
    }

    [Fact]
    public async Task EnqueueMarketplaceInstall_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter,
            registryClientFactory: _ => registryClient);

        var operation = (await service.EnqueueMarketplaceInstallAsync("agent", "Agent", new Uri("https://registry.example/")))!;

        Assert.Equal(BackgroundTaskIndicator.Packages, operation.Indicator);
        Assert.Equal(PackageOperationService.PackageStoreGroupKey, operation.GroupKey);
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueRegistrySourceAdoption_ConfirmsLatestAgainstConfiguredRegistry()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = (await service.EnqueueRegistrySourceAdoptionAsync(
            "younics.ai.agent",
            "AI Agent",
            new Uri("https://registry.example/")))!;

        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var request = Assert.IsType<RuntimeRegistrySourceAdoptionRequest>(runtimeClient.LastSourceAdoptionRequest);
        Assert.Equal("younics.ai.agent", request.PackageId);
        Assert.Equal("https://registry.example/", request.RegistryOrigin);
        Assert.Equal("latest", request.Tag);
        Assert.Null(request.Version);
        Assert.True(request.Confirm);
        Assert.False(request.DryRun);
        Assert.Equal(new string('f', 64), request.ExpectedResolutionFingerprint);
        Assert.Equal(3, runtimeClient.SourceAdoptionRequests.Count);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenArchiveChangedAfterReview_DoesNotStageInstall()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient { UploadContentHash = "changed-hash" };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var failed = await Assert.ThrowsAsync<InvalidDataException>(() => service.EnqueueLocalInstallAsync(LocalSelection(
            Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"), "reviewed-hash", false)));
        Assert.Empty(queue.ListTasks());
        Assert.Empty(runtimeClient.InstalledPackageIds);
        Assert.Empty(runtimeClient.CommittedStageIds);
        Assert.Contains("changed after review", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnqueueLocalInstall_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;

        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WaitsForEventPresentationAndWritesItOnce()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var stamp = new RuntimePackageStamp(runtimeInstanceId, 1);
        var runtimeClient = new FakeRuntimeApiClient { CommittedStamp = stamp };
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            subscription.WaitUntilAppliedAsync,
            notificationCenter);

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;
        await WaitForConditionAsync(() => runtimeClient.CommittedStageIds.Count == 1);
        Assert.NotEqual(BackgroundTaskState.Completed, queue.GetTask(operation.TaskId)?.State);

        var snapshot = new RuntimePackageSnapshot(runtimeInstanceId, 1, 1, RuntimeBootstrapState.Ready, [], [], [], []);
        await subscription.ApplySnapshotAsync(snapshot, [], ["agent"]);
        await subscription.ApplySnapshotAsync(snapshot, [], ["agent"]);
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        Assert.Equal(1, presentationCount);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenLifecycleApplyFails_CompletesWithWarningNotification()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => throw new InvalidOperationException("shell refresh failed"),
            notificationCenter);

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;

        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var completed = queue.GetTask(operation.TaskId);
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
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            waitForPresentationAsync: (_, _) => Task.FromResult(PackagePresentationResult.Unavailable(
                "Sunder is running in the Core Shell without a live Runtime presentation.")));

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var completed = queue.GetTask(operation.TaskId);
        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Contains("commit succeeded", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Core Shell", completed?.StatusText, StringComparison.Ordinal);
        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Equal(PackageNotificationSeverity.Warning, Assert.Single(notificationCenter.ListNotifications()).Severity);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenPresentationWaitTimesOut_ReportsCommittedStoreWarning()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
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

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var completed = queue.GetTask(operation.TaskId);
        Assert.Contains("commit succeeded", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not apply it within", completed?.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtimeClient.DiscardedStageIds);
    }

    [Fact]
    public async Task EnqueueLocalInstall_WhenCanceledAfterCommit_ReportsPresentationFailureWithoutDiscardingCommit()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var presentationWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(
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

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;
        await presentationWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(service.CancelOperation(operation.TaskId));
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Failed);

        var failed = queue.GetTask(operation.TaskId);
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
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            RuntimeSessionApplied = false,
            RequiresAppRestart = true,
        };
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;

        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var completed = queue.GetTask(operation.TaskId);
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
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");

        Assert.Equal(BackgroundTaskIndicator.Packages, operation.Indicator);
        Assert.False(operation.CanCancel);
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        Assert.Equal(["agent"], runtimeClient.EnabledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueDisable_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueDisable("agent", "Agent");

        Assert.Equal(BackgroundTaskIndicator.Packages, operation.Indicator);
        Assert.False(operation.CanCancel);
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        Assert.Equal(["agent"], runtimeClient.DisabledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task EnqueueMarketplaceUpdate_UsesRecordedRuntimeUpdatePolicy()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));

        var operation = (await service.EnqueueMarketplaceUpdateAsync(
            "agent",
            "Agent",
            "2.0.0"))!;
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var request = Assert.Single(runtimeClient.UpdateRequests, request => !request.DryRun);
        Assert.Equal("agent", request.PackageId);
        Assert.Null(request.RegistryOrigin);
    }

    [Fact]
    public async Task EnqueueUpdateAll_DoesNotSupplyAnOriginOverride()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));

        var operation = (await service.EnqueueUpdateAllAsync())!;
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var request = Assert.Single(runtimeClient.UpdateRequests, request => !request.DryRun);
        Assert.Null(request.PackageId);
        Assert.Null(request.RegistryOrigin);
    }

    [Fact]
    public async Task EnqueueUninstall_PreflightsPlanWithoutImplicitCascadeConsent()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json")));

        var operation = service.EnqueueUninstall("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        var mutation = Assert.Single(Assert.Single(runtimeClient.StagedRequests).Mutations);
        Assert.False(mutation.AllowCascade);
        Assert.Equal(new string('b', 64), mutation.ConfirmationToken);
    }

    [Fact]
    public async Task EnqueueEnable_WhenCommitAndDiscardFail_PreservesCommitFailure()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            CommitException = new InvalidOperationException("primary commit failure"),
            DiscardException = new InvalidOperationException("secondary discard failure"),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Failed);

        var failed = queue.GetTask(operation.TaskId);
        Assert.Contains("primary commit failure", failed?.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secondary discard failure", failed?.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueueEnable_WhenCommitResponseIsLostAfterServerCommit_ReconcilesWithoutDiscard(
        bool cancellation)
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            CommitException = cancellation
                ? new OperationCanceledException("response cancelled after commit")
                : new HttpRequestException("response lost after commit"),
            StageStatusAfterCommitException = RuntimePackageStageState.Committed,
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Completed);

        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Equal(1, runtimeClient.StageStatusCallCount);
    }

    [Fact]
    public async Task EnqueueEnable_WhenAmbiguousCommitIsStillCommitting_LeavesStageIntact()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            CommitException = new HttpRequestException("response lost while committing"),
            StageStatusAfterCommitException = RuntimePackageStageState.Committing,
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");
        await WaitForConditionAsync(() => queue.GetTask(operation.TaskId)?.State == BackgroundTaskState.Failed);

        Assert.Empty(runtimeClient.DiscardedStageIds);
        Assert.Equal(1, runtimeClient.StageStatusCallCount);
    }

    [Fact]
    public async Task EnqueueMarketplaceInstall_WhenPackageAlreadyHasActiveOperation_ReturnsExistingOperation()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            registryClientFactory: _ => registryClient);

        var first = (await service.EnqueueMarketplaceInstallAsync("agent", "Agent", new Uri("https://registry.example/")))!;
        var second = (await service.EnqueueMarketplaceInstallAsync("agent", "Agent", new Uri("https://registry.example/")))!;

        Assert.Equal(first.TaskId, second.TaskId);
        await service.CancelAllAsync();
    }

    [Fact]
    public async Task EnqueueEnable_WhenCalledConcurrentlyForSamePackage_ReturnsSingleOperation()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient
        {
            EnableDelay = TimeSpan.FromSeconds(1),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
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

        Assert.Single(operations.Select(operation => operation.TaskId).Distinct());
        await service.CancelAllAsync();
    }

    [Fact]
    public async Task CancelAllAsync_DoesNotCancelNonPackageProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 2);
        var runtimeClient = new FakeRuntimeApiClient
        {
            InstallDelay = TimeSpan.FromMinutes(1),
        };
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var nonPackageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNonPackageCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonPackageProcess = queue.Enqueue(new BackgroundTaskRequest(
            "Non-package work",
            "non-package",
            BackgroundTaskIndicator.Main,
            BackgroundTaskConcurrencyMode.ParallelWithinGroup,
            CanCancel: true,
            async _ =>
            {
                nonPackageStarted.SetResult();
                await allowNonPackageCompletion.Task;
            }));
        await nonPackageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var packageOperation = (await service.EnqueueLocalInstallAsync(LocalSelection(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"))))!;
        await WaitForConditionAsync(() => queue.GetTask(packageOperation.TaskId)?.State == BackgroundTaskState.Running);

        await service.CancelAllAsync();

        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(packageOperation.TaskId)?.State);
        Assert.Equal(BackgroundTaskState.Running, queue.GetTask(nonPackageProcess.TaskId)?.State);
        allowNonPackageCompletion.SetResult();
        await WaitForConditionAsync(() => queue.GetTask(nonPackageProcess.TaskId)?.State == BackgroundTaskState.Completed);
    }

    [Fact]
    public async Task CancelOperation_DoesNotCancelNonPackageProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var nonPackageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNonPackageCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonPackageProcess = queue.Enqueue(new BackgroundTaskRequest(
            "Non-package work",
            "non-package",
            BackgroundTaskIndicator.Main,
            BackgroundTaskConcurrencyMode.ParallelWithinGroup,
            CanCancel: true,
            async _ =>
            {
                nonPackageStarted.SetResult();
                await allowNonPackageCompletion.Task;
            }));
        await nonPackageStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancelled = service.CancelOperation(nonPackageProcess.TaskId);

        Assert.False(cancelled);
        Assert.Equal(BackgroundTaskState.Running, queue.GetTask(nonPackageProcess.TaskId)?.State);
        allowNonPackageCompletion.SetResult();
        await WaitForConditionAsync(() => queue.GetTask(nonPackageProcess.TaskId)?.State == BackgroundTaskState.Completed);
    }

    [Fact]
    public async Task CancelQueuedLocalInstall_DeletesOwnedReviewSnapshot()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        using var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.Enqueue(new BackgroundTaskRequest(
            "Blocking work",
            "blocking",
            BackgroundTaskIndicator.Main,
            BackgroundTaskConcurrencyMode.ParallelWithinGroup,
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
        var operation = (await service.EnqueueLocalInstallAsync(LocalSelection(reviewPath, deleteAfterUse: true)))!;
        Assert.Equal(BackgroundTaskState.Queued, queue.GetTask(operation.TaskId)?.State);

        Assert.True(service.CancelOperation(operation.TaskId));

        await WaitForConditionAsync(() => !Directory.Exists(reviewDirectory));
        releaseBlocker.SetResult();
        await WaitForConditionAsync(() => queue.GetTask(blocker.TaskId)?.State == BackgroundTaskState.Completed);
    }

    [Fact]
    public async Task Dispose_RejectsFurtherPackageOperations()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);

        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.ListOperations());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.EnqueueLocalInstallAsync(LocalSelection("agent.sunderpkg")));
        Assert.Throws<ObjectDisposedException>(() => service.CancelOperation(Guid.NewGuid()));
    }

    [Fact]
    public void Dispose_UnsubscribesFromBackgroundTaskChanges()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = CreateService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter);
        var operationChangeCount = 0;
        service.OperationChanged += (_, _) => operationChangeCount++;

        service.Dispose();
        queue.Enqueue(new BackgroundTaskRequest(
            "Install Agent",
            PackageOperationService.PackageStoreGroupKey,
            BackgroundTaskIndicator.Packages,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask,
            new PackageOperationMetadata("agent", PackageOperationKind.InstallLocal, "Agent").ToMetadata()));

        Assert.Equal(0, operationChangeCount);
    }

    private static PackageArchiveSelection LocalSelection(string path, string hash = "test-hash", bool deleteAfterUse = false)
        => new(path, hash, deleteAfterUse, Review: Sunder.App.ViewModels.LocalPackageInstallConsentViewModel.Valid(Path.GetFileName(path),
            new(new() { Id = "agent", Name = "Agent", Version = "1.0.0", Targets = [], UsesContracts = [] }, [], [])));

    private static PackageOperationService CreateService(BackgroundTaskQueueService queue, IRuntimeApiClientFactory factory,
        Func<RuntimePackageStamp, CancellationToken, Task> presentation, NotificationCenterService notifications,
        RegistryPackageInstallService? registryInstallService = null, Func<Uri, IRegistryClient>? registryClientFactory = null,
        Func<RuntimePackageStamp, CancellationToken, Task<PackagePresentationResult>>? waitForPresentationAsync = null,
        TimeSpan? presentationWaitTimeout = null,
        Func<Sunder.App.ViewModels.LocalPackageInstallConsentViewModel, CancellationToken, Task<bool>>? confirmLocalPackage = null,
        TimeProvider? uploadRetentionClock = null)
        => new(queue, factory, presentation, notifications,
            registryInstallService ?? new RegistryPackageInstallService((_, _) => Task.FromResult(true), queue), registryClientFactory,
            waitForPresentationAsync, presentationWaitTimeout, confirmLocalPackage ?? ((_, _) => Task.FromResult(true)), uploadRetentionClock);

    private static RegistryPackageInstallPlanItem CreatePlanItem(string packageId, string version)
        => new(
             packageId,
             CurrentVersion: null,
             version,
             PackageContentIdentity: new string('a', 64),
             ActionKind: RegistryPackageActionKinds.PackageInstall,
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

    private sealed class FakeRuntimeApiClient : IRuntimePackageChangeClient, TestSupport.IReadyRuntimePrerequisiteTestClient
    {
        public TestSupport.PackageUploadRetentionTestClient UploadRetention { get; } = new();
        public Task<RuntimePackageUploadRetentionResult> RetainPackageUploadAsync(RuntimePackageUploadRetentionRequest request, CancellationToken cancellationToken = default)
            => UploadRetention.RetainPackageUploadAsync(request, cancellationToken);
        public Task<RuntimePackageUploadRetentionResult> RenewPackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
            => UploadRetention.RenewPackageUploadRetentionAsync(handle, cancellationToken);
        public Task<RuntimePackageUploadRetentionResult> GetPackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
            => UploadRetention.GetPackageUploadRetentionAsync(handle, cancellationToken);
        public Task<RuntimePackageUploadRetentionResult> ReleasePackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
            => UploadRetention.ReleasePackageUploadRetentionAsync(handle, cancellationToken);
        public Func<CancellationToken, Task>? BeforeStage { get; init; }
        public IRuntimePackageRuntimePrerequisiteClient? PrerequisiteClient { get; init; }
        public Task<RuntimePackageRuntimePrerequisitePlan> PreviewPackageRuntimePrerequisitesAsync(RuntimePackageRuntimePrerequisiteRequest request, CancellationToken cancellationToken = default)
            => PrerequisiteClient?.PreviewPackageRuntimePrerequisitesAsync(request, cancellationToken)
                ?? Task.FromResult(new RuntimePackageRuntimePrerequisitePlan(true, true, null, "Ready", new('a', 64), "osx-arm64", [], []));
        public Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken cancellationToken = default)
            => (PrerequisiteClient ?? throw new NotSupportedException()).PreviewExecutionRuntimeInstallAsync(request, cancellationToken);
        public Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
            => (PrerequisiteClient ?? throw new NotSupportedException()).ApplyExecutionRuntimeInstallAsync(request, cancellationToken);
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

        public RuntimeRegistrySourceAdoptionRequest? LastSourceAdoptionRequest { get; private set; }

        public List<RuntimeRegistrySourceAdoptionRequest> SourceAdoptionRequests { get; } = [];

        public PackageUninstallPlan UninstallPlan { get; init; } = CreateUninstallPlan("agent");

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([new(
                "agent", "Agent", "1.0.0", PackageHostRoles.Runtime, null, null, true, [], DateTimeOffset.UtcNow, null,
                Provenance: new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.FollowTag,
                    "https://registry.example/", "agent", RequestedTag: "latest"))]);

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
            => Task.FromResult(new RuntimeRegistryResolveInstallPlanResponse(true, [], [], [], [], [], ResolutionFingerprint: new('f', 64))
            { RuntimePrerequisites = new(true, true, null, "Ready", new('a', 64), "osx-arm64", [], []) });

        public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(RegistryChangeResult(request.Packages.Select(package => package.PackageId).ToArray()));

        public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
        {
            UpdateRequests.Add(request);
            return Task.FromResult(RegistryChangeResult([]) with
            { RuntimePrerequisites = new(true, true, null, "Ready", new('a', 64), "osx-arm64", [], []),
                PlanExpectations = [new("https://registry.example/", false, new('f', 64))] });
        }

        public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken cancellationToken = default)
        {
            SourceAdoptionRequests.Add(request);
            LastSourceAdoptionRequest = request;
            var result = RegistryChangeResult([request.PackageId]);
            if (request.DryRun)
            {
                result = result with
                {
                    RuntimeSessionApplied = false,
                    PlanExpectations =
                    [
                        new RuntimeRegistryPackagePlanExpectation(
                            request.RegistryOrigin,
                            request.IncludePrerelease,
                            new string('f', 64)),
                    ],
                };
            }
            return Task.FromResult(result);
        }

        public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryPackageStarResponse(true, null, null, []));

        public async Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default)
        {
            StagedRequests.Add(request);
            if (BeforeStage is not null) await BeforeStage(cancellationToken);
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
