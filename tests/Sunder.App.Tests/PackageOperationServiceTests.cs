using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;
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

        Assert.Equal(["agent"], registryClient.DownloadedPackageIds);
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

    private static RegistryPackageInstallPlanItem CreatePlanItem(string packageId, string version)
        => new(
            packageId,
            CurrentVersion: null,
            version,
            IsUpdate: false,
            DeprecatedMessage: null,
            DependsOn: [],
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{version}"));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.True(condition());
                return;
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => runtimeApiClient;
    }

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], []);

        public List<string> DownloadedPackageIds { get; } = [];

        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken cancellationToken = default)
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
        public List<string> InstalledPackageIds { get; } = [];

        public TimeSpan InstallDelay { get; init; }

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
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);

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
            return new PackageOperationResult(true, $"Installed {packageId}.", RequiresAppRestart: false, [], [])
            {
                ImpactedPackageIds = [packageId],
            };
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DevPackageLoadResult> LoadDevPackagesAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
    }
}
