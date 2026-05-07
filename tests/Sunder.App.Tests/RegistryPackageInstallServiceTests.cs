using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryPackageInstallServiceTests
{
    [Fact]
    public async Task InstallPackageAsync_ExecutesResolvedPlanInOrder()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [
                    CreatePlanItem("dependency", null, "1.0.0"),
                    CreatePlanItem("root", null, "1.0.0"),
                ],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient();

        var result = await service.InstallPackageAsync(
            "root",
            version: null,
            tag: "latest",
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            runtimeClient);

        Assert.True(result.Success);
        Assert.Equal(["dependency", "root"], runtimeClient.InstalledPackageIds);
        Assert.Equal(["dependency", "root"], result.ImpactedPackageIds);
        Assert.Equal(["dependency", "root"], registryClient.DownloadedPackageIds);
    }

    [Fact]
    public async Task InstallPackageAsync_UsesUpgradeForExistingPackage()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem("agent", "1.0.0", "1.1.0")],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", "1.0.0")]);

        var result = await service.InstallPackageAsync(
            "agent",
            version: "1.1.0",
            tag: null,
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            runtimeClient);

        Assert.True(result.Success);
        Assert.Empty(runtimeClient.InstalledPackageIds);
        Assert.Equal(["agent"], runtimeClient.UpgradedPackageIds);
    }

    [Fact]
    public async Task InstallPackageAsync_ReturnsPlanConflictsWithoutMutatingRuntime()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlan = new RegistryResolveInstallPlanResponse(
                false,
                [],
                [],
                [],
                [new RegistryPackageInstallPlanConflict("agent", "2.0.0", "<2.0.0", "root", "agent 2.0.0 conflicts with root")]),
        };
        var runtimeClient = new FakeRuntimeApiClient();

        var result = await service.InstallPackageAsync(
            "agent",
            version: "2.0.0",
            tag: null,
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            runtimeClient);

        Assert.False(result.Success);
        Assert.Contains("conflicts", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runtimeClient.InstalledPackageIds);
        Assert.Empty(runtimeClient.UpgradedPackageIds);
    }

    [Fact]
    public async Task UpdateAllAsync_ReturnsNoopWhenNothingIsInstalled()
    {
        var service = new RegistryPackageInstallService();
        var result = await service.UpdateAllAsync(new FakeRegistryApiClient(), new FakeRuntimeApiClient());

        Assert.True(result.Success);
        Assert.Contains("No packages", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryPackageInstallPlanItem CreatePlanItem(string packageId, string? currentVersion, string version)
        => new(
            packageId,
            currentVersion,
            version,
            currentVersion is not null,
            DeprecatedMessage: null,
            DependsOn: [],
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{version}"));

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

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], []);

        public List<string> DownloadedPackageIds { get; } = [];

        public Uri RegistryUrl { get; } = new("http://registry.test/");

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);

        public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageDetails?>(null);

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageVersionDetails?>(null);

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(RegistryResolveUpdatesRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryResolveUpdatesResponse([]));

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(RegistryResolveInstallPlanRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(InstallPlan);

        public Task DownloadArtifactAsync(RegistryPackageArtifact artifact, string packageId, string version, string destinationPath, CancellationToken cancellationToken = default)
        {
            DownloadedPackageIds.Add(packageId);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllText(destinationPath, $"{packageId}:{version}");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntimeApiClient(IReadOnlyList<InstalledPackageDescriptor>? installedPackages = null) : IRuntimeApiClient
    {
        private readonly List<InstalledPackageDescriptor> _installedPackages = installedPackages?.ToList() ?? [];

        public List<string> InstalledPackageIds { get; } = [];

        public List<string> UpgradedPackageIds { get; } = [];

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>([]);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>([]);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>(_installedPackages.ToArray());

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            var packageId = Path.GetFileName(packagePath).Split('.')[0];
            InstalledPackageIds.Add(packageId);
            return Task.FromResult(new PackageOperationResult(true, "installed", false, [], [])
            {
                ImpactedPackageIds = [packageId],
            });
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default)
        {
            UpgradedPackageIds.Add(packageId);
            return Task.FromResult(new PackageOperationResult(true, "upgraded", false, [], [])
            {
                ImpactedPackageIds = [packageId],
            });
        }

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

        public void Dispose()
        {
        }
    }
}
