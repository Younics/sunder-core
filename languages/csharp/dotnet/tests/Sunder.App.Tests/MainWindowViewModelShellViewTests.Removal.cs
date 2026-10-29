using Avalonia.Controls;
using Avalonia.Headless;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Xunit;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;

namespace Sunder.App.Tests;

public sealed partial class MainWindowViewModelShellViewTests
{
    [Theory]
    [InlineData(RailPlacement.LeftTop)]
    [InlineData(RailPlacement.RightTop)]
    [InlineData(RailPlacement.LeftBottom)]
    [InlineData(RailPlacement.RightBottom)]
    public async Task CompletedStartupCatalog_RemovesLegacySelectionAndCollapsesOnlyItsRegion(RailPlacement placement)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SplitterTestApplication));
        await ui.Dispatch(async () =>
        {
            var root = CreateTempDirectory();
            var views = FiveRegionViews();
            var state = FiveRegionState(views);
            state.LeftPanelWidth = 470;
            state.RightPanelWidth = 630;
            state.TopRowHeightRatio = 0.63;
            state.BottomSplitRatio = 0.27;
            var removedId = "view." + placement;
            state.ViewPackageIds.Remove(removedId); // Legacy version-2 record.
            var remaining = views.Where(view => view.ViewId != removedId).ToArray();
            var package = PackageWithViews(remaining);
            var composed = new ShellCompositionService().Compose([package], state, null, [], [],
                ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot,
                ShellCompositionService.GetUnavailablePackageIds([package], [package with { Views = [] }]));
            using (var harness = CreateRestorationHarness(root, state, composed.PackageViews.ToArray(), preserveState: true))
            {
                var workspace = new ShellWorkspace { DataContext = harness.ViewModel };
                var window = new Window { Width = 2000, Height = 1000, Content = workspace };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    foreach (var other in ShellPlacementCatalog.All.Where(region => region != RailPlacement.Middle))
                        Assert.Equal(other != placement, RegionPanel(harness.ViewModel, other).IsDockLayoutVisible);
                    var top = workspace.FindControl<Grid>("TopContentGrid")!;
                    var bottom = workspace.FindControl<Grid>("BottomContentGrid")!;
                    if (placement is RailPlacement.LeftTop or RailPlacement.RightTop)
                    {
                        var index = placement == RailPlacement.LeftTop ? 0 : 4;
                        Assert.Equal(0, top.ColumnDefinitions[index].ActualWidth);
                        Assert.Equal(0, top.ColumnDefinitions[index == 0 ? 1 : 3].ActualWidth);
                    }
                    else
                    {
                        Assert.Equal(0, bottom.ColumnDefinitions[placement == RailPlacement.LeftBottom ? 0 : 2].ActualWidth);
                        Assert.Equal(0, bottom.ColumnDefinitions[1].ActualWidth);
                    }
                    Assert.Equal("view.Middle", harness.ViewModel.MiddlePanel.ActiveViewId);
                    await harness.ViewModel.FlushShellStateAsync();
                }
                finally { window.Close(); }
            }
            using var service = new ShellStateService(Path.Combine(root, "shell-state.json"));
            var saved = service.Load();
            Assert.Null(ShellSelectionState.GetSelectedViewId(saved, placement));
            Assert.False(saved.ViewPlacements.ContainsKey(removedId));
            Assert.False(saved.ViewOrder.ContainsKey(removedId));
            Assert.False(saved.ViewPackageIds.ContainsKey(removedId));
            Assert.Equal(470, saved.LeftPanelWidth);
            Assert.Equal(630, saved.RightPanelWidth);
            Assert.Equal(0.63, saved.TopRowHeightRatio);
            Assert.Equal(0.27, saved.BottomSplitRatio);
            new ShellCompositionService().Compose([package], saved, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
            using var reopened = CreateRestorationHarness(root, saved, remaining);
            Assert.False(RegionPanel(reopened.ViewModel, placement).IsDockLayoutVisible);
            return true;
        }, default);
    }

    [Fact]
    public async Task CompletedEmptyCatalog_UsesWelcomeAndCollapsesAllSideRegions()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SplitterTestApplication));
        await ui.Dispatch(async () =>
        {
            var state = FiveRegionState(FiveRegionViews());
            new ShellCompositionService().Compose([], state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
            using var harness = CreateRestorationHarness(CreateTempDirectory(), state, []);
            var workspace = new ShellWorkspace { DataContext = harness.ViewModel };
            var window = new Window { Width = 1600, Height = 900, Content = workspace };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.False(harness.ViewModel.HasMiddleSelection);
                Assert.True(harness.ViewModel.MiddlePanel.ShowFallbackLines);
                Assert.Equal("WELCOME", harness.ViewModel.MiddlePanel.Title);
                var geometry = ReadGeometry(workspace);
                Assert.Equal(0, geometry[0]);
                Assert.Equal(0, geometry[2]);
                Assert.Equal(0, geometry[4]);
                Assert.Empty(state.ViewPlacements);
                Assert.Empty(state.ViewOrder);
                Assert.Empty(state.ViewPackageIds);
                await harness.ViewModel.FlushShellStateAsync();
                using var service = new ShellStateService(harness.StatePath);
                Assert.Null(service.Load().SelectedMiddleViewId);
                return true;
            }
            finally { window.Close(); }
        }, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleRemoval_PreparesOrderedMiddleReplacementAndCommitsCleanupAtomically(bool cancel)
    {
        var root = CreateTempDirectory();
        var folder = CreateAppPackageSource(root, "agent");
        var gatePath = Path.Combine(root, "replacement-navigation");
        File.WriteAllText(Path.Combine(folder, ShellLifecycleTestPackageModule.NavigationGatePathFileName), gatePath);
        var package = CreateActiveAgentPackage();
        var composition = new ShellCompositionService();
        var state = new ShellState
        {
            HasInitializedLayout = true,
            SelectedMiddleViewId = "dev-output.middle",
            SelectedLeftBottomViewId = "dev-output.bottom",
            ViewPlacements = new Dictionary<string, RailPlacement>
            {
                ["dev-output.middle"] = RailPlacement.Middle, ["dev-output.bottom"] = RailPlacement.LeftBottom,
                ["agent.chat"] = RailPlacement.Middle, ["agent.workspaces"] = RailPlacement.Middle,
            },
            ViewPackageIds = new Dictionary<string, string>
            {
                ["dev-output.middle"] = "dev-output", ["dev-output.bottom"] = "dev-output",
            },
            ViewOrder = new Dictionary<string, int> { ["agent.chat"] = 1, ["agent.workspaces"] = 0 },
            HiddenHotbarViewIds = ["agent.subsessions"],
        };
        var current = composition.Compose([package], state, null, [], [], ShellNormalizationPolicy.SafeModePreserveLayout).PackageViews;
        ShellPackageView RemovedView(string id, RailPlacement placement)
            => new(id, "dev-output", "Development output", "1.0.0", id, "D", placement,
                PackageReadinessState.Ready, ShowInHotbarByDefault: true);
        var host = CreatePackageViewHostService();
        using var harness = CreateHarness(root, new ThrowingRuntimeApiClientFactory(), host, host,
            deferInitialHostedViews: true, initialState: state,
            initialViews: [.. current, RemovedView("dev-output.middle", RailPlacement.Middle), RemovedView("dev-output.bottom", RailPlacement.LeftBottom)]);
        using var cancellation = new CancellationTokenSource();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder);
        var snapshot = CreateRuntimeSnapshot([package with { Views = [] }], [source]);
        var apply = harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(snapshot, [source], cancellationToken: cancellation.Token);
        try
        {
            await WaitForConditionAsync(() => File.Exists(gatePath + ".started"));
            Assert.False(apply.IsCompleted);
            Assert.Equal("dev-output.middle", state.SelectedMiddleViewId);
            Assert.Equal("dev-output.bottom", state.SelectedLeftBottomViewId);
            Assert.True(harness.ViewModel.LeftBottomPanel.IsDockLayoutVisible);
            if (cancel) cancellation.Cancel();
        }
        finally { File.WriteAllText(gatePath + ".release", ""); }
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("dev-output.middle", state.SelectedMiddleViewId);
            Assert.True(state.ViewPlacements.ContainsKey("dev-output.middle"));
            Assert.True(harness.ViewModel.LeftBottomPanel.IsDockLayoutVisible);
        }
        else
        {
            await apply.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("agent.workspaces", state.SelectedMiddleViewId);
            Assert.Equal("agent.workspaces", harness.ViewModel.MiddlePanel.ActiveViewId);
            var boundary = Assert.IsType<HostedPackageViewBoundary>(harness.ViewModel.MiddlePanel.HostedView);
            Assert.Equal("agent.workspaces", GetViewId(Assert.IsAssignableFrom<Control>(boundary.HostedView)));
            Assert.False(harness.ViewModel.LeftBottomPanel.IsDockLayoutVisible);
            Assert.Null(state.SelectedLeftBottomViewId);
            Assert.DoesNotContain(state.ViewPlacements.Keys, id => id.StartsWith("dev-output.", StringComparison.Ordinal));

            // A release can leave the same package supplied by another owner or
            // an installed source. Reconciliation alone must not close its view.
            await harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(snapshot, [source]);
            Assert.Equal("agent.workspaces", harness.ViewModel.MiddlePanel.ActiveViewId);
        }
        harness.ViewModel.CompleteInitialReveal();
        await harness.ViewModel.FlushShellStateAsync();
        using var service = new ShellStateService(harness.StatePath);
        var saved = service.Load();
        Assert.Equal(cancel ? "dev-output.middle" : "agent.workspaces", saved.SelectedMiddleViewId);
        Assert.Equal(cancel, saved.ViewPlacements.ContainsKey("dev-output.middle"));
        Assert.Equal(cancel, saved.ViewPackageIds.ContainsKey("dev-output.bottom"));
    }

    private static ActivePackageDescriptor PackageWithViews(ShellPackageView[] views)
        => new("agent", "Agent", "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready,
            views.Select(view => new PackageViewDescriptor(view.ViewId, view.PackageId, view.Title, null,
                view.Placement.ToString(), view.ShowInHotbarByDefault)).ToArray());

    private static ShellPanelViewModel RegionPanel(MainWindowViewModel viewModel, RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => viewModel.LeftTopPanel,
            RailPlacement.RightTop => viewModel.RightTopPanel,
            RailPlacement.LeftBottom => viewModel.LeftBottomPanel,
            RailPlacement.RightBottom => viewModel.RightBottomPanel,
            _ => viewModel.MiddlePanel,
        };
}
