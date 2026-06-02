using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.PackageManagement;
using Sunder.Protocol;
using Sunder.Registry.Shared;
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
        await WaitForConditionAsync(() => viewModel.RegistrySelectedPackages.Count == 1);

        Assert.True(viewModel.IsMarketplaceMode);
        Assert.True(viewModel.ShowRegistrySelectedDetails);
        Assert.Equal("Team Stack", viewModel.SelectedRegistryStackTitle);
        Assert.Contains("private text", viewModel.SelectedRegistryStackSafetyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sunder.package.agent", Assert.Single(viewModel.RegistrySelectedPackages).PackageId);
        Assert.Equal("Agent profile", Assert.Single(viewModel.RegistrySelectedFragments).DisplayName);
        Assert.Equal("API key (Secret)", Assert.Single(viewModel.RegistrySelectedRequiredInputs));
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
    public async Task PublishSelectedStackCommand_UsesSavedRegistryTokenAndPersistsPublishedState()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack");
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        await library.ImportAsync(stackPath);
        var registryClient = new FakeRegistryApiClient();
        using var viewModel = CreateViewModel(
            root,
            registryClient,
            library,
            registryUrl => new RegistryAuthToken(registryUrl.ToString(), "token-123", "owner", DateTimeOffset.UtcNow.AddHours(1)));
        viewModel.RegistryUrlText = "https://registry.example/";
        viewModel.ShowLocalCommand.Execute(null);
        await viewModel.InitializeAsync();

        await viewModel.PublishSelectedStackCommand.ExecuteAsync(null);

        Assert.Equal("token-123", registryClient.LastPublishToken);
        Assert.EndsWith("team-stack.sunderstack", registryClient.LastPublishPath);
        var publishedStack = Assert.Single(await library.ListAsync());
        Assert.Equal("team-stack", publishedStack.PublishedStackId);
        Assert.Equal("https://registry.example/", publishedStack.RegistryUrl);
        Assert.True(viewModel.SelectedStack?.IsPublished);
        Assert.False(viewModel.CanPublishSelectedStack);
        Assert.True(viewModel.CanUnpublishSelectedStack);
    }

    [Fact]
    public async Task UnpublishSelectedStackCommand_DeletesRegistryStackAndClearsPublishedState()
    {
        var root = CreateTempDirectory();
        var stackPath = await CreateStackArchiveAsync(root, "team-stack", "Team Stack");
        var library = new LocalStackLibraryService(Path.Combine(root, "library"));
        await library.ImportAsync(stackPath);
        await library.UpdatePublishStateAsync(
            "team-stack",
            "https://registry.example/",
            "published-team-stack",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(-1));
        var registryClient = new FakeRegistryApiClient
        {
            DeleteStackResponse = new RegistryStackManagementOperationResponse(true, "Unpublished Stack.", []),
        };
        using var viewModel = CreateViewModel(
            root,
            registryClient,
            library,
            registryUrl => new RegistryAuthToken(registryUrl.ToString(), "token-123", "owner", DateTimeOffset.UtcNow.AddHours(1)));
        viewModel.ShowLocalCommand.Execute(null);
        await viewModel.InitializeAsync();

        await viewModel.UnpublishSelectedStackCommand.ExecuteAsync(null);

        Assert.Equal("published-team-stack", registryClient.LastDeleteStackId);
        Assert.Equal("token-123", registryClient.LastDeleteToken);
        var unpublishedStack = Assert.Single(await library.ListAsync());
        Assert.Null(unpublishedStack.PublishedStackId);
        Assert.Null(unpublishedStack.RegistryUrl);
        Assert.False(viewModel.SelectedStack?.IsPublished);
        Assert.True(viewModel.CanPublishSelectedStack);
    }

    [Fact]
    public async Task DeleteSelectedRegistryStackCommand_UsesSavedRegistryTokenAndRemovesResult()
    {
        var root = CreateTempDirectory();
        var registryClient = new FakeRegistryApiClient
        {
            StackSearchResults = [CreateRegistryStackSummary("team-stack", "Team Stack")],
            DeleteStackResponse = new RegistryStackManagementOperationResponse(true, "Deleted Stack 'team-stack'.", []),
        };
        using var viewModel = CreateViewModel(
            root,
            registryClient,
            tokenProvider: registryUrl => new RegistryAuthToken(registryUrl.ToString(), "token-123", "owner", DateTimeOffset.UtcNow.AddHours(1)));
        await viewModel.SearchRegistryStacksCommand.ExecuteAsync(null);

        await viewModel.DeleteSelectedRegistryStackCommand.ExecuteAsync(null);

        Assert.Equal("team-stack", registryClient.LastDeleteStackId);
        Assert.Equal("token-123", registryClient.LastDeleteToken);
        Assert.Empty(viewModel.RegistryStacks);
        Assert.Contains("Deleted Stack", viewModel.StatusText, StringComparison.Ordinal);
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
        Assert.All(viewModel.PackageGroups, group => Assert.True(group.IsSelected));
        Assert.All(viewModel.PackageGroups.SelectMany(group => group.Items), item => Assert.False(item.IsSelected));
        Assert.Equal(0, viewModel.SelectedItemCount);
        Assert.True(viewModel.CanGoNext);

        viewModel.UnselectAllPackagesCommand.Execute(null);
        Assert.All(viewModel.PackageGroups, group => Assert.False(group.IsSelected));
        Assert.False(viewModel.CanGoNext);
        viewModel.SelectAllPackagesCommand.Execute(null);
        Assert.All(viewModel.PackageGroups, group => Assert.True(group.IsSelected));
        Assert.True(viewModel.CanGoNext);

        viewModel.NextCommand.Execute(null);

        Assert.False(viewModel.CanGoNext);
        var agentGroup = viewModel.PackageGroups.First(group => group.PackageId == "sunder.package.agent");
        Assert.Equal(["Agent Profiles", "Agent Workspaces"], agentGroup.ItemGroups.Select(group => group.DisplayName));
        agentGroup.SelectAllItemsCommand.Execute(null);
        Assert.Equal("2/2 items", agentGroup.CountText);
        Assert.Equal(2, viewModel.SelectedItemCount);
        Assert.True(viewModel.CanGoNext);
        agentGroup.UnselectAllItemsCommand.Execute(null);
        Assert.Equal("0/2 items", agentGroup.CountText);
        Assert.False(viewModel.CanGoNext);
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
                    ["PrivateText", "NetworkEndpoint"],
                    [
                        new RuntimeStackExportItemDetail("Custom instructions", "Use a concise tone.", "PrivateText", ValueWhenExcluded: "Not exported"),
                        new RuntimeStackExportItemDetail("Provider connections", "OpenAI", "NetworkEndpoint", ValueWhenExcluded: "Not exported", SupportsAskOnImport: true),
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
        Assert.Contains("network", item.SafetyChips);
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
        Assert.Equal("Secret", provider.SensitivityOverride);
        Assert.Equal("Importer will provide this value.", provider.EffectiveValue);
        provider.SelectedExportBehavior = "Include value";
        provider.EditedValue = "OpenAI shared endpoint";
        Assert.False(provider.HasFlagText);
        Assert.Equal("OpenAI shared endpoint", provider.ValueOverride);
        Assert.Null(provider.SensitivityOverride);
        Assert.DoesNotContain("contributor", item.SummaryText, StringComparison.OrdinalIgnoreCase);
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
        };
        await viewModel.InitializeAsync();
        viewModel.NextCommand.Execute(null);
        Assert.Single(viewModel.PackageGroups).SelectAllItemsCommand.Execute(null);
        Assert.Single(Assert.Single(viewModel.PackageGroups).Items).Details[0].EditedValue = "Edited setup detail";
        viewModel.NextCommand.Execute(null);

        await viewModel.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(runtimeApiClient.LastExportRequest);
        Assert.Equal("team-stack", runtimeApiClient.LastExportRequest.StackId);
        Assert.Equal("Team Stack", runtimeApiClient.LastExportRequest.Name);
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
                [
                    new RuntimeStackImportActionDescriptor("profile", "agent", "Agent profile", "Profile", DefaultSelected: false, "Import profile."),
                ],
                [new RuntimeStackRequiredInputDescriptor("api-key", "agent", "Secret", "API key", true, "Provider key.")],
                [],
                [],
                []),
            ImportResponse = new RuntimeStackImportResponse(
                true,
                [new RuntimeStackImportedItemDescriptor("profile", "agent", "Agent profile", "Profile")],
                new Dictionary<string, string>(),
                [],
                []),
        };
        var viewModel = new UseStackWizardViewModel(
            library,
            stack,
            runtimeApiClient,
            new RegistryPackageInstallService(),
            (_, _) => Task.CompletedTask,
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
        Assert.Equal("secret-local-value", runtimeApiClient.LastImportRequest.InputValues["api-key"]);
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
                [new RuntimeStackImportActionDescriptor("profile", "agent", "Agent profile", "Profile", DefaultSelected: false, "Import profile.")],
                [],
                [],
                [],
                []),
            ImportResponse = new RuntimeStackImportResponse(
                true,
                [new RuntimeStackImportedItemDescriptor("profile", "agent", "Agent profile", "Profile")],
                new Dictionary<string, string>(),
                [],
                []),
            InstallPackagesFromPathsResponse = SuccessPackageOperation() with
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
            _ => registryClient,
            "https://registry.example/");

        await viewModel.InitializeAsync();

        var packageRow = Assert.Single(viewModel.PackageRows);
        Assert.Equal("Will install 1.2.0", packageRow.StatusText);
        Assert.True(viewModel.CanApply);

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(runtimeApiClient.LastInstallBatchRequest);
        var installItem = Assert.Single(runtimeApiClient.LastInstallBatchRequest.Items);
        Assert.Contains("sunder.package.agent.1.2.0.sunderpkg", installItem.PackagePath, StringComparison.Ordinal);
        Assert.Equal(["install", "lifecycle:sunder.package.agent", "preview", "import"], runtimeApiClient.Events);
        Assert.Equal(["agent-profile"], runtimeApiClient.LastImportRequest?.SelectedFragmentIds);
        Assert.Equal(["profile"], runtimeApiClient.LastImportRequest?.SelectedActionIds);
    }

    private static StacksWindowViewModel CreateViewModel(
        string root,
        FakeRegistryApiClient registryClient,
        LocalStackLibraryService? library = null,
        Func<Uri, RegistryAuthToken?>? tokenProvider = null,
        FakeRuntimeApiClient? runtimeApiClient = null)
        => new(
            library ?? new LocalStackLibraryService(Path.Combine(root, "library")),
            new FakeStackArchivePicker(),
            runtimeApiClient ?? new FakeRuntimeApiClient(),
            registryClientFactory: _ => registryClient,
            registryTokenProvider: tokenProvider ?? (_ => null));

    private static RegistryStackSummary CreateRegistryStackSummary(string stackId, string name)
        => new(
            stackId,
            name,
            "A shared Stack.",
            PackageCount: 1,
            FragmentCount: 0,
            new RegistryStackSafety(false, false, false, true, false, false, false),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow);

    private static RegistryStackDetails CreateRegistryStackDetails(string stackId, string name)
        => new(
            stackId,
            name,
            "A shared Stack.",
            [new RegistryStackPackageRequirement("sunder.package.agent", "latest", null, "1.0.0", true)],
            [new RegistryStackFragmentSummary("agent-profile", "sunder.package.agent", "sunder.package.agent.profile", "sunder.package.agent/profile", 1, "Agent profile", "Profile settings.", true)],
            [new RegistryStackRequiredInput("api-key", "Secret", "API key", "Provider key.", true)],
            new RegistryStackSafety(false, false, false, true, false, false, false),
            new RegistryStackArtifact("", 0, $"https://registry.example/api/stacks/{stackId}/download"),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow);

    private static RuntimeStackExportItemDescriptor CreateExportItem(
        string ownerPackageId,
        string itemId,
        string displayName,
        IReadOnlyList<string>? sensitivities = null,
        IReadOnlyList<RuntimeStackExportItemDetail>? details = null,
        string kind = "Configuration")
        => new(
            ownerPackageId + ".contributor",
            ownerPackageId,
            itemId,
            displayName,
            kind,
            DefaultSelected: true,
            sensitivities ?? [],
            "Exportable setup item.",
            details ?? []);

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
                    SourceItemId = "profile",
                    OwnerPackageId = "sunder.package.agent",
                    ContributorId = "agent",
                    SchemaId = "agent.profile",
                    SchemaVersion = 1,
                    Kind = "agent-profile",
                    DisplayName = includeDisplayDetails ? "Blender Artist" : "Agent profile",
                    Description = "Profile settings.",
                    DefaultSelected = false,
                    PayloadPath = "payload/fragments/agent-profile.json",
                    RequiredInputs = includeRequiredInput
                        ? [new SunderStackRequiredInputManifest { InputId = "api-key", Kind = "Secret", Label = "API key", Required = true }]
                        : [],
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
                    Safety = new SunderStackSafetyManifest(),
                },
            ];
        }

        await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
        {
            SchemaVersion = 1,
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
            RequiredInputs = [],
            Safety = new SunderStackSafetyManifest
            {
                ContainsPrivateText = true,
            },
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

        public string? StackDownloadSourcePath { get; init; }

        public string? LastStackSearchQuery { get; private set; }

        public string? LastDownloadedStackId { get; private set; }

        public string? LastPublishToken { get; private set; }

        public string? LastPublishPath { get; private set; }

        public string? LastDeleteStackId { get; private set; }

        public string? LastDeleteToken { get; private set; }

        public RegistryStackManagementOperationResponse DeleteStackResponse { get; init; } = new(true, "Deleted Stack.", []);

        public IReadOnlyList<RegistryResolveInstallPlanResponse> InstallPlanResponses { get; init; } = [new(true, [], [], [], [])];

        public List<RegistryResolveInstallPlanRequest> InstallPlanRequests { get; } = [];

        private int _installPlanResponseIndex;

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);

        public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageDetails?>(null);

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
            => Task.FromResult<RegistryPackageVersionDetails?>(null);

        public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken cancellationToken = default)
        {
            LastStackSearchQuery = query;
            return Task.FromResult<IReadOnlyList<RegistryStackSummary>>(StackSearchResults.Skip(skip).Take(take).ToArray());
        }

        public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default)
            => Task.FromResult(StackDetails);

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

        public Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default)
        {
            LastDownloadedStackId = stackId;
            if (string.IsNullOrWhiteSpace(StackDownloadSourcePath))
            {
                throw new InvalidOperationException("No Stack download source configured.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(StackDownloadSourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
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

        public void Dispose()
        {
        }
    }

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        public RuntimeStackExportDiscoveryResponse ExportDiscoveryResponse { get; init; } = new([], [], []);

        public RuntimeStackImportPreviewResponse ImportPreviewResponse { get; init; } = new(true, [], [], [], [], []);

        public RuntimeStackImportResponse ImportResponse { get; init; } = new(true, [], new Dictionary<string, string>(), [], []);

        public PackageOperationResult InstallPackagesFromPathsResponse { get; init; } = SuccessPackageOperation();

        public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; init; } = [];

        public IReadOnlyList<SessionPackageDescriptor> SessionPackages { get; init; } = [];

        public IReadOnlyList<InstalledPackageDescriptor> InstalledPackages { get; init; } = [];

        public RuntimeStackExportRequest? LastExportRequest { get; private set; }

        public RuntimeStackImportRequest? LastImportRequest { get; private set; }

        public PackageInstallBatchFromPathRequest? LastInstallBatchRequest { get; private set; }

        public List<string> Events { get; } = [];

        public bool WriteArchiveOnExport { get; init; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActivePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(SessionPackages);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>([]);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(InstalledPackages);

        public Uri CreatePackageAssetUri(string packageId, string assetPath) => new($"file:///packages/{packageId}/{assetPath}");

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> InstallPackagesFromPathsAsync(PackageInstallBatchFromPathRequest request, CancellationToken cancellationToken = default)
        {
            LastInstallBatchRequest = request;
            Events.Add("install");
            return Task.FromResult(InstallPackagesFromPathsResponse);
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult(Success());

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageLifecycleOperationResult(true, "ok", [], [], [], [], []));

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageConfigurationSchemaDescriptor>>([]);

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult<PackageConfigurationValuesResponse?>(null);

        public Task SavePackageConfigurationValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
            if (WriteArchiveOnExport)
            {
                var selectedPackages = ExportDiscoveryResponse.Items
                    .Where(item => request.SelectedItems.Any(selected => string.Equals(selected.ContributorId, item.ContributorId, StringComparison.Ordinal)
                                                               && string.Equals(selected.ItemId, item.ItemId, StringComparison.Ordinal)))
                    .Select(item => item.OwnerPackageId)
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
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Packages = selectedPackages,
                    Fragments = [],
                    RequiredInputs = [],
                    Safety = new SunderStackSafetyManifest(),
                }, request.OutputPath, cancellationToken: cancellationToken);
            }

            return new RuntimeStackExportResponse(true, request.OutputPath, [], []);
        }

        public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken cancellationToken = default)
        {
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
    }

    private static PackageOperationResult SuccessPackageOperation()
        => new(true, "ok", RuntimeSessionApplied: true, RequiresAppRestart: false, [], []);
}
