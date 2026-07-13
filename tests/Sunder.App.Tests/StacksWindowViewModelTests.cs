using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using Xunit;

namespace Sunder.App.Tests;

public sealed class StacksWindowViewModelTests
{
    [Fact]
    public async Task SearchRegistryStacksCommand_PopulatesRegistryStackResults()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults =
            [
                CreateRegistryStackSummary("team-stack", "Team Stack"),
            ],
        };
        using var viewModel = CreateViewModel(root, registryClient);
        viewModel.RegistrySearchText = "team";

        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);

        var result = Assert.Single(viewModel.RegistryStacks);
        Assert.Equal("team-stack", result.StackId);
        Assert.Equal("team", registryClient.LastStackSearchQuery);
        Assert.Equal(result, viewModel.SelectedRegistryStack);
    }

    [Fact]
    public async Task RegistrySearchText_WhenMarketplaceMode_SearchesRegistryAfterThrottle()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
        };
        using var viewModel = CreateViewModel(root, registryClient, registrySearchThrottleDelay: TimeSpan.FromMilliseconds(40));

        viewModel.RegistrySearchText = " team ";

        await Task.Delay(10);
        Assert.Empty(registryClient.StackSearchQueries);

        await WaitForConditionAsync(() => registryClient.StackSearchQueries.Count == 1);

        Assert.Collection(registryClient.StackSearchQueries, query => Assert.Equal("team", query));
        Assert.Equal("team-stack", Assert.Single(viewModel.RegistryStacks).StackId);
        Assert.True(viewModel.HasRegistrySearchText);
    }

    [Fact]
    public async Task RegistrySearchText_WhenChangedBeforeThrottleElapsed_SearchesOnlyLatestText()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
        };
        using var viewModel = CreateViewModel(root, registryClient, registrySearchThrottleDelay: TimeSpan.FromMilliseconds(50));

        viewModel.RegistrySearchText = "t";
        await Task.Delay(10);
        viewModel.RegistrySearchText = "team";

        await WaitForConditionAsync(() => registryClient.StackSearchQueries.Count == 1);

        Assert.Collection(registryClient.StackSearchQueries, query => Assert.Equal("team", query));
    }

    [Fact]
    public async Task RegistrySortOption_WhenChanged_SearchesWithSelectedSort()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
        };
        using var viewModel = CreateViewModel(
            root,
            registryClient,
            registryDetailSpinnerDelay: TimeSpan.FromMilliseconds(40));

        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        viewModel.SelectedRegistrySortOption = viewModel.RegistrySortOptions.Single(option => option.Sort == RegistrySearchSort.Stars);

        await WaitForConditionAsync(() => registryClient.StackSearchSorts.Count == 2);

        Assert.Collection(
            registryClient.StackSearchSorts,
            sort => Assert.Equal(RegistrySearchSort.Downloads, sort),
            sort => Assert.Equal(RegistrySearchSort.Stars, sort));
    }

    [Fact]
    public async Task SearchRegistryStacksCommand_LoadsSelectedRegistryStackDetails()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
            StackDetails = CreateRegistryStackDetails("team-stack", "Team Stack"),
        };
        using var viewModel = CreateViewModel(root, registryClient);

        viewModel.ShowMarketplaceCommand.Execute(null);
        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => viewModel.RegistrySelectedDetails.Count == 1);

        Assert.True(viewModel.IsMarketplaceMode);
        Assert.True(viewModel.ShowRegistrySelectedDetails);
        Assert.Equal("Team Stack", viewModel.SelectedRegistryStackTitle);
        Assert.Equal("sunder.package.agent", Assert.Single(viewModel.RegistrySelectedPackages).PackageId);
        var detailPackage = Assert.Single(viewModel.RegistrySelectedDetails);
        Assert.Equal("sunder.package.agent", detailPackage.PackageId);
        var group = Assert.Single(detailPackage.ItemGroups);
        Assert.Equal("Agent Profiles", group.DisplayName);
        var item = Assert.Single(group.Items);
        Assert.Equal("Agent profile", item.DisplayName);
        Assert.Equal("Custom instructions", Assert.Single(item.Values).Label);
        Assert.Equal("API key", Assert.Single(viewModel.RegistrySelectedRequiredInputs));
    }

    [Fact]
    public async Task RegistryStackSelection_WhenDetailsAreLoading_ClearsStaleDetailContent()
    {
        var root = CreateTempDirectory();
        var toolsDetailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToolsDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults =
            [
                CreateRegistryStackSummary("team-stack", "Team Stack"),
                CreateRegistryStackSummary("tools-stack", "Tools Stack"),
            ],
            StackDetailsFactory = async (stackId, cancellationToken) =>
            {
                if (string.Equals(stackId, "tools-stack", StringComparison.OrdinalIgnoreCase))
                {
                    toolsDetailsStarted.SetResult();
                    await releaseToolsDetails.Task.WaitAsync(cancellationToken);
                    return CreateRegistryStackDetails(stackId, "Tools Stack");
                }

                return CreateRegistryStackDetails(stackId, "Team Stack");
            },
        };
        using var viewModel = CreateViewModel(root, registryClient);

        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => viewModel.RegistryStackDetailsLoaded);
        Assert.Equal("Team Stack", viewModel.SelectedRegistryStackTitle);
        Assert.Single(viewModel.RegistrySelectedPackages);
        Assert.Single(viewModel.RegistrySelectedDetails);

        viewModel.SelectedRegistryStack = viewModel.RegistryStacks[1];
        await toolsDetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsRegistryStackDetailsLoading);
        Assert.False(viewModel.RegistryStackDetailsLoaded);
        Assert.False(viewModel.ShowRegistryStackDetailsLoading);
        Assert.False(viewModel.ShowRegistryStackDetailsContent);
        Assert.Equal(string.Empty, viewModel.SelectedRegistryStackSummary);
        Assert.Empty(viewModel.RegistrySelectedPackages);
        Assert.Empty(viewModel.RegistrySelectedDetails);
        Assert.False(viewModel.CanImportSelectedRegistryStack);
        Assert.False(viewModel.CanUseSelectedRegistryStack);

        await WaitForConditionAsync(() => viewModel.ShowRegistryStackDetailsLoading);

        releaseToolsDetails.SetResult();
        await WaitForConditionAsync(() => viewModel.RegistryStackDetailsLoaded && viewModel.SelectedRegistryStackTitle == "Tools Stack");

        Assert.False(viewModel.IsRegistryStackDetailsLoading);
        Assert.True(viewModel.ShowRegistryStackDetailsContent);
        Assert.Single(viewModel.RegistrySelectedPackages);
        Assert.Single(viewModel.RegistrySelectedDetails);
    }

    [Fact]
    public async Task RegistryStackSelection_WhenDetailsLoadBeforeDelay_DoesNotShowLoadingSpinner()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
            StackDetails = CreateRegistryStackDetails("team-stack", "Team Stack"),
        };
        using var viewModel = CreateViewModel(
            root,
            registryClient,
            registryDetailSpinnerDelay: TimeSpan.FromMilliseconds(40));

        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        await Task.Delay(80);

        Assert.True(viewModel.RegistryStackDetailsLoaded);
        Assert.False(viewModel.IsRegistryStackDetailsLoading);
        Assert.False(viewModel.ShowRegistryStackDetailsLoading);
    }

    [Fact]
    public async Task RegistryStackSelection_WhenEarlierDetailsCompleteLater_DoesNotOverwriteCurrentDetails()
    {
        var root = CreateTempDirectory();
        var teamDetailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teamDetailsCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeamDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults =
            [
                CreateRegistryStackSummary("team-stack", "Team Stack"),
                CreateRegistryStackSummary("tools-stack", "Tools Stack"),
            ],
            StackDetailsFactory = async (stackId, cancellationToken) =>
            {
                if (string.Equals(stackId, "team-stack", StringComparison.OrdinalIgnoreCase))
                {
                    teamDetailsStarted.SetResult();
                    try
                    {
                        await releaseTeamDetails.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        teamDetailsCancelled.SetResult();
                        throw;
                    }

                    return CreateRegistryStackDetails(stackId, "Team Stack");
                }

                return CreateRegistryStackDetails(stackId, "Tools Stack");
            },
        };
        using var viewModel = CreateViewModel(root, registryClient);

        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        await teamDetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedRegistryStack = viewModel.RegistryStacks[1];
        await teamDetailsCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => viewModel.RegistryStackDetailsLoaded && viewModel.SelectedRegistryStackTitle == "Tools Stack");

        Assert.Equal("Tools Stack", viewModel.SelectedRegistryStackTitle);
        Assert.Equal("tools-stack", viewModel.SelectedRegistryStackSubtitle);
        Assert.Single(viewModel.RegistrySelectedPackages);
        Assert.Single(viewModel.RegistrySelectedDetails);

        releaseTeamDetails.SetResult();
        await Task.Delay(50);

        Assert.Equal("Tools Stack", viewModel.SelectedRegistryStackTitle);
        Assert.Equal("tools-stack", viewModel.SelectedRegistryStackSubtitle);
    }

    [Fact]
    public async Task ApplyLaunchRequestAsync_WhenStackDetailsLink_SelectsExactMarketplaceStackWithoutImporting()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults =
            [
                CreateRegistryStackSummary("team-stack-tools", "Team Stack Tools"),
                CreateRegistryStackSummary("team-stack", "Team Stack"),
            ],
            StackDetailsFactory = (stackId, _) => Task.FromResult<RegistryStackDetails?>(
                CreateRegistryStackDetails(
                    stackId,
                    string.Equals(stackId, "team-stack", StringComparison.OrdinalIgnoreCase) ? "Team Stack" : "Team Stack Tools")),
        };
        using var viewModel = CreateViewModel(root, registryClient);

        await viewModel.ApplyLaunchRequestAsync(new AppLaunchRequest(
            AppLaunchRequestKind.StackDetails,
            StackId: "team-stack",
            RegistryUrl: new Uri("https://registry.example/")));
        await WaitForConditionAsync(() => viewModel.RegistryStackDetailsLoaded && viewModel.SelectedRegistryStack?.StackId == "team-stack");

        Assert.True(viewModel.IsMarketplaceMode);
        Assert.Equal("team-stack", viewModel.RegistrySearchText);
        Assert.Collection(viewModel.RegistryStacks, stack => Assert.Equal("team-stack", stack.StackId));
        Assert.Equal("Team Stack", viewModel.SelectedRegistryStackTitle);
        Assert.Empty(viewModel.Stacks);
        Assert.Null(registryClient.LastDownloadedStackId);
        Assert.Collection(registryClient.StackSearchQueries, query => Assert.Equal("team-stack", query));
    }

    [Fact]
    public async Task ImportSelectedRegistryStackCommand_DownloadsAndImportsLocalStack()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack");
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
            StackDetails = CreateRegistryStackDetails("team-stack", "Team Stack"),
            StackDownloadSourcePath = stackPath,
        };
        using var viewModel = CreateViewModel(root, registryClient);

        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        await viewModel.ImportSelectedRegistryStackCommand.ExecuteAsync(null);

        var localStack = Assert.Single(viewModel.Stacks);
        Assert.Equal("team-stack", localStack.StackId);
        Assert.True(File.Exists(localStack.LocalPath));
        Assert.Equal("team-stack", registryClient.LastDownloadedStackId);
    }

    [Fact]
    public async Task ImportSelectedRegistryStackAsLocalAsync_WhenDownloadIsCancelled_DoesNotPublishPartialImport()
    {
        var root = CreateTempDirectory();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
            StackDetails = CreateRegistryStackDetails("team-stack", "Team Stack"),
            StackDownloadFactory = async (_, _, cancellationToken) =>
            {
                downloadStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        using var viewModel = CreateViewModel(root, registryClient, library);
        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);
        using var cancellation = new CancellationTokenSource();

        var importTask = viewModel.ImportSelectedRegistryStackAsLocalAsync(cancellationToken: cancellation.Token);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.False(await importTask);
        Assert.Empty(await library.ListAsync());
        Assert.Empty(viewModel.Stacks);
        Assert.False(viewModel.IsBusy);
        Assert.NotNull(registryClient.LastStackDownloadDestination);
        Assert.False(Directory.Exists(Path.GetDirectoryName(registryClient.LastStackDownloadDestination)!));
    }

    [Fact]
    public void StackSelectionCoordinator_RapidSelectionsCancelEveryStaleRequest()
    {
        using var lifetime = new CancellationTokenSource();
        using var coordinator = new StackSelectionCoordinator(lifetime.Token);
        var requests = Enumerable.Range(0, 100)
            .Select(_ => coordinator.BeginRegistry(hasSelection: true))
            .ToArray();

        Assert.All(requests[..^1], request => Assert.True(request.Token.IsCancellationRequested));
        Assert.All(requests[..^1], request => Assert.False(coordinator.IsCurrent(request)));
        Assert.False(requests[^1].Token.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(requests[^1]));

        coordinator.Dispose();

        Assert.True(requests[^1].Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(requests[^1]));
    }

    [Fact]
    public async Task CreateStackWizard_InitializesItemsGroupedByOwnerPackage()
    {
        var root = CreateTempDirectory();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ExportDiscoveryResponse = new RuntimeStackExportDiscoveryResponse(
                [
                    CreateExportItem("sunder.package.agent", "profile", "Agent profile", kind: "agent-profile"),
                    CreateExportItem("sunder.package.agent", "workspace", "Agent workspace", kind: "agent-workspace"),
                    CreateExportItem("sunder.package.agent.tools", "tool", "Tool config"),
                ],
                [],
                []),
        };
        var viewModel = new CreateStackWizardViewModel(
            new LocalStackLibraryService(Path.Combine(root, "library")),
            runtimeApiClient);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.PackageGroups.Count);
        Assert.Contains(viewModel.PackageGroups, group => group.PackageId == "sunder.package.agent" && group.Items.Count == 2);
        Assert.Contains(viewModel.PackageGroups, group => group.PackageId == "sunder.package.agent.tools" && group.Items.Count == 1);
        Assert.All(viewModel.PackageGroups, group => Assert.False(group.IsSelected));
        Assert.All(viewModel.PackageGroups.SelectMany(group => group.Items), item => Assert.False(item.IsSelected));
        Assert.Equal(0, viewModel.SelectedPackageCount);
        Assert.Equal(0, viewModel.SelectedItemCount);
        Assert.False(viewModel.CanGoNext);
        viewModel.SelectAllPackagesCommand.Execute(null);
        Assert.All(viewModel.PackageGroups, group => Assert.True(group.IsSelected));
        Assert.True(viewModel.CanGoNext);

        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.CanGoNext);
        var agentGroup = viewModel.PackageGroups.First(group => group.PackageId == "sunder.package.agent");
        Assert.Equal(["Agent Profiles", "Agent Workspaces"], agentGroup.ItemGroups.Select(group => group.DisplayName));
        agentGroup.SelectAllItemsCommand.Execute(null);
        Assert.Equal("2/2 items", agentGroup.CountText);
        Assert.Equal(2, viewModel.SelectedItemCount);
        Assert.True(viewModel.CanGoNext);
        agentGroup.UnselectAllItemsCommand.Execute(null);
        Assert.Equal("0/2 items", agentGroup.CountText);
        Assert.True(viewModel.CanGoNext);
    }

    [Fact]
    public async Task CreateStackWizard_HidesZeroItemPackagesOnItemsStepAndShowsThemInReview()
    {
        var root = CreateTempDirectory();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ActivePackages =
            [
                CreateActivePackage("sunder.package.agent.tools.shell", "Sunder Agent Tools Shell"),
                CreateActivePackage("sunder.package.agent.tools.web", "Sunder Agent Tools Web"),
            ],
        };
        var viewModel = new CreateStackWizardViewModel(
            new LocalStackLibraryService(Path.Combine(root, "library")),
            runtimeApiClient);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.PackageGroups.Count);
        Assert.All(viewModel.PackageGroups, group => Assert.Empty(group.Items));
        Assert.All(viewModel.PackageGroups, group => Assert.Contains("No setup items", group.ContentSummary, StringComparison.Ordinal));
        viewModel.SelectAllPackagesCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        Assert.Equal(0, viewModel.SelectedItemCount);
        Assert.False(viewModel.HasSelectedPackagesWithItems);
        Assert.True(viewModel.ShowNoItemsStepPackages);
        Assert.True(viewModel.CanGoNext);

        viewModel.NextCommand.Execute(null);

        Assert.True(viewModel.IsReviewStep);
        Assert.All(viewModel.PackageGroups, group => Assert.True(group.ShowReviewNoSelectedItems));
        Assert.All(viewModel.PackageGroups, group => Assert.Contains("Package will be installed", group.ReviewNoSelectedItemsText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateStackWizard_UsesUserFacingExportDetails()
    {
        var root = CreateTempDirectory();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ExportDiscoveryResponse = new RuntimeStackExportDiscoveryResponse(
                [CreateExportItem(
                    "sunder.package.agent",
                    "profile",
                    "Agent profile",
                    ["Public", "Secret"],
                    [
                        new RuntimeStackExportItemDetail("Custom instructions", "Use a concise tone.", "Public", ValueWhenExcluded: "Not exported"),
                        new RuntimeStackExportItemDetail("Provider connections", "OpenAI", "Secret", ValueWhenExcluded: "Not exported", SupportsAskOnImport: true),
                    ])],
                [],
                []),
        };
        var viewModel = new CreateStackWizardViewModel(
            new LocalStackLibraryService(Path.Combine(root, "library")),
            runtimeApiClient);

        await viewModel.InitializeAsync();

        var item = Assert.Single(Assert.Single(viewModel.PackageGroups).Items);
        Assert.Contains("Provider connections", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains("secret prompt", item.SecretChips);
        var instructions = Assert.Single(item.Details, detail => detail.Label == "Custom instructions");
        Assert.Equal("Use a concise tone.", instructions.Value);
        Assert.Equal("Include value", Assert.Single(instructions.AvailableExportBehaviors));
        Assert.Equal("Include value", instructions.SelectedExportBehavior);
        Assert.False(instructions.HasFlagText);
        Assert.False(instructions.IsExpanded);
        Assert.True(instructions.IsCollapsed);
        instructions.ToggleExpandedCommand.Execute(null);
        Assert.True(instructions.IsExpanded);
        Assert.All(item.Details, detail => Assert.True(detail.IsIncludedByOptions));
        Assert.All(item.Details, detail => Assert.True(detail.IsSelected));
        var provider = Assert.Single(item.Details, detail => detail.Label == "Provider connections");
        provider.IsSelected = false;
        Assert.False(provider.IsSelected);
        Assert.Equal(1, item.SelectedDetailCount);
        provider.IsSelected = true;
        Assert.Contains("Ask on import", provider.AvailableExportBehaviors);
        provider.SelectedExportBehavior = "Ask on import";
        Assert.Equal("ask on import", provider.FlagText);
        Assert.Null(provider.SensitivityOverride);
        Assert.Equal("Importer will provide this value.", provider.EffectiveValue);
        provider.SelectedExportBehavior = "Include value";
        provider.EditedValue = "OpenAI shared endpoint";
        Assert.False(provider.HasFlagText);
        Assert.Equal("OpenAI shared endpoint", provider.ValueOverride);
        Assert.Equal("Public", provider.SensitivityOverride);
        Assert.DoesNotContain("contributor", item.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateStackWizard_ReviewGroupsOnlyIncludeSelectedItems()
    {
        var root = CreateTempDirectory();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ExportDiscoveryResponse = new RuntimeStackExportDiscoveryResponse(
                [
                    CreateExportItem("sunder.package.agent", "profile", "Agent profile", kind: "agent-profile"),
                    CreateExportItem("sunder.package.agent", "workspace", "Agent workspace", kind: "agent-workspace"),
                ],
                [],
                []),
        };
        var viewModel = new CreateStackWizardViewModel(
            new LocalStackLibraryService(Path.Combine(root, "library")),
            runtimeApiClient);

        await viewModel.InitializeAsync();

        var packageGroup = Assert.Single(viewModel.PackageGroups);
        packageGroup.IsSelected = true;
        packageGroup.Items.Single(item => item.ItemId == "profile").IsSelected = true;

        var includedGroup = Assert.Single(packageGroup.ItemGroups, group => group.Kind == "agent-profile");
        var emptyGroup = Assert.Single(packageGroup.ItemGroups, group => group.Kind == "agent-workspace");
        Assert.True(includedGroup.IsIncludedInReview);
        Assert.Equal("1 item", includedGroup.ReviewCountText);
        Assert.Equal("profile", Assert.Single(includedGroup.ReviewItems).ItemId);
        Assert.False(emptyGroup.IsIncludedInReview);
        Assert.Empty(emptyGroup.ReviewItems);
    }

    [Fact]
    public async Task CreateStackWizard_CreateCommand_ExportsAndImportsCreatedStack()
    {
        var root = CreateTempDirectory();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ExportDiscoveryResponse = new RuntimeStackExportDiscoveryResponse(
                [CreateExportItem("sunder.package.agent", "profile", "Agent profile")],
                [],
                []),
            WriteArchiveOnExport = true,
        };
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var viewModel = new CreateStackWizardViewModel(library, runtimeApiClient)
        {
            StackId = "team-stack",
            StackName = "Team Stack",
            StackShortDescription = "Team setup\nfor coding agents",
        };
        await viewModel.InitializeAsync();
        viewModel.SelectAllPackagesCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        Assert.Single(viewModel.PackageGroups).SelectAllItemsCommand.Execute(null);
        Assert.Single(Assert.Single(viewModel.PackageGroups).Items).Details[0].EditedValue = "Edited setup detail";
        viewModel.NextCommand.Execute(null);

        await viewModel.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(runtimeApiClient.LastExportRequest);
        Assert.Equal("team-stack", runtimeApiClient.LastExportRequest.StackId);
        Assert.Equal("Team Stack", runtimeApiClient.LastExportRequest.Name);
        Assert.Null(runtimeApiClient.LastExportRequest.ReadmeMarkdown);
        Assert.Equal("Team setup\nfor coding agents", runtimeApiClient.LastExportRequest.Summary);
        Assert.Null(runtimeApiClient.LastExportRequest.Media);
        Assert.Equal(["sunder.package.agent"], runtimeApiClient.LastExportRequest.SelectedPackages);
        var selectedItem = Assert.Single(runtimeApiClient.LastExportRequest.SelectedItems);
        var selectedDetail = Assert.Single(selectedItem.Details ?? []);
        Assert.True(selectedDetail.IsSelected);
        Assert.Equal("Edited setup detail", selectedDetail.ValueOverride);
        var stacks = await library.ListAsync();
        var stack = Assert.Single(stacks);
        Assert.Equal("team-stack", stack.StackId);
        var detailPackage = Assert.Single(stack.Details ?? []);
        Assert.Equal("sunder.package.agent", detailPackage.PackageId);
        var detailItem = Assert.Single(detailPackage.Items);
        Assert.Equal("Agent profile", detailItem.DisplayName);
        var detailValue = Assert.Single(detailItem.Values);
        Assert.Equal("Setup item", detailValue.Label);
        Assert.Equal("Edited setup detail", detailValue.Value);
        Assert.Equal("team-stack", viewModel.CreatedStackId);
    }

    [Fact]
    public async Task CreateStackWizard_CreateCommand_AllowsPackageOnlyStack()
    {
        var root = CreateTempDirectory();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ActivePackages = [CreateActivePackage("sunder.package.agent.tools.shell", "Sunder Agent Tools Shell")],
            WriteArchiveOnExport = true,
        };
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var viewModel = new CreateStackWizardViewModel(library, runtimeApiClient)
        {
            StackId = "tools-stack",
            StackName = "Tools Stack",
        };
        await viewModel.InitializeAsync();
        viewModel.SelectAllPackagesCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        Assert.True(viewModel.CanGoNext);
        viewModel.NextCommand.Execute(null);
        Assert.True(viewModel.CanCreate);

        await viewModel.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(runtimeApiClient.LastExportRequest);
        Assert.Empty(runtimeApiClient.LastExportRequest.SelectedItems);
        Assert.Equal(["sunder.package.agent.tools.shell"], runtimeApiClient.LastExportRequest.SelectedPackages);
        var stack = Assert.Single(await library.ListAsync());
        Assert.Equal("tools-stack", stack.StackId);
    }

    [Fact]
    public async Task CreateStackWizard_EditMode_PrepopulatesExistingStackSelections()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includeDisplayDetails: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = await library.ImportAsync(stackPath);
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ExportDiscoveryResponse = new RuntimeStackExportDiscoveryResponse(
                [CreateExportItem(
                    "sunder.package.agent",
                    "profile",
                    "Agent profile",
                    ["Public", "Secret"],
                    [
                        new RuntimeStackExportItemDetail("Custom instructions", "Use a concise tone.", "Public"),
                        new RuntimeStackExportItemDetail("Provider connections", "OpenAI", "Secret", SupportsAskOnImport: true),
                    ],
                    kind: "agent-profile",
                    contributorId: "agent")],
                [],
                []),
        };
        var viewModel = new CreateStackWizardViewModel(library, runtimeApiClient, new CreateStackWizardEditContext(stack));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsEditMode);
        Assert.Equal("Edit Stack", viewModel.WindowTitle);
        Assert.Equal("Save Changes", viewModel.PrimaryActionText);
        Assert.True(viewModel.IsStackIdReadOnly);
        Assert.Equal("team-stack", viewModel.StackId);
        Assert.Equal("Team Stack", viewModel.StackName);
        var package = Assert.Single(viewModel.PackageGroups);
        Assert.True(package.IsSelected);
        var item = Assert.Single(package.Items);
        Assert.True(item.IsSelected);
        var instructions = Assert.Single(item.Details, detail => detail.Label == "Custom instructions");
        Assert.True(instructions.IsSelected);
        Assert.Equal("Use a concise tone.", instructions.EditedValue);
        var provider = Assert.Single(item.Details, detail => detail.Label == "Provider connections");
        Assert.True(provider.IsSelected);
        Assert.Equal("Ask on import", provider.SelectedExportBehavior);
        Assert.Empty(viewModel.PreservedFragments);
    }

    [Fact]
    public async Task CreateStackWizard_EditCommand_PreservesUnmatchedFragmentsAndPublishState()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includePackage: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = await library.ImportAsync(stackPath);
        var publishedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var publishedUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await library.UpdatePublishStateAsync(stack.StackId, "https://registry.example/", "team-stack", publishedAt, publishedUpdatedAt);
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            WriteArchiveOnExport = true,
        };
        var viewModel = new CreateStackWizardViewModel(library, runtimeApiClient, new CreateStackWizardEditContext(stack));

        await viewModel.InitializeAsync();
        Assert.Single(viewModel.PreservedFragments);
        viewModel.StackName = "Updated Team Stack";
        viewModel.NextCommand.Execute(null);
        viewModel.NextCommand.Execute(null);
        await viewModel.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(runtimeApiClient.LastExportRequest);
        Assert.Equal(["sunder.package.agent"], runtimeApiClient.LastExportRequest.SelectedPackages);
        Assert.Empty(runtimeApiClient.LastExportRequest.SelectedItems);
        var listed = Assert.Single(await library.ListAsync());
        Assert.Equal("Updated Team Stack", listed.Name);
        Assert.Equal("https://registry.example/", listed.RegistryUrl);
        Assert.Equal("team-stack", listed.PublishedStackId);
        Assert.Equal(publishedAt, listed.PublishedAtUtc);
        Assert.Equal(publishedUpdatedAt, listed.PublishedUpdatedAtUtc);
        var manifest = await library.ReadManifestAsync(listed.LocalPath);
        var preservedFragment = Assert.Single(manifest.Fragments ?? []);
        Assert.Equal("agent-profile", preservedFragment.FragmentId);
    }

    [Fact]
    public async Task UseStackWizard_ExposesReadOnlyProfileMediaAndMarkdown()
    {
        var root = CreateTempDirectory();
        var mediaPath = Path.Combine(root, "hero.png");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4]);
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includePackage: false);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = (await library.ImportAsync(stackPath)) with
        {
            ReadmeMarkdown = "# Team Stack\n\nUse this setup.",
            Media =
            [
                new LocalStackMedia(
                    "payload/media/hero.png",
                    "hero.png",
                    "image/png",
                    4,
                    "Hero image",
                    0,
                    mediaPath),
            ],
        };
        var viewModel = new UseStackWizardViewModel(
            library,
            stack,
            new FakeRuntimeApiClient(),
            new RegistryPackageInstallService(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            _ => new FakeRegistryApiClient(),
            "https://registry.example/");

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasStackReadme);
        Assert.True(viewModel.HasStackProfileMedia);
        Assert.True(viewModel.StackReadmeMarkdownBuilder.Length > 0);
        var media = Assert.Single(viewModel.StackProfileMedia);
        Assert.Equal("Hero image", media.Caption);
        Assert.Equal("hero.png", media.FileName);
    }

    [Fact]
    public async Task StacksWindowViewModel_SelectsCreatedStackDetailsFromLocalLibrary()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includeRequiredInput: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var imported = await library.ImportAsync(stackPath);
        await library.UpdateDetailsAsync(imported.StackId,
        [
            new LocalStackDetailPackage(
                "sunder.package.agent",
                "Agent",
                "A",
                [new LocalStackDetailItem(
                    "profile",
                    "Agent profile",
                    "Instructions",
                    [new LocalStackDetailValue("Tone", "Concise", "Include value")])]),
        ]);
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ActivePackages =
            [
                new ActivePackageDescriptor(
                    "sunder.package.agent",
                    "Agent",
                    "1.0.0",
                    new PackageIconDescriptor("A", "icons/agent.png"),
                    true,
                    PackageReadinessState.Ready,
                    []),
            ],
        };
        using var viewModel = CreateViewModel(root, new FakeRegistryApiClient(), library, runtimeApiClient: runtimeApiClient);

        viewModel.ShowLocalCommand.Execute(null);
        await viewModel.InitializeAsync();
        await WaitForConditionAsync(() => viewModel.SelectedLocalDetails.Count == 1 && viewModel.SelectedLocalDetails[0].IconUri is not null);

        Assert.True(viewModel.CanRemoveSelectedStack);
        Assert.True(viewModel.CanExportSelectedStack);
        Assert.True(viewModel.CanPublishSelectedStack);
        Assert.DoesNotContain("Loaded", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        var package = Assert.Single(viewModel.SelectedLocalDetails);
        Assert.Equal("Agent", package.DisplayName);
        Assert.Equal("file:///packages/sunder.package.agent/icons/agent.png", package.IconUri?.ToString());
        Assert.True(package.IsExpanded);
        Assert.False(package.IsCollapsed);
        Assert.Equal("Agent Profiles", Assert.Single(package.ItemGroups).DisplayName);
        package.ToggleExpandedCommand.Execute(null);
        Assert.False(package.IsExpanded);
        package.ToggleExpandedCommand.Execute(null);
        Assert.True(package.IsExpanded);
        var item = Assert.Single(package.Items);
        Assert.Equal("Agent profile", item.DisplayName);
        Assert.False(item.IsExpanded);
        item.ToggleExpandedCommand.Execute(null);
        Assert.True(item.IsExpanded);
        var value = Assert.Single(item.Values);
        Assert.Equal("Concise", value.Value);
        Assert.False(value.IsExpanded);
        value.ToggleExpandedCommand.Execute(null);
        Assert.True(value.IsExpanded);
        await WaitForConditionAsync(() => viewModel.SelectedFragments.Count == 1);
        Assert.False(viewModel.HasSelectedRequiredInputs);
    }

    [Fact]
    public async Task UseStackWizard_DefaultsSetupItemsSelectedAndAppliesGeneratedActions()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includePackage: false);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = (await library.ImportAsync(stackPath)) with
        {
            Details =
            [
                new LocalStackDetailPackage(
                    "sunder.package.agent",
                    "Sunder Agent",
                    "A",
                    [new LocalStackDetailItem(
                        "profile",
                        "Blender Artist",
                        "Custom instructions, Provider connections",
                        [new LocalStackDetailValue("Custom instructions", "# Professional Blender 3D Agent Instructions", "Include value")])]),
            ],
        };
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ImportPreviewResponse = new RuntimeStackImportPreviewResponse(
                true,
                "plan-1",
                DateTimeOffset.UtcNow.AddMinutes(15),
                [
                    new RuntimeStackImportActionDescriptor("profile", "agent", "Agent profile", "Profile", DefaultSelected: false, "Import profile."),
                ],
                [new RuntimeStackRequiredInputDescriptor("api-key", "agent", "API key", true, "Provider key.")],
                [],
                [],
                []),
            ImportResponse = new RuntimeStackImportResponse(
                RuntimeStackImportOutcome.Completed,
                [new RuntimeStackImportedItemDescriptor("profile", "agent", "Agent profile", "Profile")],
                new Dictionary<string, string>(),
                [],
                [],
                []),
        };
        var viewModel = new UseStackWizardViewModel(
            library,
            stack,
            runtimeApiClient,
            new RegistryPackageInstallService(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            _ => new FakeRegistryApiClient(),
            "https://registry.example/");

        await viewModel.InitializeAsync();

        var packageGroup = Assert.Single(viewModel.SetupPackageGroups);
        Assert.Equal("Agent Profiles", Assert.Single(packageGroup.ItemGroups).DisplayName);
        var setupItem = Assert.Single(packageGroup.Items);
        Assert.Equal("Blender Artist", setupItem.DisplayName);
        var detail = Assert.Single(setupItem.Details);
        Assert.Equal("Custom instructions", detail.Label);
        Assert.Equal("# Professional Blender 3D Agent Instructions", detail.ValuePreview);
        Assert.True(setupItem.IsSelected);
        Assert.Single(viewModel.ImportActions);
        Assert.False(viewModel.CanApply);
        Assert.Equal("api-key", Assert.Single(viewModel.RequiredInputs).InputId);
        Assert.Equal("Not provided", Assert.Single(viewModel.RequiredInputs).ReviewValue);
        Assert.Single(viewModel.RequiredInputs).Value = "secret-local-value";
        Assert.True(viewModel.CanApply);

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(runtimeApiClient.LastImportRequest);
        Assert.Equal(["agent-profile"], runtimeApiClient.LastImportRequest.SelectedFragmentIds);
        Assert.Equal(["profile"], runtimeApiClient.LastImportRequest.SelectedActionIds);
        Assert.Equal("plan-1", runtimeApiClient.LastImportRequest.PlanId);
        Assert.Equal("secret-local-value", runtimeApiClient.LastPreviewRequest?.InputValues["api-key"]);
    }

    [Fact]
    public async Task UseStackWizard_ImportedStackShowsManifestDisplayDetails()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includePackage: false, includeDisplayDetails: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = await library.ImportAsync(stackPath);
        var viewModel = new UseStackWizardViewModel(
            library,
            stack,
            new FakeRuntimeApiClient(),
            new RegistryPackageInstallService(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            _ => new FakeRegistryApiClient(),
            "https://registry.example/");

        await viewModel.InitializeAsync();

        var setupItem = Assert.Single(Assert.Single(viewModel.SetupPackageGroups).Items);
        Assert.Equal("Agent Profiles", Assert.Single(Assert.Single(viewModel.SetupPackageGroups).ItemGroups).DisplayName);
        Assert.Equal("Blender Artist", setupItem.DisplayName);
        Assert.Equal("Custom instructions, Provider connections", setupItem.Summary);
        Assert.Collection(
            setupItem.Details,
            detail =>
            {
                Assert.Equal("Custom instructions", detail.Label);
                Assert.Equal("Use a concise tone.", detail.ValuePreview);
                Assert.Equal("Include value", detail.Behavior);
            },
            detail =>
            {
                Assert.Equal("Provider connections", detail.Label);
                Assert.Equal("Importer will provide this value.", detail.ValuePreview);
                Assert.Equal("Ask on import", detail.Behavior);
            });
    }

    [Fact]
    public async Task UseStackWizard_ApplyInstallsPackagesBeforeImportingSetup()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack", includeFragment: true, includePackage: true);
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        var stack = await library.ImportAsync(stackPath);
        var installPlan = new RegistryResolveInstallPlanResponse(
            true,
            [new RegistryPackageInstallPlanItem(
                "sunder.package.agent",
                CurrentVersion: null,
                Version: "1.2.0",
                IsUpdate: false,
                DeprecatedMessage: null,
                DependsOn: [],
                new RegistryPackageArtifact("sha256", 1024, "https://registry.example/packages/sunder.package.agent/1.2.0/download"))],
            [],
            [],
            []);
        var noChangesPlan = new RegistryResolveInstallPlanResponse(true, [], [], [], []);
        var registryClient = new FakeRegistryApiClient
        {
            InstallPlanResponses = [installPlan, installPlan, noChangesPlan],
        };
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            ImportPreviewResponse = new RuntimeStackImportPreviewResponse(
                true,
                "plan-1",
                DateTimeOffset.UtcNow.AddMinutes(15),
                [new RuntimeStackImportActionDescriptor("profile", "agent", "Agent profile", "Profile", DefaultSelected: false, "Import profile.")],
                [],
                [],
                [],
                []),
            ImportResponse = new RuntimeStackImportResponse(
                RuntimeStackImportOutcome.Completed,
                [new RuntimeStackImportedItemDescriptor("profile", "agent", "Agent profile", "Profile")],
                new Dictionary<string, string>(),
                [],
                [],
                []),
            RegistryPlan = installPlan,
            PackageStoreStageOperationResult = SuccessPackageOperation() with
            {
                ImpactedPackageIds = ["sunder.package.agent"],
            },
        };
        var viewModel = new UseStackWizardViewModel(
            library,
            stack,
            runtimeApiClient,
            new RegistryPackageInstallService(),
            (packageIds, _) =>
            {
                runtimeApiClient.Events.Add("lifecycle:" + string.Join(",", packageIds));
                return Task.CompletedTask;
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            _ => registryClient,
            "https://registry.example/");

        await viewModel.InitializeAsync();

        var packageRow = Assert.Single(viewModel.PackageRows);
        Assert.Equal("Will install 1.2.0", packageRow.StatusText);
        Assert.True(viewModel.CanApply);

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["install", "lifecycle:sunder.package.agent", "preview", "import"], runtimeApiClient.Events);
        Assert.Equal(["agent-profile"], runtimeApiClient.LastImportRequest?.SelectedFragmentIds);
        Assert.Equal(["profile"], runtimeApiClient.LastImportRequest?.SelectedActionIds);
    }

    private static StacksWindowViewModel CreateViewModel(
        string root,
        FakeRegistryApiClient registryClient,
        LocalStackLibraryService? library = null,
        Func<Uri, object?>? tokenProvider = null,
        FakeRuntimeApiClient? runtimeApiClient = null,
        TimeSpan? registrySearchThrottleDelay = null,
        TimeSpan? registryDetailSpinnerDelay = null)
        => new(
            library ?? new LocalStackLibraryService(Path.Combine(root, "library")),
            new FakeStackArchivePicker(),
            runtimeApiClient ?? new FakeRuntimeApiClient(),
            registryClientFactory: _ => registryClient,
            registrySearchThrottleDelay: registrySearchThrottleDelay,
            registryDetailSpinnerDelay: registryDetailSpinnerDelay);

    private static RegistryStackSummary CreateRegistryStackSummary(string stackId, string name, RegistryStackStats? stats = null)
        => new(
            stackId,
            name,
            "A shared Stack.",
            PackageCount: 1,
            FragmentCount: 0,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            stats);

    private static RegistryStackDetails CreateRegistryStackDetails(string stackId, string name, RegistryStackStats? stats = null)
        => new(
            stackId,
            name,
            "A shared Stack.",
            [new RegistryStackPackageRequirement("sunder.package.agent", "latest", null, "1.0.0", true)],
            [new RegistryStackFragmentSummary(
                "agent-profile",
                "sunder.package.agent",
                "sunder.package.agent.profile",
                "sunder.package.agent/profile",
                1,
                "Agent profile",
                "Profile settings.",
                true,
                "profile",
                "agent-profile",
                [new RegistryStackFragmentDetail("Custom instructions", "Use a concise tone.", "Include value")])],
            [new RegistryStackRequiredInput("api-key", "API key", "Provider key.", true)],
            new RegistryStackArtifact("", 0, $"https://registry.example/api/v1/stacks/{stackId}/download"),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            stats);

    private static RuntimeStackExportItemDescriptor CreateExportItem(
        string ownerPackageId,
        string itemId,
        string displayName,
        IReadOnlyList<string>? sensitivities = null,
        IReadOnlyList<RuntimeStackExportItemDetail>? details = null,
        string kind = "Configuration",
        string? contributorId = null)
        => new(
            contributorId ?? ownerPackageId + ".contributor",
            ownerPackageId,
            itemId,
            displayName,
            kind,
            DefaultSelected: true,
            sensitivities ?? [],
            "Exportable setup item.",
            details ?? []);

    private static ActivePackageDescriptor CreateActivePackage(string packageId, string displayName)
        => new(packageId, displayName, "1.0.0", null, true, PackageReadinessState.Ready, []);

    private static async Task<string> CreateStackArchiveAsync(
        string root,
        string stackId,
        string name,
        bool includeFragment = false,
        bool includePackage = true,
        bool includeRequiredInput = false,
        bool includeDisplayDetails = false)
    {
        var path = Path.Combine(root, stackId + ".sunderstack");
        var payloadFiles = new Dictionary<string, string>();
        IReadOnlyList<SunderStackFragmentManifest> fragments = [];
        if (includeFragment)
        {
            var payloadPath = Path.Combine(root, "agent-profile.json");
            await File.WriteAllTextAsync(payloadPath, "{}\n");
            payloadFiles["payload/fragments/agent-profile.json"] = payloadPath;
            fragments =
            [
                new SunderStackFragmentManifest
                {
                    FragmentId = "agent-profile",
                    OwnerPackageId = "sunder.package.agent",
                    ContributorId = "agent",
                    SchemaId = "agent.profile",
                    SchemaVersion = 1,
                    DisplayName = includeDisplayDetails ? "Blender Artist" : "Agent profile",
                    Description = "Profile settings.",
                    DefaultSelected = false,
                    PayloadPath = "payload/fragments/agent-profile.json",
                    RequiredInputs = includeRequiredInput
                        ? [new SunderStackRequiredInputManifest { InputId = "api-key", Label = "API key", Required = true }]
                        : [],
                    Preview = new SunderStackFragmentPreview
                    {
                        SourceItemId = "profile",
                        Kind = "agent-profile",
                        DisplayDetails = includeDisplayDetails
                            ?
                            [
                                new SunderStackFragmentDisplayDetail
                                {
                                    Label = "Custom instructions",
                                    Value = "Use a concise tone.",
                                    Behavior = "Include value",
                                },
                                new SunderStackFragmentDisplayDetail
                                {
                                    Label = "Provider connections",
                                    Value = "Importer will provide this value.",
                                    Behavior = "Ask on import",
                                },
                            ]
                            : [],
                    },
                },
            ];
        }

        await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
        {
            SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
            MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
            StackId = stackId,
            Name = name,
            Summary = "A shared Stack.",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Packages = includePackage
                ?
                [
                    new SunderStackPackageRequirement
                    {
                        PackageId = "sunder.package.agent",
                        InstallTag = "latest",
                        MinimumVersion = "1.0.0",
                        Required = true,
                    },
                ]
                : [],
            Fragments = fragments,
        }, path, payloadFiles);
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-stacks-window-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeStackArchivePicker : IStackArchivePicker
    {
        public string? StackSavePath { get; init; }

        public string? LastSuggestedFileName { get; private set; }

        public Task<string?> PickStackPathAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickStackSavePathAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            LastSuggestedFileName = suggestedFileName;
            return Task.FromResult(StackSavePath);
        }

    }

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public IReadOnlyList<RegistryStackSummary> StackSearchResults { get; init; } = [];

        public RegistryStackDetails? StackDetails { get; init; }

        public Func<string, CancellationToken, Task<RegistryStackDetails?>>? StackDetailsFactory { get; init; }

        public string? StackDownloadSourcePath { get; init; }

        public Func<string, string, CancellationToken, Task>? StackDownloadFactory { get; init; }

        public string? LastStackSearchQuery { get; private set; }

        public string? LastStackDownloadDestination { get; private set; }

        public List<string?> StackSearchQueries { get; } = [];

        public List<RegistrySearchSort> StackSearchSorts { get; } = [];

        public string? LastDownloadedStackId { get; private set; }

        public string? LastPublishToken { get; private set; }

        public string? LastPublishPath { get; private set; }

        public string? LastDeleteStackId { get; private set; }

        public string? LastDeleteToken { get; private set; }

        public string? LastStarStackId { get; private set; }

        public string? LastStarToken { get; private set; }

        public string? LastUnstarStackId { get; private set; }

        public string? LastUnstarToken { get; private set; }

        public RegistryStackManagementOperationResponse DeleteStackResponse { get; init; } = new(true, "Deleted Stack.", []);

        public RegistryStackStarResponse StarStackResponse { get; init; } = new(true, "Starred Stack.", new RegistryStackStats(0, 1, true), []);

        public RegistryStackStarResponse UnstarStackResponse { get; init; } = new(true, "Unstarred Stack.", new RegistryStackStats(0, 0, false), []);

        public IReadOnlyList<RegistryResolveInstallPlanResponse> InstallPlanResponses { get; init; } = [new(true, [], [], [], [])];

        public List<RegistryResolveInstallPlanRequest> InstallPlanRequests { get; } = [];

        private int _installPlanResponseIndex;

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);

        public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageDetails?>(null);

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageVersionDetails?>(null);

        public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
        {
            LastStackSearchQuery = query;
            StackSearchQueries.Add(query);
            StackSearchSorts.Add(sort);
            return Task.FromResult<IReadOnlyList<RegistryStackSummary>>(StackSearchResults.Skip(skip).Take(take).ToArray());
        }

        public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StackDetailsFactory is null
                ? Task.FromResult(StackDetails)
                : StackDetailsFactory(stackId, cancellationToken);
        }

        public Task<RegistryStackDetails?> GetStackAsync(string stackId, string bearerToken, CancellationToken cancellationToken = default)
            => GetStackAsync(stackId, cancellationToken);

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(RegistryResolveUpdatesRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryResolveUpdatesResponse([]));

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(RegistryResolveInstallPlanRequest request, CancellationToken cancellationToken = default)
        {
            InstallPlanRequests.Add(request);
            var response = InstallPlanResponses[Math.Min(_installPlanResponseIndex, InstallPlanResponses.Count - 1)];
            _installPlanResponseIndex++;
            return Task.FromResult(response);
        }

        public Task DownloadArtifactAsync(RegistryPackageArtifact artifact, string packageId, string version, string destinationPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default)
        {
            LastDownloadedStackId = stackId;
            LastStackDownloadDestination = destinationPath;
            if (StackDownloadFactory is not null)
            {
                await StackDownloadFactory(stackId, destinationPath, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(StackDownloadSourcePath))
            {
                throw new InvalidOperationException("No Stack download source configured.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(StackDownloadSourcePath, destinationPath, overwrite: true);
        }

        public Task<RegistryPublishStackResponse> PublishStackAsync(string stackPath, string bearerToken, CancellationToken cancellationToken = default)
        {
            LastPublishPath = stackPath;
            LastPublishToken = bearerToken;
            return Task.FromResult(new RegistryPublishStackResponse(true, "team-stack", "Published Stack 'team-stack'.", [], []));
        }

        public Task<RegistryStackManagementOperationResponse> DeleteStackAsync(string stackId, string bearerToken, CancellationToken cancellationToken = default)
        {
            LastDeleteStackId = stackId;
            LastDeleteToken = bearerToken;
            return Task.FromResult(DeleteStackResponse);
        }

        public Task<RegistryStackStarResponse> StarStackAsync(string stackId, string bearerToken, CancellationToken cancellationToken = default)
        {
            LastStarStackId = stackId;
            LastStarToken = bearerToken;
            return Task.FromResult(StarStackResponse);
        }

        public Task<RegistryStackStarResponse> UnstarStackAsync(string stackId, string bearerToken, CancellationToken cancellationToken = default)
        {
            LastUnstarStackId = stackId;
            LastUnstarToken = bearerToken;
            return Task.FromResult(UnstarStackResponse);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntimeApiClient : IRuntimeStacksClient
    {
        private string? _exportPath;
        public RuntimeStackExportDiscoveryResponse ExportDiscoveryResponse { get; init; } = new([], [], []);

        public RuntimeStackImportPreviewResponse ImportPreviewResponse { get; init; } = new(true, "plan-1", DateTimeOffset.UtcNow.AddMinutes(15), [], [], [], [], []);

        public RuntimeStackImportResponse ImportResponse { get; init; } = new(RuntimeStackImportOutcome.Completed, [], new Dictionary<string, string>(), [], [], []);

        public RegistryResolveInstallPlanResponse RegistryPlan { get; init; } = new(true, [], [], [], []);

        public PackageOperationResult PackageStoreStageOperationResult { get; init; } = SuccessPackageOperation();

        public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; init; } = [];

        public IReadOnlyList<SessionPackageDescriptor> SessionPackages { get; init; } = [];

        public IReadOnlyList<InstalledPackageDescriptor> InstalledPackages { get; init; } = [];

        public RuntimeStackExportRequest? LastExportRequest { get; private set; }

        public RuntimeStackImportRequest? LastImportRequest { get; private set; }

        public RuntimeStackImportPreviewRequest? LastPreviewRequest { get; private set; }

        public PackageStoreStageRequest? LastPackageStoreStageRequest { get; private set; }

        public List<string> Events { get; } = [];

        public bool WriteArchiveOnExport { get; init; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActivePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(SessionPackages);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>([]);

        public Task<PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<PackageSessionStatus?>(null);

        public Task<PackageSessionOperationResult> LoadPackageSessionAsync(PackageSessionLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSessionOperationResult.Failed("Not configured for this test."));

        public Task<PackageSessionOperationResult> UnloadPackageSessionAsync(string packageId, PackageSourceKind sourceKind, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSessionOperationResult.Failed("Not configured for this test."));

        public Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken = default)
            => Task.FromResult(Success() with { ImpactedPackageIds = impactedPackageIds });

        public Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(PackageLifecycleStageRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageLifecycleStageResult.Failed("Not configured for this test."));

        public Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageLifecycleOperationResult.Failed("Not configured for this test."));

        public Task DiscardPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DownloadPackageUiSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(InstalledPackages);

        public Task<RegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(RegistryPlan);

        public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("install");
            return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Installed packages.",
                true,
                false,
                RegistryPlan.Warnings,
                [],
                RegistryPlan.Items.Select(item => item.PackageId).ToArray(),
                RegistryPlan.Items));
        }

        public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Installed package.", true, false, [], [], [request.PackageId], []));

        public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Updated packages.", true, false, [], [], [], []));

        public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryPackageStarResponse(true, "Updated package star.", null, []));

        public Task<RegistryStackStarResponse> SetRegistryStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryStackStarResponse(true, "Updated Stack star.", new RegistryStackStats(0, request.Starred ? 1 : 0, request.Starred), []));

        public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryPublishStackResponse(true, "team-stack", "Published Stack 'team-stack'.", [], []));

        public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RegistryStackManagementOperationResponse(true, "Deleted Stack.", []));

        public Uri CreatePackageAssetUri(string packageId, string assetPath) => new($"file:///packages/{packageId}/{assetPath}");

        public Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor("package-upload", "test-hash", 0, Path.GetFileName(packagePath), "application/vnd.sunder.package"));

        public Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor(
                "stack-upload",
                "test-hash",
                new FileInfo(stackPath).Length,
                Path.GetFileName(stackPath),
                "application/vnd.sunder.stack"));

        public Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentUploadDescriptor(
                "media-upload",
                "test-hash",
                new FileInfo(mediaPath).Length,
                Path.GetFileName(mediaPath),
                contentType));

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default)
        {
            LastPackageStoreStageRequest = request;
            var impactedPackageIds = request.Mutations.Select(GetMutationPackageId).ToArray();
            return Task.FromResult(new PackageStoreStageResult(
                "stage-1",
                SuccessPackageOperation() with
                {
                    ImpactedPackageIds = impactedPackageIds,
                },
                impactedPackageIds.Select(packageId => new ActivePackageDescriptor(packageId, packageId, "1.0.0", null, true, PackageReadinessState.Ready, [])).ToArray(),
                impactedPackageIds.Select(packageId => RuntimeContractTestData.Snapshot(packageId, PackageSourceKind.Installed, packageId)).ToArray()));
        }

        public Task<PackageOperationResult> CommitPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            Events.Add("install");
            return Task.FromResult(PackageStoreStageOperationResult);
        }

        public Task DiscardPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageLifecycleOperationResult(true, "ok", [], [], [], [], []));

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageConfigurationSchemaDescriptor>>([]);

        public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult<PackageSettingsValuesResponse?>(null);

        public Task SavePackageSettingsValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult<PackageAuthStatusResponse?>(null);

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult<PackageAuthSessionStartResponse?>(null);

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken cancellationToken = default) => Task.FromResult<PackageAuthSessionStatusResponse?>(null);

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult<PackageAuthStatusResponse?>(null);

        public Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ExportDiscoveryResponse);

        public async Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken cancellationToken = default)
        {
            LastExportRequest = request;
            var exportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sunderstack");
            _exportPath = exportPath;
            if (WriteArchiveOnExport)
            {
                var selectedPackages = (request.SelectedPackages ?? [])
                    .Concat(ExportDiscoveryResponse.Items
                    .Where(item => request.SelectedItems.Any(selected => string.Equals(selected.ContributorId, item.ContributorId, StringComparison.Ordinal)
                                                                && string.Equals(selected.ItemId, item.ItemId, StringComparison.Ordinal)))
                    .Select(item => item.OwnerPackageId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(packageId => new SunderStackPackageRequirement
                    {
                        PackageId = packageId,
                        InstallTag = "latest",
                        Required = true,
                    })
                    .ToArray();
                await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
                {
                    SchemaVersion = 1,
                    StackId = request.StackId,
                    Name = request.Name,
                    Summary = request.Summary,
                    ReadmeMarkdown = request.ReadmeMarkdown,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Packages = selectedPackages,
                    Fragments = [],
                    Media = request.Media?.Select(media => new SunderStackMediaManifest
                    {
                         Path = media.ContentPath,
                        FileName = media.FileName,
                        ContentType = media.ContentType,
                         Size = new FileInfo(media.UploadId).Length,
                        AltText = media.AltText,
                        SortOrder = media.SortOrder,
                    }).ToArray(),
                }, exportPath, request.Media?.ToDictionary(media => media.ContentPath, media => media.UploadId) ?? [], cancellationToken);
            }
            else
            {
                await File.WriteAllBytesAsync(exportPath, [], cancellationToken);
            }

            var info = new FileInfo(exportPath);
            return new RuntimeStackExportResponse(true, new ContentDownloadDescriptor("download", string.Empty, info.Length, info.Name, "application/vnd.sunder.stack", "downloads/download"), [], []);
        }

        public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default)
        {
            File.Copy(_exportPath ?? throw new InvalidOperationException("No Stack export is available."), destinationPath, overwrite: true);
            return Task.CompletedTask;
        }

        public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken cancellationToken = default)
        {
            LastPreviewRequest = request;
            Events.Add("preview");
            return Task.FromResult(ImportPreviewResponse);
        }

        public Task<RuntimeStackImportResponse> ImportStackAsync(RuntimeStackImportRequest request, CancellationToken cancellationToken = default)
        {
            LastImportRequest = request;
            Events.Add("import");
            return Task.FromResult(ImportResponse);
        }

        public void Dispose()
        {
        }

        private static PackageOperationResult Success()
            => SuccessPackageOperation();

        private static string GetMutationPackageId(PackageStoreMutationRequest mutation)
            => !string.IsNullOrWhiteSpace(mutation.PackageId)
                ? mutation.PackageId
                : Path.GetFileName(mutation.UploadId ?? string.Empty).Split('.')[0];
    }

    private static PackageOperationResult SuccessPackageOperation()
        => new(true, "ok", RuntimeSessionApplied: true, RequiresAppRestart: false, [], []);
}
