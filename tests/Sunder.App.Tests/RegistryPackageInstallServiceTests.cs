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
        Assert.Collection(runtimeClient.StageRequests, request => Assert.Equal([PackageStoreMutationKind.Install, PackageStoreMutationKind.Install], request.Mutations.Select(mutation => mutation.Kind)));
        Assert.Single(runtimeClient.CommittedStageIds);
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
        Assert.Collection(runtimeClient.StageRequests, request => Assert.Equal([PackageStoreMutationKind.Upgrade], request.Mutations.Select(mutation => mutation.Kind)));
        Assert.Single(runtimeClient.CommittedStageIds);
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
        Assert.Single(runtimeClient.CommittedStageIds);
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
    public async Task InstallPackageAsync_WhenPreflightFails_DiscardsStageWithoutMutatingRuntime()
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
        var runtimeClient = new FakeRuntimeApiClient();

        var result = await service.InstallPackageAsync(
            "agent",
            version: null,
            tag: "latest",
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            runtimeClient,
            preflightPackageStoreStageAsync: (_, _) => throw new InvalidOperationException("preflight rejected"));

        Assert.False(result.Success);
        Assert.Contains("preflight rejected", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(runtimeClient.StageRequests);
        Assert.Empty(runtimeClient.CommittedStageIds);
        Assert.Single(runtimeClient.DiscardedStageIds);
        Assert.Empty(runtimeClient.InstalledPackageIds);
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
        Assert.Collection(runtimeClient.StageRequests, request => Assert.Equal([PackageStoreMutationKind.Install, PackageStoreMutationKind.Install, PackageStoreMutationKind.Install], request.Mutations.Select(mutation => mutation.Kind)));
        Assert.Single(runtimeClient.CommittedStageIds);
        Assert.Equal(["roota", "rootb"], registryClient.InstallPlanRequests.Select(request => request.PackageId));
        Assert.Contains(registryClient.InstallPlanRequests[1].InstalledPackages, package => package.PackageId == "dependency" && package.Version == "1.0.0");
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

    [Fact]
    public async Task UpdateAllAsync_ExecutesSingleResolvedPackageChangePlan()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            PackageChangesPlan = new RegistryResolveInstallPlanResponse(
                true,
                [
                    CreatePlanItem("dependency", "1.0.0", "1.1.0"),
                    CreatePlanItem("root", "1.0.0", "1.1.0"),
                ],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient(
            [
                CreateInstalledPackage("dependency", "1.0.0"),
                CreateInstalledPackage("root", "1.0.0", [new PackageDependencyDescriptor("dependency", ">=1.0.0")]),
            ]);

        var result = await service.UpdateAllAsync(registryClient, runtimeClient);

        Assert.True(result.Success);
        Assert.Single(registryClient.PackageChangesRequests);
        Assert.Equal(["dependency", "root"], registryClient.PackageChangesRequests[0].Packages.Select(package => package.PackageId));
        Assert.Empty(runtimeClient.InstalledPackageIds);
        Assert.Equal(["dependency", "root"], runtimeClient.UpgradedPackageIds);
        Assert.Collection(runtimeClient.StageRequests, request => Assert.Equal([PackageStoreMutationKind.Upgrade, PackageStoreMutationKind.Upgrade], request.Mutations.Select(mutation => mutation.Kind)));
        Assert.Single(runtimeClient.CommittedStageIds);
    }

    [Fact]
    public async Task UpdateAllAsync_WhenBatchResolverIsMethodNotAllowed_UsesCompatibilityPlan()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            PackageChangesPlan = new RegistryResolveInstallPlanResponse(
                false,
                [],
                [],
                ["Method Not Allowed"],
                []),
            UpdatesResponse = new RegistryResolveUpdatesResponse(
                [
                    new RegistryPackageUpdate(
                        "root",
                        "1.0.0",
                        "1.1.0",
                        DeprecatedMessage: null,
                        new RegistryPackageArtifact("", 0, "download/root/1.1.0")),
                ]),
        };
        registryClient.InstallPlansByPackageId["root"] = new RegistryResolveInstallPlanResponse(
            true,
            [
                CreatePlanItem("dependency", "1.0.0", "1.1.0"),
                CreatePlanItem("root", "1.0.0", "1.1.0"),
            ],
            [],
            [],
            []);
        var runtimeClient = new FakeRuntimeApiClient(
            [
                CreateInstalledPackage("dependency", "1.0.0"),
                CreateInstalledPackage("root", "1.0.0", [new PackageDependencyDescriptor("dependency", ">=1.0.0")]),
            ]);

        var result = await service.UpdateAllAsync(registryClient, runtimeClient);

        Assert.True(result.Success);
        Assert.Single(registryClient.PackageChangesRequests);
        Assert.Single(registryClient.ResolveUpdatesRequests);
        Assert.Collection(registryClient.InstallPlanRequests, request =>
        {
            Assert.Equal("root", request.PackageId);
            Assert.Equal("1.1.0", request.Version);
        });
        Assert.Equal(["dependency", "root"], runtimeClient.UpgradedPackageIds);
        Assert.Collection(runtimeClient.StageRequests, request => Assert.Equal([PackageStoreMutationKind.Upgrade, PackageStoreMutationKind.Upgrade], request.Mutations.Select(mutation => mutation.Kind)));
        Assert.Single(runtimeClient.CommittedStageIds);
        Assert.Contains(result.Warnings, warning => warning.Contains("compatibility", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateAllAsync_WhenBatchResolverReturnsPackageError_DoesNotUseCompatibilityPlan()
    {
        var service = new RegistryPackageInstallService();
        var registryClient = new FakeRegistryApiClient
        {
            PackageChangesPlan = new RegistryResolveInstallPlanResponse(
                false,
                [],
                [],
                ["Package 'root' was not found."],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("root", "1.0.0")]);

        var result = await service.UpdateAllAsync(registryClient, runtimeClient);

        Assert.False(result.Success);
        Assert.Contains("root", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registryClient.ResolveUpdatesRequests);
        Assert.Empty(registryClient.InstallPlanRequests);
        Assert.Empty(runtimeClient.UpgradedPackageIds);
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

    private static InstalledPackageDescriptor CreateInstalledPackage(
        string packageId,
        string version,
        IReadOnlyList<PackageDependencyDescriptor>? dependencies = null)
        => new(
            packageId,
            packageId,
            version,
            Summary: null,
            Icon: null,
            IsEnabled: true,
            DependsOn: dependencies ?? [],
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

        public RegistryResolveInstallPlanResponse PackageChangesPlan { get; init; } = new(true, [], [], [], []);

        public RegistryResolveUpdatesResponse UpdatesResponse { get; init; } = new([]);

        public Dictionary<string, RegistryResolveInstallPlanResponse> InstallPlansByPackageId { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<RegistryResolveUpdatesRequest> ResolveUpdatesRequests { get; } = [];

        public List<RegistryResolveInstallPlanRequest> InstallPlanRequests { get; } = [];

        public List<RegistryResolvePackageChangesRequest> PackageChangesRequests { get; } = [];

        public List<string> DownloadedPackageIds { get; } = [];

        public Uri RegistryUrl { get; } = new("http://registry.test/");

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);

        public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageDetails?>(null);

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageVersionDetails?>(null);

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(RegistryResolveUpdatesRequest request, CancellationToken cancellationToken = default)
        {
            ResolveUpdatesRequests.Add(request);
            return Task.FromResult(UpdatesResponse);
        }

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(RegistryResolveInstallPlanRequest request, CancellationToken cancellationToken = default)
        {
            InstallPlanRequests.Add(request);
            return Task.FromResult(ResolveInstallPlan?.Invoke(request)
                ?? (InstallPlansByPackageId.TryGetValue(request.PackageId, out var plan) ? plan : InstallPlan));
        }

        public Task<RegistryResolveInstallPlanResponse> ResolvePackageChangesAsync(RegistryResolvePackageChangesRequest request, CancellationToken cancellationToken = default)
        {
            PackageChangesRequests.Add(request);
            return Task.FromResult(PackageChangesPlan);
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
        private readonly Dictionary<string, PackageStoreStageRequest> _pendingStages = new(StringComparer.OrdinalIgnoreCase);

        public List<string> InstalledPackageIds { get; } = [];

        public List<string> UpgradedPackageIds { get; } = [];

        public List<IReadOnlyList<string>> ReloadedPackageIds { get; } = [];

        public List<PackageInstallBatchFromPathRequest> BatchInstallRequests { get; } = [];

        public List<bool> ApplyRuntimeSessionFlags { get; } = [];

        public List<PackageStoreStageRequest> StageRequests { get; } = [];

        public List<string> CommittedStageIds { get; } = [];

        public List<string> DiscardedStageIds { get; } = [];

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
            => InstallPackageFromPathAsync(packagePath, applyRuntimeSession: true, cancellationToken);

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, bool applyRuntimeSession, CancellationToken cancellationToken = default)
        {
            var packageId = Path.GetFileName(packagePath).Split('.')[0];
            InstalledPackageIds.Add(packageId);
            ApplyRuntimeSessionFlags.Add(applyRuntimeSession);
            var runtimeSessionApplied = applyRuntimeSession && OperationRuntimeSessionApplied;
            return Task.FromResult(new PackageOperationResult(
                true,
                "installed",
                runtimeSessionApplied,
                false,
                applyRuntimeSession && !OperationRuntimeSessionApplied ? ["Installed package changes are saved, but the running package session kept the previous loaded packages: test failure"] : [],
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
            => UpgradePackageFromPathAsync(packageId, packagePath, allowDowngrade, reinstall, applyRuntimeSession: true, cancellationToken);

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade, bool reinstall, bool applyRuntimeSession, CancellationToken cancellationToken = default)
        {
            UpgradedPackageIds.Add(packageId);
            ApplyRuntimeSessionFlags.Add(applyRuntimeSession);
            var runtimeSessionApplied = applyRuntimeSession && OperationRuntimeSessionApplied;
            return Task.FromResult(new PackageOperationResult(
                true,
                "upgraded",
                runtimeSessionApplied,
                false,
                applyRuntimeSession && !OperationRuntimeSessionApplied ? ["Installed package changes are saved, but the running package session kept the previous loaded packages: test failure"] : [],
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

        public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default)
        {
            StageRequests.Add(request);
            var stageId = Guid.NewGuid().ToString("N");
            _pendingStages[stageId] = request;
            var impactedPackageIds = request.Mutations.Select(GetMutationPackageId).ToArray();
            return Task.FromResult(new PackageStoreStageResult(
                stageId,
                new PackageOperationResult(true, "staged", RuntimeSessionApplied: false, RequiresAppRestart: false, [], [])
                {
                    ImpactedPackageIds = impactedPackageIds,
                },
                impactedPackageIds.Select(packageId => new ActivePackageDescriptor(packageId, packageId, "1.0.0", null, true, PackageReadinessState.Ready, [])).ToArray(),
                impactedPackageIds.Select(packageId => new PackageSourceDescriptor(packageId, PackageSourceKind.Installed, packageId)).ToArray()));
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
                    case PackageStoreMutationKind.Upgrade:
                        UpgradedPackageIds.Add(GetMutationPackageId(mutation));
                        break;
                }
            }

            ReloadedPackageIds.Add(impactedPackageIds);
            return Task.FromResult(new PackageOperationResult(
                true,
                "committed",
                FinalReloadRuntimeSessionApplied,
                false,
                FinalReloadRuntimeSessionApplied ? [] : ["Installed package changes are saved, but the running package session kept the previous loaded packages: final failure"],
                [])
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

        private static string GetMutationPackageId(PackageStoreMutationRequest mutation)
            => !string.IsNullOrWhiteSpace(mutation.PackageId)
                ? mutation.PackageId
                : Path.GetFileName(mutation.PackagePath ?? string.Empty).Split('.')[0];
    }
}
