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
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<IReadOnlyList<string>>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (packageIds, _) =>
            {
                lifecycleApplications.Add(packageIds.ToArray());
                return Task.CompletedTask;
            },
            notificationCenter,
            registryClientFactory: _ => registryClient);

        var operation = service.EnqueueMarketplaceInstall("agent", "Agent", new Uri("https://registry.example/"));

        Assert.Equal(BackgroundProcessIndicator.Packages, operation.Indicator);
        Assert.Equal(PackageOperationService.PackageStoreGroupKey, operation.GroupKey);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Collection(lifecycleApplications, packageIds => Assert.Equal(["agent"], packageIds));
    }

    [Fact]
    public async Task EnqueueLocalInstall_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<IReadOnlyList<string>>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (packageIds, _) =>
            {
                lifecycleApplications.Add(packageIds.ToArray());
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"));

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Collection(lifecycleApplications, packageIds => Assert.Equal(["agent"], packageIds));
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

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"));

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        var completed = queue.GetProcess(operation.ProcessId);
        Assert.Equal(["agent"], runtimeClient.InstalledPackageIds);
        Assert.Contains("running shell rejected the live change", completed?.StatusText);
        var notification = Assert.Single(notificationCenter.ListNotifications());
        Assert.Equal("Package changes were not applied live", notification.Title);
        Assert.Equal(PackageNotificationSeverity.Warning, notification.Severity);
        Assert.Contains("shell refresh failed", notification.Message);
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
        var lifecycleApplications = new List<IReadOnlyList<string>>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (packageIds, _) =>
            {
                lifecycleApplications.Add(packageIds.ToArray());
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"));

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
    public async Task EnqueueLocalInstall_WhenPreflightFails_DiscardsStageWithoutInstalling()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (_, _) => Task.CompletedTask,
            notificationCenter,
            preflightPackageLifecycleChangesAsync: (_, _, _, _) => throw new InvalidOperationException("preflight rejected"));

        var operation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"));

        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Failed);

        Assert.Empty(runtimeClient.InstalledPackageIds);
        Assert.Empty(runtimeClient.CommittedStageIds);
        Assert.Single(runtimeClient.DiscardedStageIds);
        var notification = Assert.Single(notificationCenter.ListNotifications());
        Assert.Equal(PackageNotificationSeverity.Error, notification.Severity);
        Assert.Contains("preflight rejected", notification.Message);
    }

    [Fact]
    public async Task EnqueueEnable_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<IReadOnlyList<string>>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (packageIds, _) =>
            {
                lifecycleApplications.Add(packageIds.ToArray());
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueEnable("agent", "Agent");

        Assert.Equal(BackgroundProcessIndicator.Packages, operation.Indicator);
        Assert.False(operation.CanCancel);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.EnabledPackageIds);
        Assert.Collection(lifecycleApplications, packageIds => Assert.Equal(["agent"], packageIds));
    }

    [Fact]
    public async Task EnqueueDisable_RunsInBackgroundAndAppliesLifecycleChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var runtimeClient = new FakeRuntimeApiClient();
        var lifecycleApplications = new List<IReadOnlyList<string>>();
        var notificationCenter = new NotificationCenterService(Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (packageIds, _) =>
            {
                lifecycleApplications.Add(packageIds.ToArray());
                return Task.CompletedTask;
            },
            notificationCenter);

        var operation = service.EnqueueDisable("agent", "Agent");

        Assert.Equal(BackgroundProcessIndicator.Packages, operation.Indicator);
        Assert.False(operation.CanCancel);
        await WaitForConditionAsync(() => queue.GetProcess(operation.ProcessId)?.State == BackgroundProcessState.Completed);

        Assert.Equal(["agent"], runtimeClient.DisabledPackageIds);
        Assert.Collection(lifecycleApplications, packageIds => Assert.Equal(["agent"], packageIds));
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
        var packageOperation = service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg"));
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
        Assert.Throws<ObjectDisposedException>(() => service.EnqueueLocalInstall(Path.Combine(CreateTempDirectory(), "agent.1.0.0.sunderpkg")));
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
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{version}"));

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => runtimeApiClient;
    }

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], []);

        public List<string> DownloadedPackageIds { get; } = [];

        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(RegistryResolveUpdatesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(RegistryResolveInstallPlanRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(InstallPlan);

        public Task DownloadArtifactAsync(
            RegistryPackageArtifact artifact,
            string packageId,
            string version,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadedPackageIds.Add(packageId);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        private readonly Dictionary<string, PackageStoreStageRequest> _pendingStages = new(StringComparer.OrdinalIgnoreCase);

        public List<string> InstalledPackageIds { get; } = [];

        public List<string> EnabledPackageIds { get; } = [];

        public List<string> DisabledPackageIds { get; } = [];

        public List<string> UninstalledPackageIds { get; } = [];

        public List<string> CommittedStageIds { get; } = [];

        public List<string> DiscardedStageIds { get; } = [];

        public TimeSpan InstallDelay { get; init; }

        public TimeSpan EnableDelay { get; init; }

        public bool RuntimeSessionApplied { get; init; } = true;

        public bool RequiresAppRestart { get; init; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);

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
                []));
        }

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => throw new NotSupportedException();

        public async Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            if (InstallDelay > TimeSpan.Zero)
            {
                await Task.Delay(InstallDelay, cancellationToken);
            }

            var fileName = Path.GetFileNameWithoutExtension(packagePath);
            var packageId = fileName.Split('.')[0];
            InstalledPackageIds.Add(packageId);
            return new PackageOperationResult(true, $"Installed {packageId}.", RuntimeSessionApplied, RequiresAppRestart, [], [])
            {
                ImpactedPackageIds = [packageId],
            };
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
        {
            if (EnableDelay > TimeSpan.Zero)
            {
                await Task.Delay(EnableDelay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnabledPackageIds.Add(packageId);
            return new PackageOperationResult(true, $"Enabled {packageId}.", RuntimeSessionApplied, RequiresAppRestart, [], [])
            {
                ImpactedPackageIds = [packageId],
            };
        }

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisabledPackageIds.Add(packageId);
            return Task.FromResult(new PackageOperationResult(true, $"Disabled {packageId}.", RuntimeSessionApplied, RequiresAppRestart, [], [])
            {
                ImpactedPackageIds = [packageId],
            });
        }

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default)
        {
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
                impactedPackageIds.Select(packageId => new ActivePackageDescriptor(packageId, packageId, "1.0.0", null, true, PackageReadinessState.Ready, [])).ToArray(),
                impactedPackageIds.Select(packageId => RuntimeContractTestData.Snapshot(packageId, PackageSourceKind.Installed, packageId)).ToArray());
        }

        public Task<PackageOperationResult> CommitPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
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
            });
        }

        public Task DiscardPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            DiscardedStageIds.Add(stageId);
            _pendingStages.Remove(stageId);
            return Task.CompletedTask;
        }

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageOperationResult(true, "reloaded", RuntimeSessionApplied, RequiresAppRestart, [], [])
            {
                ImpactedPackageIds = impactedPackageIds.ToArray(),
            });

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SavePackageConfigurationValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }

        private static string GetMutationPackageId(PackageStoreMutationRequest mutation)
            => !string.IsNullOrWhiteSpace(mutation.PackageId)
                ? mutation.PackageId
                : Path.GetFileNameWithoutExtension(mutation.UploadId ?? string.Empty).Split('.')[0];
    }
}
