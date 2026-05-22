using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;
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

    private static PackageUpdateStartupCheckService CreateService(
        BackgroundProcessQueueService queue,
        FakeRuntimeApiClient runtimeApiClient,
        FakeRegistryApiClient registryApiClient,
        NotificationCenterService notificationCenter)
        => new(
            queue,
            new FakeRuntimeApiClientFactory(runtimeApiClient),
            new AppPackageNotificationService(notificationCenter, "sunder.app", "Sunder"),
            () => new Uri("https://registry.example/"),
            _ => registryApiClient);

    private static NotificationCenterService CreateNotificationCenter()
        => new(Path.Combine(CreateTempDirectory(), "notifications.json"));

    private static InstalledPackageDescriptor CreateInstalledPackage(string packageId, string version)
        => new(
            packageId,
            packageId,
            version,
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
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{availableVersion}"));

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => runtimeApiClient;
    }

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public IReadOnlyList<RegistryPackageUpdate> Updates { get; init; } = [];

        public bool ThrowOnResolveUpdates { get; init; }

        public int ResolveUpdatesCallCount { get; private set; }

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
            RegistryResolveUpdatesRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveUpdatesCallCount++;
            if (ThrowOnResolveUpdates)
            {
                throw new InvalidOperationException("registry unavailable");
            }

            return Task.FromResult(new RegistryResolveUpdatesResponse(Updates));
        }

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
            RegistryResolveInstallPlanRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DownloadArtifactAsync(
            RegistryPackageArtifact artifact,
            string packageId,
            string version,
            string destinationPath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntimeApiClient(IReadOnlyList<InstalledPackageDescriptor>? installedPackages = null) : IRuntimeApiClient
    {
        private readonly IReadOnlyList<InstalledPackageDescriptor> _installedPackages = installedPackages ?? [];

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_installedPackages);

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(
            string packageId,
            string packagePath,
            bool allowDowngrade = false,
            bool reinstall = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(
            PackageLifecycleLoadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
            string packageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SavePackageConfigurationValuesAsync(
            string packageId,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
            string packageId,
            string authSessionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReportPackageFaultAsync(
            string packageId,
            PackageFailureOrigin origin,
            string message,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
