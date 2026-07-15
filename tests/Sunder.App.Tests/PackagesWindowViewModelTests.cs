using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
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
            Updates = [CreateUpdate("sunder.package.agent", "1.0.0", "1.1.0")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([CreateInstalledPackage("sunder.package.agent", isEnabled: true)])
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
    public async Task RefreshInstalledPackages_PreservesUnchangedRowsAndReplacesChangedRows()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [
                CreateInstalledPackage("agent", isEnabled: true),
                CreateInstalledPackage("tools", isEnabled: true),
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
    public void MarketplacePackages_UseRegistryIconUrlWhenAvailable()
    {
        var iconUri = new Uri(
            "http://127.0.0.1:1/api/v1/packages/sunder.package.agent/versions/1.0.0/icon"
        );
        using var package = new RegistryPackageSearchItemViewModel(
            new RegistryPackageSummary(
                "sunder.package.agent",
                "Sunder Agent",
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
        Assert.Equal("S", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
    }

    [Fact]
    public async Task SearchText_WhenMarketplaceMode_SearchesRegistryAfterThrottle()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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

        await Task.Delay(10);
        Assert.Empty(registryClient.SearchQueries);

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("agent", query));
        Assert.Collection(
            viewModel.Marketplace.Packages,
            package => Assert.Equal("sunder.package.agent", package.PackageId)
        );
        Assert.True(viewModel.HasSearchText);
    }

    [Fact]
    public async Task MarketplaceSortOption_WhenChanged_SearchesWithSelectedSort()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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
            SearchResults = query => string.Equals(query, "sunder.package.agent", StringComparison.OrdinalIgnoreCase)
                ? [CreateRegistryPackage("sunder.package.agent.tools"), CreateRegistryPackage("sunder.package.agent")]
                : [],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40),
            marketplaceDetailSpinnerDelay: TimeSpan.FromMilliseconds(40));

        await viewModel.ApplyLaunchRequestAsync(new AppLaunchRequest(
            AppLaunchRequestKind.PackageDetails,
            PackageId: "sunder.package.agent",
            RegistryUrl: new Uri("https://registry.example/")));

        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.Equal("sunder.package.agent", viewModel.SearchText);
        Assert.Equal("sunder.package.agent", viewModel.SelectedMarketplacePackage?.PackageId);
        Assert.Collection(viewModel.Marketplace.Packages, package => Assert.Equal("sunder.package.agent", package.PackageId));
        Assert.True(viewModel.ShowMarketplaceInstallButton);
        Assert.Contains("Loaded sunder.package.agent", viewModel.Operations.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("sunder.package.agent", query));
    }

    [Fact]
    public async Task SearchText_WhenChangedBeforeThrottleElapsed_SearchesOnlyLatestText()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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
                    : [CreateRegistryPackage("sunder.package.agent")],
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
                CreateRegistryPackage("sunder.package.agent", "9.0.0"),
                CreateRegistryPackage("sunder.package.tools", "2.0.0"),
            ],
            PackageDetails = async (packageId, cancellationToken) =>
            {
                if (string.Equals(packageId, "sunder.package.agent", StringComparison.OrdinalIgnoreCase))
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
                CreateRegistryPackage("sunder.package.agent", "9.0.0"),
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
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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
    public async Task MarketplaceSelection_WhenDetailsContainProfile_PopulatesProfileSections()
    {
        var profile = new RegistryPackageProfile(
            "sunder.package.agent",
            "Detailed local agent package.",
            "# Sunder Agent",
            "https://example.test/agent",
            "https://example.test/source",
            "https://example.test/issues",
            " MIT ",
            ["agent", " local ", "agent", ""],
            [],
            DateTimeOffset.UtcNow);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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
        Assert.Equal("sunder.package.agent", runtimeClient.LastRegistryStarRequest?.ResourceId);
    }

    [Fact]
    public async Task InstallSelectedMarketplacePackageCommand_InstallsSelectedVersion()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent", "2.0.0")],
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
                [CreatePlanItem("sunder.package.agent", "1.5.0")],
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

        Assert.Equal("sunder.package.agent", operationExecutor.MarketplaceInstallPackageId);
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
                CreateInstalledPackage("sunder.package.agent", isEnabled: true),
                CreateInstalledPackage("sunder.package.tools", isEnabled: true),
            ]),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.RegistryUrlText = "https://registry.example/";
        await viewModel.InitializeAsync();
        viewModel.SearchText = "agent";
        Assert.Collection(viewModel.Installed.Packages, package => Assert.Equal("sunder.package.agent", package.PackageId));

        await viewModel.ShowMarketplaceCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.SearchText);
        viewModel.SearchText = "tools";
        await WaitForConditionAsync(() => registryClient.SearchQueries.Count > 0);

        await viewModel.ShowInstalledCommand.ExecuteAsync(null);

        Assert.Equal("agent", viewModel.SearchText);
        Assert.Collection(viewModel.Installed.Packages, package => Assert.Equal("sunder.package.agent", package.PackageId));
    }

    [Fact]
    public async Task PackageOperationCompletion_RebuildsInstalledPackagesWhileMarketplaceTabIsActive()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
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
        Assert.DoesNotContain(viewModel.Installed.Packages, package => package.PackageId == "sunder.package.agent");
        runtimeClient.AddInstalledPackage(CreateInstalledPackage("sunder.package.agent", isEnabled: true));

        await InvokeRefreshAfterPackageOperationAsync(
            viewModel,
            new PackageOperationMetadata("sunder.package.agent", PackageOperationKind.InstallMarketplace, "Sunder Agent"));

        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.Contains(viewModel.Installed.Packages, package => package.PackageId == "sunder.package.agent");
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
            "Install Sunder Agent",
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
        FakePackageOperationExecutor? operationExecutor = null
    )
    {
        var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            operationExecutor ?? new FakePackageOperationExecutor(),
            registryClientFactory: registryClientFactory,
            marketplaceSearchThrottleDelay: marketplaceSearchThrottleDelay,
            marketplaceDetailSpinnerDelay: marketplaceDetailSpinnerDelay
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
        PackageIconDescriptor? icon = null
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
            StatusMessage: isEnabled ? null : "Disabled"
        );

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

    private static RegistryPackageInstallPlanItem CreatePlanItem(string packageId, string version)
        => new(
            packageId,
            CurrentVersion: null,
            version,
            IsUpdate: false,
            DeprecatedMessage: null,
            DependsOn: [],
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{version}"));

    private static RegistryPackageUpdate CreateUpdate(string packageId, string currentVersion, string availableVersion)
        => new(
            packageId,
            currentVersion,
            availableVersion,
            DeprecatedMessage: null,
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{availableVersion}"));

    private static string ToDisplayName(string packageId) =>
        string.Concat(packageId[..1].ToUpperInvariant(), packageId[1..]);

    private static bool IsCompletedEnableOperation(BackgroundProcessSnapshot snapshot)
        => snapshot.State == BackgroundProcessState.Completed
           && PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
           && metadata.Kind == PackageOperationKind.Enable
           && string.Equals(metadata.PackageId, "agent", StringComparison.OrdinalIgnoreCase);

    private sealed class FakePackageArchivePicker : IPackageArchivePicker
    {
        public Task<string?> PickPackagePathAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
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

        public BackgroundProcessSnapshot? GetActiveOperationForPackage(string packageId) => null;

        public BackgroundProcessSnapshot? GetActivePackageStoreOperation() => null;

        public bool CancelActiveOperationForPackage(string packageId) => false;

        public BackgroundProcessSnapshot EnqueueMarketplaceInstall(
            string packageId,
            string displayName,
            Uri registryUrl,
            string? version = null,
            string? tag = "latest")
        {
            MarketplaceInstallPackageId = packageId;
            MarketplaceInstallVersion = version;
            return CreateSnapshot(packageId, PackageOperationKind.InstallMarketplace, displayName);
        }

        public BackgroundProcessSnapshot EnqueueMarketplaceUpdate(
            string packageId,
            string displayName,
            string version,
            Uri registryUrl)
            => CreateSnapshot(packageId, PackageOperationKind.UpdateMarketplace, displayName);

        public BackgroundProcessSnapshot EnqueueUpdateAll(Uri registryUrl)
            => CreateSnapshot(null, PackageOperationKind.UpdateAll, "All packages");

        public BackgroundProcessSnapshot EnqueueLocalInstall(string packagePath)
            => CreateSnapshot(null, PackageOperationKind.InstallLocal, Path.GetFileName(packagePath));

        public BackgroundProcessSnapshot EnqueueEnable(string packageId, string displayName)
        {
            EnabledPackageIds.Add(packageId);
            return CreateSnapshot(packageId, PackageOperationKind.Enable, displayName);
        }

        public BackgroundProcessSnapshot EnqueueDisable(string packageId, string displayName)
            => CreateSnapshot(packageId, PackageOperationKind.Disable, displayName);

        public BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName)
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

        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], []);

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
        ) => Task.FromResult<RegistryPackageVersionDetails?>(null);

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
            return Task.FromResult(new RegistryResolveUpdatesResponse(Updates));
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

        public Task DownloadArtifactAsync(
            RegistryPackageArtifact artifact,
            string packageId,
            string version,
            string destinationPath,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeRuntimeApiClient(
        IReadOnlyList<InstalledPackageDescriptor> installedPackages
    ) : IRuntimePackagesClient
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

        public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryResolveInstallPlanResponse(
                true,
                RegistryUpdates.Select(update => new RuntimeRegistryPackageInstallPlanItem(
                    update.PackageId,
                    update.CurrentVersion,
                    update.AvailableVersion,
                    true,
                    update.DeprecatedMessage,
                    [],
                    new RuntimeRegistryPackageArtifact(update.Artifact.Sha256, update.Artifact.Size, update.Artifact.DownloadUrl))).ToArray(),
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
        ) => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>([]);

        public Task<PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<PackageSessionStatus?>(null);

        public Task<PackageSessionOperationResult> LoadPackageSessionAsync(PackageSessionLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSessionOperationResult.Failed("Not configured for this test."));

        public Task<PackageSessionOperationResult> UnloadPackageSessionAsync(string packageId, PackageSourceKind sourceKind, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSessionOperationResult.Failed("Not configured for this test."));

        public Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageOperationResult(true, null, true, false, [], []));

        public Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(PackageLifecycleStageRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageLifecycleStageResult.Failed("Not configured for this test."));

        public Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageLifecycleOperationResult.Failed("Not configured for this test."));

        public Task DiscardPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

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

        public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
            new(
                $"file:///packages/{Uri.EscapeDataString(packageId)}/assets/{assetPath.Replace('\\', '/')}"
            );

        public Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("package-upload", "test-hash", 0, Path.GetFileName(packagePath), "application/vnd.sunder.package"));

        public Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("stack-upload", "test-hash", 0, Path.GetFileName(stackPath), "application/vnd.sunder.stack"));

        public Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("media-upload", "test-hash", 0, Path.GetFileName(mediaPath), contentType));

        public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<PackageOperationResult> InstallPackageFromPathAsync(
            string packagePath,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageId = RegistryInstallPackageId ?? Path.GetFileNameWithoutExtension(packagePath);
            if (!_installedPackages.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                _installedPackages.Add(CreateInstalledPackage(packageId, isEnabled: true));
            }

            return Task.FromResult(
                new PackageOperationResult(
                    true,
                    $"Installed package '{ToDisplayName(packageId)}'.",
                    true,
                    false,
                    [],
                    [])
                {
                    ImpactedPackageIds = [packageId],
                });
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(
            string packageId,
            string packagePath,
            bool allowDowngrade = false,
            bool reinstall = false,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new PackageOperationResult(true, $"Upgraded {packageId}.", true, false, [], []));

        public Task<PackageOperationResult> EnableInstalledPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        )
        {
            if (EnableResult is not null)
            {
                return Task.FromResult(EnableResult);
            }

            EnabledPackageIds.Add(packageId);
            SetPackageEnabled(packageId, isEnabled: true);
            return Task.FromResult(
                new PackageOperationResult(
                    true,
                    $"Enabled package '{ToDisplayName(packageId)}'.",
                    true,
                    false,
                    [],
                    []
                )
                {
                    ImpactedPackageIds = [packageId],
                }
            );
        }

        public Task<PackageOperationResult> DisableInstalledPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new PackageOperationResult(true, $"Disabled {packageId}.", true, false, [], []));

        public Task<PackageOperationResult> UninstallPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new PackageOperationResult(true, $"Uninstalled {packageId}.", true, false, [], []));

        public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
            PackageStoreStageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnableResult is not null && request.Mutations.Any(mutation => mutation.Kind == PackageStoreMutationKind.Enable))
            {
                return Task.FromResult(new PackageStoreStageResult(null, EnableResult, [], []));
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
                impactedPackageIds.Select(packageId => new ActivePackageDescriptor(packageId, ToDisplayName(packageId), "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, [])).ToArray(),
                impactedPackageIds.Select(packageId => RuntimeContractTestData.Snapshot(packageId, PackageSourceKind.Installed, packageId)).ToArray()));
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

        public Task ReportPackageFaultAsync(
            string packageId,
            PackageFailureOrigin origin,
            string message,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

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
