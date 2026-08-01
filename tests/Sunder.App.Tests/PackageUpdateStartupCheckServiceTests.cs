using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageUpdateStartupCheckServiceTests
{
    [Fact]
    public async Task EnqueueStartupCheck_QueuesHiddenBackgroundProcess()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var service = CreateService(
            queue,
            new FakeRuntimeApiClient(),
            new FakeRegistryApiClient(),
            notificationCenter);

        var snapshot = service.EnqueueStartupCheck();

        Assert.Equal(BackgroundProcessIndicator.Hidden, snapshot.Indicator);
        Assert.Equal(PackageUpdateStartupCheckService.GroupKey, snapshot.GroupKey);
        Assert.False(snapshot.CanCancel);
        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
    }

    [Fact]
    public async Task EnqueueStartupCheck_WhenNoPackagesInstalled_DoesNotPublishNotification()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var registryClient = new FakeRegistryApiClient();
        var notificationCenter = CreateNotificationCenter();
        var service = CreateService(
            queue,
            new FakeRuntimeApiClient(),
            registryClient,
            notificationCenter);

        var snapshot = service.EnqueueStartupCheck();

        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
        Assert.Equal(0, registryClient.ResolveUpdatesCallCount);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    [Fact]
    public async Task EnqueueStartupCheck_WhenNoUpdatesAreAvailable_DoesNotPublishNotification()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var service = CreateService(
            queue,
            new FakeRuntimeApiClient([CreateInstalledPackage("agent", "1.0.0")]),
            new FakeRegistryApiClient(),
            notificationCenter);

        var snapshot = service.EnqueueStartupCheck();

        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    [Fact]
    public async Task EnqueueStartupCheck_WhenUpdatesAreAvailable_PublishesHistoryNotification()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var service = CreateService(
            queue,
            new FakeRuntimeApiClient([
                CreateInstalledPackage("agent", "1.0.0"),
                CreateInstalledPackage("tools", "1.0.0"),
            ]),
            new FakeRegistryApiClient
            {
                Updates = [
                    CreateUpdate("agent", "1.0.0", "1.1.0"),
                    CreateUpdate("tools", "1.0.0", "1.2.0"),
                ],
            },
            notificationCenter);

        var snapshot = service.EnqueueStartupCheck();

        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
        var notification = Assert.Single(notificationCenter.ListNotifications());
        Assert.Equal("sunder.app", notification.SourcePackageId);
        Assert.Equal("Sunder", notification.SourceDisplayName);
        Assert.Equal("Package updates available", notification.Title);
        Assert.Equal("2 installed packages can be updated. Open Packages to review and update.", notification.Message);
        Assert.Equal(PackageNotificationSeverity.Information, notification.Severity);
    }

    [Fact]
    public async Task EnqueueStartupCheck_WhenUpdateCheckFails_CompletesWithoutNotification()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var service = CreateService(
            queue,
            new FakeRuntimeApiClient([CreateInstalledPackage("agent", "1.0.0")]),
            new FakeRegistryApiClient { ThrowOnResolveUpdates = true },
            notificationCenter);

        var snapshot = service.EnqueueStartupCheck();

        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
        var completed = queue.GetProcess(snapshot.ProcessId);
        Assert.Equal(BackgroundProcessState.Completed, completed?.State);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    [Fact]
    public async Task EnqueueStartupCheck_WhenPlanFails_DoesNotReportUpdates()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var service = CreateService(
            queue,
            new FakeRuntimeApiClient([CreateInstalledPackage("agent", "1.0.0")]),
            new FakeRegistryApiClient
            {
                PlanSuccess = false,
                Updates = [CreateUpdate("agent", "1.0.0", "1.1.0")],
            },
            notificationCenter);

        var snapshot = service.EnqueueStartupCheck();

        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
        Assert.Equal(BackgroundProcessState.Completed, queue.GetProcess(snapshot.ProcessId)?.State);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    private static PackageUpdateStartupCheckService CreateService(
        BackgroundProcessQueueService queue,
        FakeRuntimeApiClient runtimeApiClient,
        FakeRegistryApiClient registryApiClient,
        NotificationCenterService notificationCenter)
    {
        runtimeApiClient.Updates = registryApiClient.Updates;
        runtimeApiClient.PlanSuccess = registryApiClient.PlanSuccess;
        runtimeApiClient.ThrowOnResolveUpdates = registryApiClient.ThrowOnResolveUpdates;
        return new(
            queue,
            new FakeRuntimeApiClientFactory(runtimeApiClient),
            new AppPackageNotificationService(notificationCenter, "sunder.app", "Sunder"),
            () => new Uri("https://registry.example/"),
            _ => registryApiClient);
    }

    private static NotificationCenterService CreateNotificationCenter()
        => new(Path.Combine(CreateTempDirectory(), "notifications.json"));

    private static InstalledPackageDescriptor CreateInstalledPackage(string packageId, string version)
        => new(
            packageId,
            packageId,
            version,
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Summary: null,
            Icon: null,
            IsEnabled: true,
            DependsOn: [],
            DateTimeOffset.UtcNow,
            StatusMessage: null);

    private static RegistryPackageUpdate CreateUpdate(string packageId, string currentVersion, string availableVersion)
        => new(
            packageId,
            currentVersion,
            availableVersion,
            DeprecatedMessage: null,
            Artifacts:
            [
                new RegistryPackageProjectionArtifact(
                    "shared",
                    null,
                    "",
                    0,
                    $"download/{packageId}/{availableVersion}",
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
        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public IReadOnlyList<RegistryPackageUpdate> Updates { get; init; } = [];

        public bool PlanSuccess { get; init; } = true;

        public bool ThrowOnResolveUpdates { get; init; }

        public int ResolveUpdatesCallCount { get; private set; }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntimeApiClient(IReadOnlyList<InstalledPackageDescriptor>? installedPackages = null) : IRuntimePackageUpdateClient
    {
        private readonly IReadOnlyList<InstalledPackageDescriptor> _installedPackages = installedPackages ?? [];

        public IReadOnlyList<RegistryPackageUpdate> Updates { get; set; } = [];

        public bool PlanSuccess { get; set; } = true;

        public bool ThrowOnResolveUpdates { get; set; }

        public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnResolveUpdates) throw new InvalidOperationException("registry unavailable");
            return Task.FromResult(new RuntimeRegistryResolveInstallPlanResponse(
                PlanSuccess,
                Updates.Select(update => new RuntimeRegistryPackageInstallPlanItem(
                    update.PackageId,
                    update.CurrentVersion,
                    update.AvailableVersion,
                    true,
                    update.DeprecatedMessage,
                    [],
                    [],
                    update.Artifacts.Select(artifact => new RuntimeRegistryPackageProjectionArtifact(
                        artifact.Kind,
                        artifact.Rid,
                        artifact.Sha256,
                        artifact.Size,
                        artifact.DownloadUrl,
                        artifact.SourceArchiveSha256,
                        artifact.ManifestSha256,
                        artifact.ProjectionContentIdentity,
                        artifact.ProjectionFormatVersion)).ToArray())).ToArray(),
                [],
                PlanSuccess ? [] : ["Registry is not reachable."],
                [],
                []));
        }

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_installedPackages);

        public void Dispose()
        {
        }
    }
}
