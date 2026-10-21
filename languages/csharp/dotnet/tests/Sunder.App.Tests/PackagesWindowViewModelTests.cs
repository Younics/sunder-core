using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackagesWindowViewModelTests
{
    [Fact]
    public async Task EnableSelectedPackageCommand_UsesExplicitOperationExecutor()
    {
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("agent", isEnabled: false)]);
        var executor = new FakePackageOperationExecutor();
        using var viewModel = CreateViewModel(runtimeClient, operationExecutor: executor);

        await viewModel.InitializeAsync();
        await viewModel.EnableSelectedPackageCommand.ExecuteAsync(null);

        Assert.Equal(["agent"], executor.EnabledPackageIds);
        Assert.Equal("Queued enable for agent.", viewModel.Operations.StatusText);
    }

    [Fact]
    public async Task UnknownProvenancePackage_OffersExplicitRegistrySourceAdoption()
    {
        var package = CreateInstalledPackage("younics.ai.agent", isEnabled: false);
        var runtimeClient = new FakeRuntimeApiClient([package]);
        var executor = new FakePackageOperationExecutor();
        using var viewModel = CreateViewModel(runtimeClient, operationExecutor: executor);
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ShowAdoptSelectedInstalledPackageSource);
        Assert.True(viewModel.AdoptSelectedInstalledPackageSourceCommand.CanExecute(null));
        Assert.True(viewModel.EnableSelectedPackageCommand.CanExecute(null));

        await viewModel.AdoptSelectedInstalledPackageSourceCommand.ExecuteAsync(null);

        Assert.Equal("younics.ai.agent", executor.AdoptedPackageId);
        Assert.Equal(new Uri("https://registry.example/"), executor.AdoptionRegistryUrl);
        Assert.Equal(
            "Queued Registry source adoption for younics.ai.agent.",
            viewModel.Operations.StatusText);
    }

    [Fact]
    public async Task EnableSelectedPackageCommand_WhenOperationServicePresent_QueuesBackgroundOperation()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        );
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var lifecycleApplications = new List<RuntimePackageStamp>();
        using var operationService = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (stamp, _) =>
            {
                lifecycleApplications.Add(stamp);
                return Task.CompletedTask;
            },
            notificationCenter);
        using var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            operationService,
            backgroundProcessQueue: queue,
            registryClientFactory: null)
        {
            Mode = PackageWindowMode.Installed,
            RegistryUrlText = string.Empty,
        };

        await viewModel.InitializeAsync();
        await viewModel.EnableSelectedPackageCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => queue.ListProcesses().Any(IsCompletedEnableOperation));

        Assert.Equal(["agent"], runtimeClient.EnabledPackageIds);
        Assert.Single(lifecycleApplications);
    }

    [Fact]
    public async Task InstalledPackageCommands_CanExecuteReflectsSelectionAndBusyState()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        );
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.RefreshCommand.CanExecute(null));
        Assert.True(viewModel.InstallPackageCommand.CanExecute(null));
        Assert.True(viewModel.EnableSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.DisableSelectedPackageCommand.CanExecute(null));
        Assert.True(viewModel.UninstallSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.UpdateSelectedInstalledPackageCommand.CanExecute(null));

        viewModel.Operations.IsBusy = true;

        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.InstallPackageCommand.CanExecute(null));
        Assert.False(viewModel.EnableSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.DisableSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.UninstallSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.UpdateSelectedInstalledPackageCommand.CanExecute(null));
    }

    [Fact]
    public void OperationPresentation_BusyLeaseDoesNotClearAnotherActiveOperation()
    {
        var presentation = new PackageOperationPresentationViewModel(new FakePackageOperationExecutor());
        var first = presentation.EnterBusy();
        var second = presentation.EnterBusy();

        first.Dispose();

        Assert.True(presentation.IsBusy);

        second.Dispose();

        Assert.False(presentation.IsBusy);
    }

    [Fact]
    public async Task HeaderUpdateAllPackages_WhenUpdatesAreAvailable_IsVisibleInInstalledAndMarketplaceModes()
    {
        var registryClient = new FakeRegistryApiClient
        {
            Updates = [CreateUpdate("younics.ai.agent", "1.0.0", "1.1.0")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([CreateInstalledPackage(
                "younics.ai.agent",
                isEnabled: true,
                isRegistryManaged: true)])
            {
                RegistryUpdates = registryClient.Updates,
            },
            _ => registryClient);
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.InitializeAsync();

        Assert.Equal(1, viewModel.AvailableUpdateCount);
        Assert.True(viewModel.ShowHeaderUpdateAllPackages);

        viewModel.Mode = PackageWindowMode.Marketplace;

        Assert.True(viewModel.ShowHeaderUpdateAllPackages);
    }

    [Fact]
    public async Task InstalledPackages_UsePackageIconAssetUriWhenAvailable()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [
                CreateInstalledPackage(
                    "agent",
                    isEnabled: true,
                    new PackageIconDescriptor(null, "assets/icons/agent.svg")
                ),
            ]
        );
        var viewModel = CreateViewModel(runtimeClient);

        await viewModel.InitializeAsync();

        var package = Assert.Single(viewModel.Installed.Packages);
        Assert.Equal(
            new Uri("file:///packages/agent/assets/assets/icons/agent.svg"),
            package.IconUri
        );
        Assert.Equal("A", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
        Assert.True(viewModel.ShowSelectedPackageIcon);
        Assert.Equal("A", viewModel.SelectedPackageGlyph);
        Assert.True(viewModel.SelectedPackageShowGlyphFallback);
    }

    [Fact]
    public async Task InstalledPackageDetails_ProjectDeclaredRpcAccess()
    {
        var runtimeClient = new FakeRuntimeApiClient(
        [
            CreateInstalledPackage(
                "agent",
                isEnabled: true,
                rpcContractUses:
                [
                    new PackageRpcContractUseDescriptor(
                        SunderPackageFormat.RuntimeHostRole,
                        "dev.sunder.execution",
                        "1.4.2+desktop",
                        new string('a', 64),
                        Required: true,
                        Actions: ["invoke", "discover"]),
                ]),
        ]);
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.InitializeAsync();

        var package = Assert.IsType<PackageCatalogItemViewModel>(viewModel.SelectedInstalledPackage);
        var access = Assert.Single(package.RpcContractUses);
        Assert.Equal(SunderPackageFormat.RuntimeHostRole, access.Role);
        Assert.Equal("dev.sunder.execution", access.ContractId);
        Assert.Equal("1.4.2+desktop", access.ContractVersion);
        Assert.Equal("Runtime caller", access.CallerRoleText);
        Assert.Equal("Version 1.4.2+desktop", access.ContractVersionText);
        Assert.Equal("Required", access.RequirementText);
        Assert.Equal("discover | invoke", access.ActionsText);
        Assert.Equal("1 contract | 2 actions", package.RpcAccessSummary);
    }

    [Fact]
    public async Task InstalledPackageDetails_ProjectExecutionRuntimeCompatibilityAndMissingWarnings()
    {
        var healthyBinding = CreateExecutionRuntimeBinding("dotnet", "8.0.1", "linux-x64");
        var missingBinding = CreateExecutionRuntimeBinding("node", "22.1.0", "linux-x64");
        var runtimeClient = new FakeRuntimeApiClient([
            CreateInstalledPackage(
                "agent",
                isEnabled: true,
                executionRuntimeBindings: [healthyBinding, missingBinding]),
        ])
        {
            ExecutionRuntimes = [CreateExecutionRuntimeSummary(healthyBinding, healthy: true)],
        };
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasSelectedPackageExecutionRuntimes);
        Assert.Equal(2, viewModel.SelectedPackageExecutionRuntimes.Count);
        Assert.Contains("1 of 2", viewModel.SelectedPackageExecutionRuntimeSummary, StringComparison.Ordinal);
        var healthy = Assert.Single(viewModel.SelectedPackageExecutionRuntimes, runtime => runtime.Title.StartsWith("dotnet", StringComparison.Ordinal));
        var missing = Assert.Single(viewModel.SelectedPackageExecutionRuntimes, runtime => runtime.Title.StartsWith("node", StringComparison.Ordinal));
        Assert.Equal("Compatible", healthy.StatusText);
        Assert.False(healthy.ShowWarning);
        Assert.Equal("Missing", missing.StatusText);
        Assert.True(missing.ShowWarning);
        Assert.True(missing.CanRepair);
        Assert.Equal("Restore", missing.ActionText);
    }

    [Fact]
    public async Task RepairExecutionRuntimeCommand_DelegatesExactIdentityAndRefreshesCompatibility()
    {
        var binding = CreateExecutionRuntimeBinding("dotnet", "8.0.1", "linux-x64");
        RuntimeExecutionRuntimeIdentity? repairedIdentity = null;
        var runtimeClient = new FakeRuntimeApiClient([
            CreateInstalledPackage(
                "agent",
                isEnabled: true,
                executionRuntimeBindings: [binding]),
        ])
        {
            ExecutionRuntimes = [CreateExecutionRuntimeSummary(binding, healthy: false)],
        };
        runtimeClient.RepairRuntime = (identity, _) =>
        {
            repairedIdentity = identity;
            var healthy = CreateExecutionRuntimeSummary(binding, healthy: true);
            runtimeClient.ExecutionRuntimes = [healthy];
            return Task.FromResult(new RuntimeExecutionRuntimeOperationResult(
                true,
                null,
                "Execution runtime repaired.",
                RuntimeExecutionRuntimeOperationActions.Repair,
                healthy,
                AlreadyPresent: false));
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.InitializeAsync();
        var runtime = Assert.Single(viewModel.SelectedPackageExecutionRuntimes);
        Assert.True(runtime.CanRepair);
        Assert.True(viewModel.RepairExecutionRuntimeCommand.CanExecute(runtime));

        await viewModel.RepairExecutionRuntimeCommand.ExecuteAsync(runtime);

        Assert.Equal(binding.Runtime, repairedIdentity);
        Assert.Same(runtimeClient.RuntimeImpact, runtimeClient.LastRepairImpact);
        Assert.Equal("Compatible", Assert.Single(viewModel.SelectedPackageExecutionRuntimes).StatusText);
        Assert.Equal("Execution runtime repaired.", viewModel.Operations.StatusText);
    }

    [Fact]
    public async Task RepairExecutionRuntimeCommand_RestoredRuntimeRefreshesAfterReloadFailure()
    {
        var binding = CreateExecutionRuntimeBinding("dotnet", "8.0.1", "linux-x64");
        var runtimeClient = new FakeRuntimeApiClient([
            CreateInstalledPackage(
                "agent",
                isEnabled: true,
                executionRuntimeBindings: [binding]),
        ]);
        runtimeClient.RepairRuntime = (_, _) =>
        {
            var healthy = CreateExecutionRuntimeSummary(binding, healthy: true);
            runtimeClient.ExecutionRuntimes = [healthy];
            return Task.FromResult(new RuntimeExecutionRuntimeOperationResult(
                false,
                ExecutionRuntimeErrorCodes.InstallFailed,
                "Runtime restored, but package reload failed.",
                RuntimeExecutionRuntimeOperationActions.Repair,
                healthy,
                AlreadyPresent: false,
                runtimeClient.RuntimeImpact));
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.InitializeAsync();
        var missing = Assert.Single(viewModel.SelectedPackageExecutionRuntimes);
        Assert.Equal("Missing", missing.StatusText);

        await viewModel.RepairExecutionRuntimeCommand.ExecuteAsync(missing);

        Assert.Equal("Compatible", Assert.Single(viewModel.SelectedPackageExecutionRuntimes).StatusText);
        Assert.Equal("Runtime restored, but package reload failed.", viewModel.Operations.StatusText);
    }

    [Fact]
    public async Task RepairExecutionRuntimeCommand_RequiresConfirmationForSharedImpact()
    {
        var binding = CreateExecutionRuntimeBinding("dotnet", "8.0.1", "linux-x64");
        var impact = new RuntimeExecutionRuntimeImpact(
            2,
            ["agent", "tools"],
            new string('e', 64));
        var runtimeClient = new FakeRuntimeApiClient([
            CreateInstalledPackage(
                "agent",
                isEnabled: true,
                executionRuntimeBindings: [binding]),
        ])
        {
            ExecutionRuntimes = [CreateExecutionRuntimeSummary(binding, healthy: false)],
            RuntimeImpact = impact,
            RepairRuntime = (_, _) => throw new InvalidOperationException("Repair must not run without confirmation."),
        };
        RuntimeExecutionRuntimeImpact? confirmedImpact = null;
        using var viewModel = CreateViewModel(
            runtimeClient,
            confirmSharedRuntimeRepair: (candidate, _) =>
            {
                confirmedImpact = candidate;
                return Task.FromResult(false);
            });
        await viewModel.InitializeAsync();

        await viewModel.RepairExecutionRuntimeCommand.ExecuteAsync(
            Assert.Single(viewModel.SelectedPackageExecutionRuntimes));

        Assert.Same(impact, confirmedImpact);
        Assert.Null(runtimeClient.LastRepairImpact);
        Assert.Equal("Execution runtime repair cancelled.", viewModel.Operations.StatusText);
    }

    [Fact]
    public async Task RefreshInstalledPackages_PreservesUnchangedRowsAndReplacesChangedRows()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [
                CreateInstalledPackage("agent", isEnabled: true),
                CreateInstalledPackage(
                    "tools",
                    isEnabled: true,
                    rpcContractUses:
                    [
                        new PackageRpcContractUseDescriptor(
                            SunderPackageFormat.AppHostRole,
                            "dev.sunder.tools",
                            "1.0.0",
                            new string('b', 64),
                            Required: true,
                            Actions: ["invoke"]),
                    ]),
            ]
        );
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.InitializeAsync();
        var originalAgent = Assert.Single(viewModel.Installed.Packages, package => package.PackageId == "agent");
        var originalTools = Assert.Single(viewModel.Installed.Packages, package => package.PackageId == "tools");

        runtimeClient.AddInstalledPackage(CreateInstalledPackage("agent", isEnabled: false));
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.NotSame(originalAgent, Assert.Single(viewModel.Installed.Packages, package => package.PackageId == "agent"));
        Assert.Same(originalTools, Assert.Single(viewModel.Installed.Packages, package => package.PackageId == "tools"));
    }

    [Fact]
    public async Task PackageSnapshotReadiness_RemainsCurrentAfterOlderCatalogRefresh()
    {
        var ready = CreateSessionPackage("agent", PackageReadinessState.Ready);
        var runtimeClient = new FakeRuntimeApiClient([
            CreateInstalledPackage("agent", isEnabled: true),
        ])
        {
            SessionPackages = [ready],
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.InitializeAsync();
        var snapshot = new RuntimePackageSnapshot(
            Guid.NewGuid(),
            SessionGeneration: 1,
            EventSequence: 2,
            RuntimeBootstrapState.Ready,
            ActivePackages: [],
            SessionPackages: [ready with { Readiness = PackageReadinessState.Degraded }],
            Warnings: [],
            Errors: []);

        await viewModel.ApplyPackageSnapshotAsync(snapshot, CancellationToken.None);
        Assert.Equal(
            "Degraded",
            Assert.Single(viewModel.Installed.Packages, package => package.PackageId == "agent").StatusText);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(
            "Degraded",
            Assert.Single(viewModel.Installed.Packages, package => package.PackageId == "agent").StatusText);
    }

    [Fact]
    public async Task DevOverlayDetails_UseActiveSessionRpcAccessInsteadOfInstalledManifest()
    {
        var runtimeClient = new FakeRuntimeApiClient(
        [
            CreateInstalledPackage(
                "agent",
                isEnabled: true,
                rpcContractUses:
                [
                    new PackageRpcContractUseDescriptor(
                        SunderPackageFormat.RuntimeHostRole,
                        "example.installed",
                        "1.0.0",
                        new string('c', 64),
                        Required: true,
                        Actions: ["invoke"]),
                ]),
        ])
        {
            SessionPackages =
            [
                new SessionPackageDescriptor(
                    "agent",
                    "Agent",
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    Views: [],
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0,
                    RpcContractUses:
                    [
                        new PackageRpcContractUseDescriptor(
                            SunderPackageFormat.AppHostRole,
                            "example.development",
                            "2.0.0",
                            new string('d', 64),
                            Required: true,
                            Actions: ["discover", "invoke"]),
                    ]),
            ],
        };
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.InitializeAsync();

        var access = Assert.Single(viewModel.SelectedInstalledPackage!.RpcContractUses);
        Assert.Equal(SunderPackageFormat.AppHostRole, access.Role);
        Assert.Equal("example.development", access.ContractId);
        Assert.Equal("2.0.0", access.ContractVersion);
        Assert.Equal("discover | invoke", access.ActionsText);
    }

    [Fact]
    public void MarketplacePackages_UseRegistryIconUrlWhenAvailable()
    {
        var iconUri = new Uri(
            "http://127.0.0.1:1/api/v1/packages/younics.ai.agent/versions/1.0.0/icon"
        );
        using var package = new RegistryPackageSearchItemViewModel(
            new RegistryPackageSummary(
                "younics.ai.agent",
                "AI Agent",
                "Adds local agents.",
                "1.0.0",
                iconUri.ToString(),
                IsYanked: false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow
            ),
            installedVersion: null,
            update: null,
            _ => Task.CompletedTask,
            loadIcon: false
        );

        Assert.Equal(iconUri, package.IconUri);
        Assert.Equal("A", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
    }

    [Fact]
    public async Task SearchText_WhenMarketplaceMode_SearchesRegistryAfterThrottle()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
        };
        var runtimeClient = new FakeRuntimeApiClient([]);
        using var viewModel = CreateViewModel(
            runtimeClient,
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";

        viewModel.SearchText = " agent ";

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("agent", query));
        Assert.Collection(
            viewModel.Marketplace.Packages,
            package => Assert.Equal("younics.ai.agent", package.PackageId)
        );
        Assert.True(viewModel.HasSearchText);
    }

    [Fact]
    public async Task MarketplaceSortOption_WhenChanged_SearchesWithSelectedSort()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        viewModel.SelectedMarketplaceSortOption = viewModel.MarketplaceSortOptions.Single(option => option.Sort == RegistrySearchSort.Stars);

        await WaitForConditionAsync(() => registryClient.SearchSorts.Count == 2);

        Assert.Collection(
            registryClient.SearchSorts,
            sort => Assert.Equal(RegistrySearchSort.Downloads, sort),
            sort => Assert.Equal(RegistrySearchSort.Stars, sort));
    }

    [Fact]
    public async Task ApplyLaunchRequestAsync_WhenPackageDetailsLink_SelectsExactMarketplacePackageWithoutInstalling()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = query => string.Equals(query, "younics.ai.agent", StringComparison.OrdinalIgnoreCase)
                ? [CreateRegistryPackage("younics.ai.agent.tools.files"), CreateRegistryPackage("younics.ai.agent")]
                : [],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40),
            marketplaceDetailSpinnerDelay: TimeSpan.FromMilliseconds(40));

        await viewModel.ApplyLaunchRequestAsync(new AppLaunchRequest(
            AppLaunchRequestKind.PackageDetails,
            PackageId: "younics.ai.agent",
            RegistryUrl: new Uri("https://registry.example/")));

        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.Equal("younics.ai.agent", viewModel.SearchText);
        Assert.Equal("younics.ai.agent", viewModel.SelectedMarketplacePackage?.PackageId);
        Assert.Collection(viewModel.Marketplace.Packages, package => Assert.Equal("younics.ai.agent", package.PackageId));
        Assert.True(viewModel.ShowMarketplaceInstallButton);
        Assert.Contains("Loaded younics.ai.agent", viewModel.Operations.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("younics.ai.agent", query));
    }

    [Fact]
    public async Task SearchText_WhenChangedBeforeThrottleElapsed_SearchesOnlyLatestText()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(50)
        );
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";

        viewModel.SearchText = "a";
        viewModel.SearchText = "agent";

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("agent", query));
    }

    [Fact]
    public async Task ClearSearchCommand_WhenMarketplaceMode_ClearsTextAndRefreshesResults()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = query =>
                query is null
                    ? [CreateRegistryPackage("sunder.package.tools")]
                    : [CreateRegistryPackage("younics.ai.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";
        viewModel.SearchText = "agent";
        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        viewModel.ClearSearchCommand.Execute(null);

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 2);

        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.False(viewModel.HasSearchText);
        Assert.Collection(
            registryClient.SearchQueries,
            query => Assert.Equal("agent", query),
            Assert.Null
        );
        Assert.Collection(
            viewModel.Marketplace.Packages,
            package => Assert.Equal("sunder.package.tools", package.PackageId)
        );
    }

    [Fact]
    public async Task MarketplaceSelection_WhenEarlierDetailsCompleteLater_DoesNotOverwriteCurrentDetails()
    {
        var agentDetailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentDetailsCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAgentDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ =>
            [
                CreateRegistryPackage("younics.ai.agent", "9.0.0"),
                CreateRegistryPackage("sunder.package.tools", "2.0.0"),
            ],
            PackageDetails = async (packageId, cancellationToken) =>
            {
                if (string.Equals(packageId, "younics.ai.agent", StringComparison.OrdinalIgnoreCase))
                {
                    agentDetailsStarted.SetResult();
                    try
                    {
                        await releaseAgentDetails.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        agentDetailsCancelled.SetResult();
                        throw;
                    }

                    return CreateRegistryPackageDetails(packageId, "9.0.0");
                }

                return CreateRegistryPackageDetails(packageId, "2.0.0");
            },
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.RegistryUrlText = "https://registry.example/";

        var searchTask = viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await agentDetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.Marketplace.Packages[1].SelectCommand.ExecuteAsync(null);
        await agentDetailsCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Sunder.package.tools", viewModel.SelectedPackageTitle);
        Assert.Collection(viewModel.Marketplace.Versions, version => Assert.Equal("2.0.0", version.Version));

        releaseAgentDetails.SetResult();
        await searchTask;

        Assert.Equal("Sunder.package.tools", viewModel.SelectedPackageTitle);
        Assert.Collection(viewModel.Marketplace.Versions, version => Assert.Equal("2.0.0", version.Version));
    }

    [Fact]
    public async Task MarketplaceSelection_WhenDetailsAreLoading_ClearsStaleDetailContent()
    {
        var toolsDetailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToolsDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ =>
            [
                CreateRegistryPackage("younics.ai.agent", "9.0.0"),
                CreateRegistryPackage("sunder.package.tools", "2.0.0"),
            ],
            PackageDetails = async (packageId, cancellationToken) =>
            {
                if (string.Equals(packageId, "sunder.package.tools", StringComparison.OrdinalIgnoreCase))
                {
                    toolsDetailsStarted.SetResult();
                    await releaseToolsDetails.Task.WaitAsync(cancellationToken);
                }

                return CreateRegistryPackageDetails(packageId, packageId.EndsWith("tools", StringComparison.OrdinalIgnoreCase) ? "2.0.0" : "9.0.0");
            },
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";
        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        Assert.True(viewModel.MarketplacePackageDetailsLoaded);
        Assert.Collection(viewModel.Marketplace.Versions, version => Assert.Equal("9.0.0", version.Version));

        var selectTask = viewModel.Marketplace.Packages[1].SelectCommand.ExecuteAsync(null);
        await toolsDetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsMarketplacePackageDetailsLoading);
        Assert.False(viewModel.MarketplacePackageDetailsLoaded);
        Assert.False(viewModel.ShowMarketplacePackageDetailsLoading);
        Assert.False(viewModel.ShowMarketplacePackageDetailsContent);
        Assert.Equal(string.Empty, viewModel.SelectedPackageSummary);
        Assert.Empty(viewModel.Marketplace.Versions);
        Assert.False(viewModel.ShowNoMarketplaceVersions);
        Assert.False(viewModel.CanInstallSelectedMarketplacePackage);

        await WaitForConditionAsync(() => viewModel.ShowMarketplacePackageDetailsLoading);

        releaseToolsDetails.SetResult();
        await selectTask;

        Assert.False(viewModel.IsMarketplacePackageDetailsLoading);
        Assert.True(viewModel.MarketplacePackageDetailsLoaded);
        Assert.True(viewModel.ShowMarketplacePackageDetailsContent);
        Assert.Collection(viewModel.Marketplace.Versions, version => Assert.Equal("2.0.0", version.Version));
    }

    [Fact]
    public async Task MarketplaceSelection_WhenDetailsLoadBeforeDelay_DoesNotShowLoadingSpinner()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40),
            marketplaceDetailSpinnerDelay: TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await Task.Delay(80);

        Assert.True(viewModel.MarketplacePackageDetailsLoaded);
        Assert.False(viewModel.IsMarketplacePackageDetailsLoading);
        Assert.False(viewModel.ShowMarketplacePackageDetailsLoading);
    }

    [Fact]
    public async Task MarketplaceVersionSelection_LoadsDeclaredRpcAccess()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
            VersionDetails = (packageId, version, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<RegistryPackageVersionDetails?>(CreateRegistryPackageVersionDetails(
                    packageId,
                    version,
                    [
                        new RegistryPackageContractUse(
                            SunderPackageFormat.AppHostRole,
                            "dev.sunder.execution",
                            "1.7.3",
                            new string('e', 64),
                            Required: true,
                            Actions: ["invoke", "discover"]),
                    ]));
            },
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => viewModel.Marketplace.RpcContractUses.Count == 1);

        var access = Assert.Single(viewModel.Marketplace.RpcContractUses);
        Assert.Equal(SunderPackageFormat.AppHostRole, access.Role);
        Assert.Equal("dev.sunder.execution", access.ContractId);
        Assert.Equal("1.7.3", access.ContractVersion);
        Assert.Equal("App caller", access.CallerRoleText);
        Assert.Equal("Version 1.7.3", access.ContractVersionText);
        Assert.Equal("discover | invoke", access.ActionsText);
        Assert.Equal("1 contract | 2 actions", viewModel.Marketplace.RpcAccessSummary);
    }

    [Fact]
    public async Task MarketplaceVersionSelection_ShowsCurrentRidRuntimeLifecycleAndInstalledReuse()
    {
        var rid = AppPackageTargetEnvironment.CurrentRid;
        var requirement = new RegistryExecutionRuntimeRequirement(
            "sunder.runtime.dotnet",
            ">=10.0.0 <11.0.0",
            "sunder.dotnet-worker-module.v1");
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
            VersionDetails = (packageId, version, _) => Task.FromResult<RegistryPackageVersionDetails?>(
                CreateRegistryPackageVersionDetails(
                    packageId,
                    version,
                    targets: [new RegistryPackageTarget(
                        SunderPackageFormat.RuntimeHostRole,
                        rid,
                        SunderPackageFormat.WorkerTargetKind,
                        "lib/Agent.dll",
                        "net10.0",
                        "1.1.0",
                        ["execution-runtime.v1", "rpc.v1", "worker-protocol.v1", "sdk-baseline-1-1.v1"],
                        ExecutionRuntime: requirement)])),
        };
        var runtimeArtifact = CreateResolvedRuntimeArtifact(rid, RuntimeExecutionRuntimeSupportState.Supported);
        var runtimeClient = new FakeRuntimeApiClient([])
        {
            RegistryPlanResult = CreateRuntimePlan(requirement, runtimeArtifact),
            ExecutionRuntimes = [CreateInstalledRuntime(runtimeArtifact)],
        };
        using var viewModel = CreateViewModel(
            runtimeClient,
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => viewModel.Marketplace.RuntimePreflightLoaded);

        Assert.Equal(rid, viewModel.Marketplace.CurrentRid);
        Assert.Equal("8 MB", viewModel.Marketplace.PackageDownloadText);
        Assert.Equal("0 bytes (already installed)", viewModel.Marketplace.SharedRuntimeDownloadText);
        Assert.Contains("already installed", viewModel.Marketplace.RuntimeCompatibilitySummary, StringComparison.OrdinalIgnoreCase);
        var runtime = Assert.Single(viewModel.Marketplace.ExecutionRuntimeRequirements);
        Assert.Equal("sunder.runtime.dotnet", runtime.RuntimeId);
        Assert.Equal(">=10.0.0 <11.0.0", runtime.VersionRange);
        Assert.Equal("10.0.1", runtime.ResolvedRuntime);
        Assert.Equal("Installed", runtime.StatusText);
        Assert.Contains("already installed", runtime.Availability, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CanInstallSelectedMarketplacePackage);
    }

    [Fact]
    public async Task MarketplaceVersionAccess_WhileLoadingOrUnavailable_DisablesInstallWithoutClaimingNoAccess()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
            VersionDetails = async (packageId, version, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(cancellationToken);
                return null;
            },
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.Marketplace.RpcAccessLoading);
        Assert.False(viewModel.Marketplace.HasNoRpcContractUses);
        Assert.False(viewModel.CanInstallSelectedMarketplacePackage);
        Assert.Equal("Loading declared access...", viewModel.Marketplace.RpcAccessSummary);

        releaseRequest.SetResult();
        await WaitForConditionAsync(() => viewModel.Marketplace.HasRpcAccessError);

        Assert.False(viewModel.Marketplace.RpcAccessLoaded);
        Assert.False(viewModel.Marketplace.HasNoRpcContractUses);
        Assert.False(viewModel.CanInstallSelectedMarketplacePackage);
        Assert.Equal("Declared access unavailable", viewModel.Marketplace.RpcAccessSummary);
    }

    [Fact]
    public async Task MarketplaceMissingRuntimeKeepsInstallEntryPointAvailableForPrerequisiteConsent()
    {
        var rid = AppPackageTargetEnvironment.CurrentRid;
        var requirement = new RegistryExecutionRuntimeRequirement("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");
        var artifact = CreateResolvedRuntimeArtifact(rid, RuntimeExecutionRuntimeSupportState.Supported);
        var installation = new RuntimeExecutionRuntimeInstallPlan(true, null, "Install runtime first", TestSupport.RuntimeManagementTestClient.Plan(),
            new(new(requirement.RuntimeId, requirement.VersionRange, requirement.ModuleAbi), artifact,
                new('f', 64), DateTimeOffset.UtcNow.AddMinutes(5), false, artifact.Distribution.DownloadSize), true);
        var plan = CreateRuntimePlan(requirement, artifact) with
        {
            Success = false,
            RuntimePrerequisites = new(true, false, "execution-runtime.missing", "Runtime required", new('a', 64), rid,
                [new(new("younics.ai.agent", "1.0.0", new('a', 64), "runtime", rid), new('a', 64),
                    new(requirement.RuntimeId, requirement.VersionRange, requirement.ModuleAbi), false, "execution-runtime.missing", null, installation)], []),
        };
        var registry = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
            VersionDetails = (id, version, _) => Task.FromResult<RegistryPackageVersionDetails?>(CreateRegistryPackageVersionDetails(id, version,
                targets: [new("runtime", rid, "worker", "lib/Agent.dll", "net10.0", "1.1.0", [], ExecutionRuntime: requirement)])),
        };
        using var viewModel = CreateViewModel(new FakeRuntimeApiClient([]) { RegistryPlanResult = plan }, _ => registry, TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";
        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => viewModel.Marketplace.RuntimePreflightLoaded);
        Assert.True(viewModel.CanInstallSelectedMarketplacePackage);
        Assert.Contains("runtime first", viewModel.Marketplace.SharedRuntimeDownloadText);
        Assert.False(Assert.Single(viewModel.Marketplace.ExecutionRuntimeRequirements).IsBlocked);
    }

    [Fact]
    public async Task MarketplaceSelection_WhenDetailsContainProfile_PopulatesProfileSections()
    {
        var profile = new RegistryPackageProfile(
            "younics.ai.agent",
            "Detailed local agent package.",
            "# AI Agent",
            "https://example.test/agent",
            "https://example.test/source",
            "https://example.test/issues",
            " MIT ",
            ["agent", " local ", "agent", ""],
            [],
            DateTimeOffset.UtcNow);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
            PackageDetails = (packageId, _) => Task.FromResult<RegistryPackageDetails?>(
                CreateRegistryPackageDetails(packageId, "1.0.0", profile)),
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);

        Assert.Equal("Detailed local agent package.", viewModel.SelectedPackageSummary);
        Assert.True(viewModel.HasMarketplaceReadme);
        Assert.True(viewModel.HasMarketplaceProfile);
        Assert.False(viewModel.HasMarketplaceProfileMedia);
        Assert.Collection(
            viewModel.Marketplace.ProfileLinks,
            link =>
            {
                Assert.Equal("Website", link.Label);
                Assert.Equal(new Uri("https://example.test/agent"), link.NavigateUri);
            },
            link =>
            {
                Assert.Equal("Source", link.Label);
                Assert.Equal(new Uri("https://example.test/source"), link.NavigateUri);
            },
            link =>
            {
                Assert.Equal("Issues", link.Label);
                Assert.Equal(new Uri("https://example.test/issues"), link.NavigateUri);
            });
        Assert.Collection(
            viewModel.Marketplace.ProfileMetadata,
            item =>
            {
                Assert.Equal("License", item.Label);
                Assert.Equal("MIT", item.Value);
            });
        Assert.Collection(
            viewModel.Marketplace.ProfileTags,
            tag => Assert.Equal("agent", tag),
            tag => Assert.Equal("local", tag));
    }

    [Fact]
    public async Task MarketplacePackage_CanToggleStar()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
            PackageDetails = (packageId, _) => Task.FromResult<RegistryPackageDetails?>(new RegistryPackageDetails(
                packageId,
                ToDisplayName(packageId),
                null,
                "1.0.0",
                null,
                [new RegistryPackageVersionSummary("1.0.0", false, null, DateTimeOffset.UtcNow)],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Stats: new RegistryPackageStats(4, 0, 0, [], Stars: 1, IsStarred: false))),
            StarPackageResponse = new RegistryPackageStarResponse(true, "Starred package.", new RegistryPackageStats(4, 0, 0, [], Stars: 2, IsStarred: true), []),
        };
        var runtimeClient = new FakeRuntimeApiClient([]);
        using var viewModel = CreateViewModel(
            runtimeClient,
            _ => registryClient,
            TimeSpan.FromMilliseconds(40),
            registryUrl => new object());
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await viewModel.ToggleSelectedMarketplacePackageStarCommand.ExecuteAsync(null);

        Assert.Equal("Starred package.", viewModel.Operations.StatusText);
        Assert.Equal("4 downloads · 2 stars", viewModel.MarketplacePackageStatsText);
        Assert.Equal("Unstar", viewModel.MarketplacePackageStarActionText);
        Assert.True(viewModel.SelectedMarketplacePackageIsStarred);
        Assert.Equal("younics.ai.agent", runtimeClient.LastRegistryStarRequest?.ResourceId);
    }

    [Fact]
    public async Task InstallSelectedMarketplacePackageCommand_InstallsSelectedVersion()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent", "2.0.0")],
            PackageDetails = (packageId, _) => Task.FromResult<RegistryPackageDetails?>(new RegistryPackageDetails(
                packageId,
                ToDisplayName(packageId),
                null,
                "2.0.0",
                null,
                [
                    new RegistryPackageVersionSummary("2.0.0", false, null, DateTimeOffset.UtcNow),
                    new RegistryPackageVersionSummary("1.5.0", false, null, DateTimeOffset.UtcNow.AddDays(-1)),
                ],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Profile: null)),
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem("younics.ai.agent", "1.5.0")],
                [],
                [],
                [],
                []),
        };
        var runtimeClient = new FakeRuntimeApiClient([]);
        var operationExecutor = new FakePackageOperationExecutor();
        using var viewModel = CreateViewModel(
            runtimeClient,
            _ => registryClient,
            TimeSpan.FromMilliseconds(40),
            operationExecutor: operationExecutor);
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        viewModel.Marketplace.Versions.Single(version => version.Version == "1.5.0").SelectCommand.Execute(null);
        await viewModel.InstallSelectedMarketplacePackageCommand.ExecuteAsync(null);

        Assert.Equal("younics.ai.agent", operationExecutor.MarketplaceInstallPackageId);
        Assert.Equal("1.5.0", operationExecutor.MarketplaceInstallVersion);
    }

    [Fact]
    public async Task SearchText_WhenSwitchingModes_RestoresModeSpecificSearch()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.tools")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient(
            [
                CreateInstalledPackage("younics.ai.agent", isEnabled: true),
                CreateInstalledPackage("sunder.package.tools", isEnabled: true),
            ]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.RegistryUrlText = "https://registry.example/";
        await viewModel.InitializeAsync();
        viewModel.SearchText = "agent";
        Assert.Collection(viewModel.Installed.Packages, package => Assert.Equal("younics.ai.agent", package.PackageId));

        await viewModel.ShowMarketplaceCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.SearchText);
        viewModel.SearchText = "tools";
        await WaitForConditionAsync(() => registryClient.SearchQueries.Count > 0);

        await viewModel.ShowInstalledCommand.ExecuteAsync(null);

        Assert.Equal("agent", viewModel.SearchText);
        Assert.Collection(viewModel.Installed.Packages, package => Assert.Equal("younics.ai.agent", package.PackageId));
    }

    [Fact]
    public async Task PackageOperationCompletion_RebuildsInstalledPackagesWhileMarketplaceTabIsActive()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("younics.ai.agent")],
        };
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("sunder.package.tools", isEnabled: true)]);
        using var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            new FakePackageOperationExecutor(),
            registryClientFactory: _ => registryClient,
            marketplaceSearchThrottleDelay: TimeSpan.FromMilliseconds(40))
        {
            RegistryUrlText = "https://registry.example/",
        };

        await viewModel.InitializeAsync();
        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.DoesNotContain(viewModel.Installed.Packages, package => package.PackageId == "younics.ai.agent");
        runtimeClient.AddInstalledPackage(CreateInstalledPackage("younics.ai.agent", isEnabled: true));

        await InvokeRefreshAfterPackageOperationAsync(
            viewModel,
            new PackageOperationMetadata("younics.ai.agent", PackageOperationKind.InstallMarketplace, "AI Agent"));

        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.Contains(viewModel.Installed.Packages, package => package.PackageId == "younics.ai.agent");
    }

    private static async Task InvokeRefreshAfterPackageOperationAsync(
        PackagesWindowViewModel viewModel,
        PackageOperationMetadata metadata)
    {
        var method = typeof(PackagesWindowViewModel).GetMethod(
            "RefreshAfterPackageOperationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var snapshot = new BackgroundProcessSnapshot(
            Guid.NewGuid(),
            "Install AI Agent",
            PackageOperationService.PackageStoreGroupKey,
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            BackgroundProcessState.Completed,
            "Completed",
            100,
            true,
            metadata.ToMetadata(),
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await (Task)method.Invoke(viewModel, [snapshot])!;
    }

    private static PackagesWindowViewModel CreateViewModel(
        FakeRuntimeApiClient runtimeClient,
        Func<Uri, IRegistryPackageBrowseClient>? registryClientFactory = null,
        TimeSpan? marketplaceSearchThrottleDelay = null,
        Func<Uri, object?>? tokenProvider = null,
        TimeSpan? marketplaceDetailSpinnerDelay = null,
        FakePackageOperationExecutor? operationExecutor = null,
        Func<RuntimeExecutionRuntimeImpact, CancellationToken, Task<bool>>? confirmSharedRuntimeRepair = null
    )
    {
        var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            operationExecutor ?? new FakePackageOperationExecutor(),
            registryClientFactory: registryClientFactory,
            marketplaceSearchThrottleDelay: marketplaceSearchThrottleDelay,
            marketplaceDetailSpinnerDelay: marketplaceDetailSpinnerDelay,
            confirmSharedRuntimeRepair: confirmSharedRuntimeRepair
        )
        {
            Mode = PackageWindowMode.Installed,
            RegistryUrlText = string.Empty,
        };

        return viewModel;
    }

    private static NotificationCenterService CreateNotificationCenter() =>
        new(Path.Combine(CreateTempDirectory(), "notifications.json"));

    private static InstalledPackageDescriptor CreateInstalledPackage(
        string packageId,
        bool isEnabled,
        PackageIconDescriptor? icon = null,
        IReadOnlyList<PackageRpcContractUseDescriptor>? rpcContractUses = null,
        bool isRegistryManaged = false,
        IReadOnlyList<PackageTargetExecutionRuntimeBinding>? executionRuntimeBindings = null
    ) =>
        new(
            packageId,
            ToDisplayName(packageId),
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Summary: null,
            Icon: icon,
            isEnabled,
            DependsOn: [],
            DateTimeOffset.UtcNow,
            StatusMessage: isEnabled ? null : "Disabled",
            RpcContractUses: rpcContractUses,
            Provenance: isRegistryManaged
                ? new InstalledPackageProvenance(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    "https://registry.example/",
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64))
                : null
            ,
            ExecutionRuntimeBindings: executionRuntimeBindings
        );

    private static PackageTargetExecutionRuntimeBinding CreateExecutionRuntimeBinding(
        string runtimeId,
        string version,
        string rid)
    {
        var now = DateTimeOffset.UtcNow;
        var moduleAbi = runtimeId == "node" ? "node-worker-v1" : "dotnet-worker-v1";
        return new PackageTargetExecutionRuntimeBinding(
            SunderPackageFormat.RuntimeHostRole,
            rid,
            new RuntimeExecutionRuntimeRequirement(runtimeId, $"[{version},{version}]", moduleAbi),
            new RuntimeExecutionRuntimeIdentity(
                runtimeId == "node" ? ConfiguredOfficialSourceId.Node : ConfiguredOfficialSourceId.DotNet, runtimeId, version, rid, moduleAbi,
                runtimeId == "node" ? RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 : RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
                new('a', runtimeId == "node" ? 64 : 128)),
            TestSupport.RuntimeManagementTestClient.InstalledArtifact(),
            now);
    }

    private static InstalledExecutionRuntimeSummary CreateExecutionRuntimeSummary(
        PackageTargetExecutionRuntimeBinding binding,
        bool healthy)
    {
        var now = DateTimeOffset.UtcNow;
        return new InstalledExecutionRuntimeSummary(
            binding.Runtime,
            healthy ? RuntimeExecutionRuntimeStatuses.Ready : RuntimeExecutionRuntimeStatuses.Unhealthy,
            binding.InstalledArtifact.ArchiveSize,
            1,
            binding.InstalledArtifact,
            new RuntimeExecutionRuntimeHealth(
                healthy ? RuntimeExecutionRuntimeStatuses.Ready : RuntimeExecutionRuntimeStatuses.Unhealthy,
                now,
                RepairRecommended: !healthy,
                healthy ? null : "Integrity verification failed."),
            TestSupport.RuntimeManagementTestClient.Support(),
            now);
    }

    private static SessionPackageDescriptor CreateSessionPackage(
        string packageId,
        PackageReadinessState readiness)
        => new(
            packageId,
            ToDisplayName(packageId),
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Icon: null,
            IsEnabled: true,
            readiness,
            Views: [],
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0,
            RpcContractUses: []);

    private static RegistryPackageSummary CreateRegistryPackage(string packageId, string latestVersion = "1.0.0") =>
        new(
            packageId,
            ToDisplayName(packageId),
            null,
            latestVersion,
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

    private static RegistryPackageDetails CreateRegistryPackageDetails(
        string packageId,
        string version,
        RegistryPackageProfile? profile = null) =>
        new(
            packageId,
            ToDisplayName(packageId),
            null,
            version,
            null,
            [new RegistryPackageVersionSummary(version, false, null, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            profile
        );

    private static RegistryPackageVersionDetails CreateRegistryPackageVersionDetails(
        string packageId,
        string version,
        IReadOnlyList<RegistryPackageContractUse>? usesContracts = null,
        IReadOnlyList<RegistryPackageTarget>? targets = null)
        => new(
            PackageId: packageId,
            Name: ToDisplayName(packageId),
            Summary: null,
            Version: version,
            Icon: null,
            IsYanked: false,
            DeprecatedMessage: null,
            DependsOn: [],
            CanonicalArtifact: new RegistryPackageCanonicalArtifact("", 8L * 1024L * 1024L, ""),
            Targets: targets ?? [],
            Projections: [],
            ContractBundles: [],
            UsesContracts: usesContracts ?? [],
            Providers: [],
            ManifestFormatVersion: SunderPackageFormat.CurrentManifestVersion,
            ArchiveFormatVersion: SunderPackageFormat.CurrentArchiveFormatVersion,
            PublishedAtUtc: DateTimeOffset.UtcNow,
            TrustedArtifactOrigins: []);

    private static RuntimeRegistryResolveInstallPlanResponse CreateRuntimePlan(
        RegistryExecutionRuntimeRequirement requirement,
        RuntimeExecutionRuntimeVendorArtifact runtimeArtifact)
    {
        var runtimeRequirement = new RuntimeRegistryExecutionRuntimeRequirement(
            requirement.RuntimeId,
            requirement.VersionRange,
            requirement.ModuleAbi);
        return new RuntimeRegistryResolveInstallPlanResponse(
            true,
            [new RuntimeRegistryPackageInstallPlanItem(
                "younics.ai.agent",
                null,
                "1.0.0",
                new string('a', 64),
                RegistryPackageActionKinds.PackageInstall,
                null,
                [],
                [new RuntimeRegistryPackageTarget(
                    SunderPackageFormat.RuntimeHostRole,
                    runtimeArtifact.Identity.Rid,
                    SunderPackageFormat.WorkerTargetKind,
                    "lib/Agent.dll",
                    "net10.0",
                    "1.1.0",
                    ["execution-runtime.v1"],
                    ExecutionRuntime: runtimeRequirement)],
                [new RuntimeRegistryPackageProjectionArtifact(
                    "runtime",
                    runtimeArtifact.Identity.Rid,
                    new string('b', 64),
                    8L * 1024L * 1024L,
                    "/package",
                    new string('c', 64),
                    new string('d', 64),
                    new string('e', 64),
                    1)])],
            [],
            [],
            [],
            [])
        { RuntimePrerequisites = new(true, true, null, "Ready", new('a', 64), runtimeArtifact.Identity.Rid,
            [new(new("younics.ai.agent", "1.0.0", new('a', 64), "runtime", runtimeArtifact.Identity.Rid), new('a', 64),
                new(requirement.RuntimeId, requirement.VersionRange, requirement.ModuleAbi), true, null, runtimeArtifact.Identity, null)], []) };
    }

    private static RuntimeExecutionRuntimeVendorArtifact CreateResolvedRuntimeArtifact(
        string rid,
        RuntimeExecutionRuntimeSupportState state)
        => new(TestSupport.RuntimeManagementTestClient.Identity with { Rid = rid },
            TestSupport.RuntimeManagementTestClient.Distribution() with { DownloadSize = 50L * 1024L * 1024L },
            TestSupport.RuntimeManagementTestClient.Support() with { State = state });

    private static InstalledExecutionRuntimeSummary CreateInstalledRuntime(
        RuntimeExecutionRuntimeVendorArtifact artifact)
        => TestSupport.RuntimeManagementTestClient.Installed() with { Identity = artifact.Identity, Support = artifact.Support };

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
            Artifacts: [CreateProjectionArtifact($"download/{packageId}/{version}")]);

    private static RegistryPackageUpdate CreateUpdate(string packageId, string currentVersion, string availableVersion)
        => new(
            packageId,
            currentVersion,
            availableVersion,
            DeprecatedMessage: null,
            Artifacts: [CreateProjectionArtifact($"download/{packageId}/{availableVersion}")]);

    private static RegistryPackageProjectionArtifact CreateProjectionArtifact(string downloadUrl)
        => new("shared", null, "", 0, downloadUrl, "", "", "", 1);

    private static string ToDisplayName(string packageId) =>
        string.Concat(packageId[..1].ToUpperInvariant(), packageId[1..]);

    private static bool IsCompletedEnableOperation(BackgroundProcessSnapshot snapshot)
        => snapshot.State == BackgroundProcessState.Completed
           && PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
           && metadata.Kind == PackageOperationKind.Enable
           && string.Equals(metadata.PackageId, "agent", StringComparison.OrdinalIgnoreCase);

    private sealed class FakePackageArchivePicker : IPackageArchivePicker
    {
        public Task<PackageArchiveSelection?> PickPackageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PackageArchiveSelection?>(null);
    }

    private sealed class FakePackageOperationExecutor : IPackageOperationExecutor
    {
        public event EventHandler<PackageOperationChangedEventArgs>? OperationChanged
        {
            add { }
            remove { }
        }

        public List<string> EnabledPackageIds { get; } = [];

        public string? MarketplaceInstallPackageId { get; private set; }

        public string? MarketplaceInstallVersion { get; private set; }

        public string? AdoptedPackageId { get; private set; }

        public Uri? AdoptionRegistryUrl { get; private set; }

        public BackgroundProcessSnapshot? GetActiveOperationForPackage(string packageId) => null;

        public BackgroundProcessSnapshot? GetActivePackageStoreOperation() => null;

        public bool CancelActiveOperationForPackage(string packageId) => false;

        public Task<BackgroundProcessSnapshot?> EnqueueMarketplaceInstallAsync(
            string packageId,
            string displayName,
            Uri registryUrl,
            string? version = null,
            string? tag = "latest", CancellationToken cancellationToken = default)
        {
            MarketplaceInstallPackageId = packageId;
            MarketplaceInstallVersion = version;
            return Task.FromResult<BackgroundProcessSnapshot?>(CreateSnapshot(packageId, PackageOperationKind.InstallMarketplace, displayName));
        }

        public Task<BackgroundProcessSnapshot?> EnqueueMarketplaceUpdateAsync(
            string packageId,
            string displayName,
            string version, CancellationToken cancellationToken = default)
            => Task.FromResult<BackgroundProcessSnapshot?>(CreateSnapshot(packageId, PackageOperationKind.UpdateMarketplace, displayName));

        public Task<BackgroundProcessSnapshot?> EnqueueRegistrySourceAdoptionAsync(
            string packageId,
            string displayName,
            Uri registryUrl, CancellationToken cancellationToken = default)
        {
            AdoptedPackageId = packageId;
            AdoptionRegistryUrl = registryUrl;
            return Task.FromResult<BackgroundProcessSnapshot?>(CreateSnapshot(packageId, PackageOperationKind.AdoptRegistrySource, displayName));
        }

        public Task<BackgroundProcessSnapshot?> EnqueueUpdateAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<BackgroundProcessSnapshot?>(CreateSnapshot(null, PackageOperationKind.UpdateAll, "All packages"));

        public Task<BackgroundProcessSnapshot?> EnqueueLocalInstallAsync(PackageArchiveSelection selection, CancellationToken cancellationToken = default)
            => Task.FromResult<BackgroundProcessSnapshot?>(CreateSnapshot(null, PackageOperationKind.InstallLocal, Path.GetFileName(selection.PackagePath)));

        public BackgroundProcessSnapshot EnqueueEnable(string packageId, string displayName)
        {
            EnabledPackageIds.Add(packageId);
            return CreateSnapshot(packageId, PackageOperationKind.Enable, displayName);
        }

        public BackgroundProcessSnapshot EnqueueDisable(string packageId, string displayName)
            => CreateSnapshot(packageId, PackageOperationKind.Disable, displayName);

        public BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName, bool allowCascade = false)
            => CreateSnapshot(packageId, PackageOperationKind.Uninstall, displayName);

        private static BackgroundProcessSnapshot CreateSnapshot(
            string? packageId,
            PackageOperationKind kind,
            string displayName)
            => new(
                Guid.NewGuid(),
                displayName,
                PackageOperationService.PackageStoreGroupKey,
                BackgroundProcessIndicator.Packages,
                BackgroundProcessConcurrencyMode.SequentialWithinGroup,
                BackgroundProcessState.Queued,
                "Queued",
                null,
                true,
                new PackageOperationMetadata(packageId, kind, displayName).ToMetadata(),
                null,
                DateTimeOffset.UtcNow,
                null,
                null);
    }

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => (TClient)(object)runtimeApiClient;
    }

    private sealed class FakeRegistryApiClient : IRegistryPackageBrowseClient
    {
        private readonly object _gate = new();
        private readonly List<string?> _searchQueries = [];
        private readonly List<RegistrySearchSort> _searchSorts = [];

        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public IReadOnlyList<string?> SearchQueries
        {
            get
            {
                lock (_gate)
                {
                    return _searchQueries.ToArray();
                }
            }
        }

        public IReadOnlyList<RegistrySearchSort> SearchSorts
        {
            get
            {
                lock (_gate)
                {
                    return _searchSorts.ToArray();
                }
            }
        }

        public Func<string?, IReadOnlyList<RegistryPackageSummary>> SearchResults { get; init; } = _ => [];

        public Func<string, CancellationToken, Task<RegistryPackageDetails?>> PackageDetails { get; init; } =
            (packageId, _) => Task.FromResult<RegistryPackageDetails?>(CreateRegistryPackageDetails(packageId, "1.0.0"));

        public Func<string, string, CancellationToken, Task<RegistryPackageVersionDetails?>> VersionDetails { get; init; } =
            (packageId, version, _) => Task.FromResult<RegistryPackageVersionDetails?>(
                CreateRegistryPackageVersionDetails(packageId, version));

        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], [], []);

        public IReadOnlyList<RegistryPackageUpdate> Updates { get; init; } = [];

        public RegistryPackageStarResponse StarPackageResponse { get; init; } = new(true, "Starred package.", new RegistryPackageStats(0, 0, 0, [], Stars: 1, IsStarred: true), []);

        public RegistryPackageStarResponse UnstarPackageResponse { get; init; } = new(true, "Unstarred package.", new RegistryPackageStats(0, 0, 0, [], Stars: 0, IsStarred: false), []);

        public RegistryResolveInstallPlanRequest? LastInstallPlanRequest { get; private set; }

        public string? LastStarPackageId { get; private set; }

        public string? LastStarPackageToken { get; private set; }

        public string? LastUnstarPackageId { get; private set; }

        public string? LastUnstarPackageToken { get; private set; }

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(
            string? query,
            int skip,
            int take,
            RegistrySearchSort sort = RegistrySearchSort.Downloads,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _searchQueries.Add(query);
                _searchSorts.Add(sort);
            }

            var results = SearchResults(query)
                .Skip(skip)
                .Take(take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<RegistryPackageSummary>>(results);
        }

        public Task<RegistryPackageDetails?> GetPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PackageDetails(packageId, cancellationToken);
        }

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken = default
        ) => VersionDetails(packageId, version, cancellationToken);

        public Task<RegistryPackageStarResponse> StarPackageAsync(
            string packageId,
            string bearerToken,
            CancellationToken cancellationToken = default)
        {
            LastStarPackageId = packageId;
            LastStarPackageToken = bearerToken;
            return Task.FromResult(StarPackageResponse);
        }

        public Task<RegistryPackageStarResponse> UnstarPackageAsync(
            string packageId,
            string bearerToken,
            CancellationToken cancellationToken = default)
        {
            LastUnstarPackageId = packageId;
            LastUnstarPackageToken = bearerToken;
            return Task.FromResult(UnstarPackageResponse);
        }

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
            RegistryResolveUpdatesRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RegistryResolveUpdatesResponse(Updates, [], []));
        }

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
            RegistryResolveInstallPlanRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInstallPlanRequest = request;
            return Task.FromResult(InstallPlan);
        }

        public void Dispose() { }
    }

    private sealed class FakeRuntimeApiClient(
        IReadOnlyList<InstalledPackageDescriptor> installedPackages
    ) : IRuntimePackagesClient, TestSupport.IReadyRuntimePrerequisiteTestClient, TestSupport.IUnsupportedPackageUploadRetentionTestClient
    {
        private readonly List<InstalledPackageDescriptor> _installedPackages =
            installedPackages.ToList();
        private readonly Dictionary<string, PackageStoreStageRequest> _pendingStages = new(StringComparer.OrdinalIgnoreCase);

        public int GetInstalledPackagesCallCount { get; private set; }

        public List<string> EnabledPackageIds { get; } = [];

        public PackageOperationResult? EnableResult { get; init; }

        public string? RegistryInstallPackageId { get; init; }

        public RuntimeRegistryPackageRequest? LastRegistryPackageRequest { get; private set; }

        public RuntimeRegistryStarRequest? LastRegistryStarRequest { get; private set; }

        public IReadOnlyList<RegistryPackageUpdate> RegistryUpdates { get; init; } = [];

        public IReadOnlyList<SessionPackageDescriptor> SessionPackages { get; init; } = [];

        public IReadOnlyList<InstalledExecutionRuntimeSummary> ExecutionRuntimes { get; set; } = [];

        public RuntimeExecutionRuntimeImpact RuntimeImpact { get; set; } = new(
            1,
            ["agent"],
            new string('f', 64));

        public RuntimeExecutionRuntimeImpact? LastRepairImpact { get; private set; }

        public RuntimeRegistryResolveInstallPlanResponse? RegistryPlanResult { get; init; }

        public Func<RuntimeExecutionRuntimeIdentity, CancellationToken, Task<RuntimeExecutionRuntimeOperationResult>>? RepairRuntime { get; set; }

        public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(RegistryPlanResult ?? new RuntimeRegistryResolveInstallPlanResponse(
                true,
                RegistryUpdates.Select(update => new RuntimeRegistryPackageInstallPlanItem(
                    update.PackageId,
                    update.CurrentVersion,
                    update.AvailableVersion,
                    new string('a', 64),
                    RegistryPackageActionKinds.PackageUpdate,
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
                [],
                [],
                []));

        public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
        {
            LastRegistryPackageRequest = request;
            return Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Installed package.", true, false, [], [], [request.PackageId], []));
        }

        public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        {
            LastRegistryStarRequest = request;
            return Task.FromResult(new RegistryPackageStarResponse(true, "Starred package.", new RegistryPackageStats(4, 0, 0, [], 2, true), []));
        }

        public void AddInstalledPackage(InstalledPackageDescriptor package)
        {
            _installedPackages.RemoveAll(existing =>
                string.Equals(existing.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase));
            _installedPackages.Add(package);
        }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>([]);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(SessionPackages);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(
            string appRid,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>([]);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetStagedPackageUiSnapshotsAsync(
            string stageId,
            string appRid,
            CancellationToken cancellationToken = default)
        {
            var request = _pendingStages[stageId];
            return Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>(
                request.Mutations
                    .Select(GetMutationPackageId)
                    .Select(packageId => RuntimeContractTestData.Snapshot(packageId, PackageSourceKind.Installed, packageId))
                    .ToArray());
        }

        public Task DownloadPackageUiSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(
            CancellationToken cancellationToken = default
        )
        {
            GetInstalledPackagesCallCount++;
            return Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>(
                _installedPackages.ToArray()
            );
        }

        public Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> GetExecutionRuntimesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(ExecutionRuntimes);

        public Task<RuntimeExecutionRuntimeImpact> GetExecutionRuntimeImpactAsync(
            RuntimeExecutionRuntimeIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(RuntimeImpact);

        public Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(
            RuntimeExecutionRuntimeIdentity identity,
            RuntimeExecutionRuntimeImpact impact,
            CancellationToken cancellationToken = default)
        {
            LastRepairImpact = impact;
            return RepairRuntime?.Invoke(identity, cancellationToken)
                   ?? Task.FromException<RuntimeExecutionRuntimeOperationResult>(new NotSupportedException());
        }

        public Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(
            string packageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateUninstallPlan(packageId));

        public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
            new(
                $"file:///packages/{Uri.EscapeDataString(packageId)}/assets/{assetPath.Replace('\\', '/')}"
            );

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

        public Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("package-upload", "test-hash", 0, Path.GetFileName(packagePath), "application/vnd.sunder.package"));

        public Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("stack-upload", "test-hash", 0, Path.GetFileName(stackPath), "application/vnd.sunder.stack"));

        public Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("media-upload", "test-hash", 0, Path.GetFileName(mediaPath), contentType));

        public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
            PackageStoreStageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnableResult is not null && request.Mutations.Any(mutation => mutation.Kind == PackageStoreMutationKind.Enable))
            {
                return Task.FromResult(new PackageStoreStageResult(null, EnableResult, []));
            }

            var stageId = Guid.NewGuid().ToString("N");
            _pendingStages[stageId] = request;
            var impactedPackageIds = request.Mutations.Select(GetMutationPackageId).ToArray();
            return Task.FromResult(new PackageStoreStageResult(
                stageId,
                new PackageOperationResult(true, "staged", RuntimeSessionApplied: false, RequiresAppRestart: false, [], [])
                {
                    ImpactedPackageIds = impactedPackageIds,
                },
                impactedPackageIds.Select(packageId => new ActivePackageDescriptor(packageId, ToDisplayName(packageId), "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, [])).ToArray()));
        }

        public Task<PackageOperationResult> CommitPackageStoreStageAsync(
            string stageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = _pendingStages[stageId];
            _pendingStages.Remove(stageId);
            var impactedPackageIds = request.Mutations.Select(GetMutationPackageId).ToArray();
            var message = "Package store updated.";
            foreach (var mutation in request.Mutations)
            {
                var packageId = GetMutationPackageId(mutation);
                switch (mutation.Kind)
                {
                    case PackageStoreMutationKind.Install:
                        if (!_installedPackages.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
                        {
                            _installedPackages.Add(CreateInstalledPackage(packageId, isEnabled: true));
                        }

                        message = $"Installed package '{ToDisplayName(packageId)}'.";
                        break;
                    case PackageStoreMutationKind.Enable:
                        EnabledPackageIds.Add(packageId);
                        SetPackageEnabled(packageId, isEnabled: true);
                        message = $"Enabled package '{ToDisplayName(packageId)}'.";
                        break;
                }
            }

            return Task.FromResult(new PackageOperationResult(true, message, RuntimeSessionApplied: true, RequiresAppRestart: false, [], [])
            {
                ImpactedPackageIds = impactedPackageIds,
                CommittedStamp = new RuntimePackageStamp(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1),
            });
        }

        public Task<RuntimePackageStageStatus> GetPackageStoreStageStatusAsync(
            string stageId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimePackageStageStatus(
                stageId,
                RuntimePackageStageKind.PackageStore,
                _pendingStages.ContainsKey(stageId)
                    ? RuntimePackageStageState.Pending
                    : RuntimePackageStageState.Committed,
                DateTimeOffset.UtcNow,
                _pendingStages.ContainsKey(stageId)
                    ? null
                    : new RuntimePackageStamp(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1),
                RuntimeSessionApplied: true,
                ReconciliationPending: false,
                null));

        public Task DiscardPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            _pendingStages.Remove(stageId);
            return Task.CompletedTask;
        }

        public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Applied package plan.", true, false, [], [], request.Packages.Select(package => package.PackageId).ToArray(), []));

        public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Updated packages.", true, false, [], [], [], []));

        public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Adopted package source.", true, false, [], [], [request.PackageId], []));

        public void Dispose() { }

        private void SetPackageEnabled(string packageId, bool isEnabled)
        {
            var index = _installedPackages.FindIndex(package =>
                string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            );
            if (index >= 0)
            {
                _installedPackages[index] = _installedPackages[index] with
                {
                    IsEnabled = isEnabled,
                    StatusMessage = isEnabled ? null : "Disabled",
                };
            }
        }

        private string GetMutationPackageId(PackageStoreMutationRequest mutation)
            => !string.IsNullOrWhiteSpace(mutation.PackageId)
                ? mutation.PackageId
                : RegistryInstallPackageId ?? Path.GetFileNameWithoutExtension(mutation.UploadId ?? string.Empty);
    }
}
