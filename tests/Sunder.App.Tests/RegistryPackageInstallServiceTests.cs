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
        Assert.Single(runtimeClient.BatchInstallRequests);
        Assert.Collection(runtimeClient.ReloadedPackageIds, packageIds => Assert.Equal(["dependency", "root"], packageIds));
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
        Assert.Collection(runtimeClient.ReloadedPackageIds, packageIds => Assert.Equal(["agent"], packageIds));
    }

    [Fact]
    public async Task InstallPackageAsync_WhenPerPackageRuntimeReloadFails_UsesFinalReloadResult()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem("agent", null, "1.0.0")],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient
        {
            OperationRuntimeSessionApplied = false,
            FinalReloadRuntimeSessionApplied = true,
        };

        var result = await service.InstallPackageAsync(
            "agent",
            version: null,
            tag: "latest",
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            runtimeClient);

        Assert.True(result.Success);
        Assert.True(result.RuntimeSessionApplied);
        Assert.False(result.RequiresAppRestart);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("kept the previous loaded packages", StringComparison.OrdinalIgnoreCase));
        Assert.Collection(runtimeClient.ReloadedPackageIds, packageIds => Assert.Equal(["agent"], packageIds));
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

    [Fact]
    public async Task InstallPackagesAsync_CombinesStackPackageGraphsBeforeExecutingPlan()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            ResolveInstallPlan = request => request.PackageId switch
            {
                "roota" => new RegistryResolveInstallPlanResponse(
                    true,
                    [
                        CreatePlanItem("dependency", null, "1.0.0"),
                        CreatePlanItem("roota", null, "1.0.0"),
                    ],
                    [],
                    [],
                    []),
                "rootb" => new RegistryResolveInstallPlanResponse(
                    true,
                    [CreatePlanItem("rootb", null, "1.0.0")],
                    [],
                    [],
                    []),
                _ => new RegistryResolveInstallPlanResponse(false, [], [], [$"Unexpected package {request.PackageId}"], []),
            },
        };
        var runtimeClient = new FakeRuntimeApiClient();

        var result = await service.InstallPackagesAsync(
            [
                CreateStackRequirement("roota"),
                CreateStackRequirement("rootb"),
            ],
            registryClient,
            runtimeClient);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["dependency", "roota", "rootb"], result.PlanItems.Select(item => item.PackageId));
        Assert.Equal(["dependency", "roota", "rootb"], runtimeClient.InstalledPackageIds);
        Assert.Single(runtimeClient.BatchInstallRequests);
        Assert.Equal(["roota", "rootb"], registryClient.InstallPlanRequests.Select(request => request.PackageId));
        Assert.Contains(registryClient.InstallPlanRequests[1].InstalledPackages, package => package.PackageId == "dependency" && package.Version == "1.0.0");
        Assert.Collection(runtimeClient.ReloadedPackageIds, packageIds => Assert.Equal(["dependency", "roota", "rootb"], packageIds));
    }

    [Fact]
    public async Task ResolveInstallPlanForPackagesAsync_SkipsAlreadyCompatibleStackPackages()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            ResolveInstallPlan = request => new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem(request.PackageId, null, "1.0.0")],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("roota", "2.0.0")]);

        var result = await service.ResolveInstallPlanForPackagesAsync(
            [
                CreateStackRequirement("roota", minimumVersion: "1.0.0"),
                CreateStackRequirement("rootb", minimumVersion: "1.0.0"),
            ],
            registryClient,
            runtimeClient);

        Assert.True(result.Success);
        Assert.Equal(["rootb"], registryClient.InstallPlanRequests.Select(request => request.PackageId));
        Assert.Equal(["rootb"], result.Items.Select(item => item.PackageId));
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

    private static Sunder.PackageManagement.SunderStackPackageRequirement CreateStackRequirement(
        string packageId,
        string? minimumVersion = null)
        => new()
        {
            PackageId = packageId,
            InstallTag = "latest",
            MinimumVersion = minimumVersion,
            Required = true,
        };

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], []);

        public Func<RegistryResolveInstallPlanRequest, RegistryResolveInstallPlanResponse>? ResolveInstallPlan { get; init; }

        public List<RegistryResolveInstallPlanRequest> InstallPlanRequests { get; } = [];

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
        {
            InstallPlanRequests.Add(request);
            return Task.FromResult(ResolveInstallPlan?.Invoke(request) ?? InstallPlan);
        }

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

        public List<IReadOnlyList<string>> ReloadedPackageIds { get; } = [];

        public List<PackageInstallBatchFromPathRequest> BatchInstallRequests { get; } = [];

        public bool OperationRuntimeSessionApplied { get; init; } = true;

        public bool FinalReloadRuntimeSessionApplied { get; init; } = true;

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
            return Task.FromResult(new PackageOperationResult(
                true,
                "installed",
                OperationRuntimeSessionApplied,
                false,
                OperationRuntimeSessionApplied ? [] : ["Installed package changes are saved, but the running package session kept the previous loaded packages: test failure"],
                [])
            {
                ImpactedPackageIds = [packageId],
            });
        }

        public Task<PackageOperationResult> InstallPackagesFromPathsAsync(PackageInstallBatchFromPathRequest request, CancellationToken cancellationToken = default)
        {
            BatchInstallRequests.Add(request);
            var impactedPackageIds = new List<string>();
            foreach (var item in request.Items)
            {
                if (string.IsNullOrWhiteSpace(item.PackageId))
                {
                    var packageId = Path.GetFileName(item.PackagePath).Split('.')[0];
                    InstalledPackageIds.Add(packageId);
                    impactedPackageIds.Add(packageId);
                }
                else
                {
                    UpgradedPackageIds.Add(item.PackageId);
                    impactedPackageIds.Add(item.PackageId);
                }
            }

            ReloadedPackageIds.Add(impactedPackageIds.ToArray());
            return Task.FromResult(new PackageOperationResult(
                true,
                "installed batch",
                FinalReloadRuntimeSessionApplied,
                false,
                FinalReloadRuntimeSessionApplied ? [] : ["Installed package changes are saved, but the running package session kept the previous loaded packages: final failure"],
                [])
            {
                ImpactedPackageIds = impactedPackageIds.ToArray(),
            });
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default)
        {
            UpgradedPackageIds.Add(packageId);
            return Task.FromResult(new PackageOperationResult(
                true,
                "upgraded",
                OperationRuntimeSessionApplied,
                false,
                OperationRuntimeSessionApplied ? [] : ["Installed package changes are saved, but the running package session kept the previous loaded packages: test failure"],
                [])
            {
                ImpactedPackageIds = [packageId],
            });
        }

        public Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken = default)
        {
            ReloadedPackageIds.Add(impactedPackageIds.ToArray());
            return Task.FromResult(new PackageOperationResult(
                true,
                "reloaded",
                FinalReloadRuntimeSessionApplied,
                false,
                FinalReloadRuntimeSessionApplied ? [] : ["Installed package changes are saved, but the running package session kept the previous loaded packages: final failure"],
                [])
            {
                ImpactedPackageIds = impactedPackageIds.ToArray(),
            });
        }

        public Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default)
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
