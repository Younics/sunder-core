using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Headless;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.Views;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Xunit;
using static Sunder.App.Tests.TestSupport.TestPaths;

namespace Sunder.App.Tests;

public sealed partial class MainWindowViewModelShellViewTests
{
    [Fact]
    public async Task FiveRegions_RestoreViewsAndActualGeometryAfterFreshViewModelAndStateLoad()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SplitterTestApplication));
        await ui.Dispatch(async () =>
        {
            var root = CreateTempDirectory();
            var views = FiveRegionViews();
            var state = FiveRegionState(views);
            state.LeftPanelWidth = 510;
            state.RightPanelWidth = 1640;
            state.TopRowHeightRatio = 0.67;
            state.BottomSplitRatio = 0.26;
            double[] before;
            using (var first = CreateRestorationHarness(root, state, views, preserveState: true))
            {
                var workspace = new ShellWorkspace { DataContext = first.ViewModel };
                var window = new Window { Width = 3000, Height = 1000, Content = workspace };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    before = ReadGeometry(workspace);
                    await first.ViewModel.FlushShellStateAsync();
                }
                finally { window.Close(); }
            }
            using var service = new ShellStateService(Path.Combine(root, "shell-state.json"));
            var reloaded = service.Load();
            using var second = CreateRestorationHarness(root, reloaded, views);
            var restoredWorkspace = new ShellWorkspace { DataContext = second.ViewModel };
            var restoredWindow = new Window { Width = 3000, Height = 1000, Content = restoredWorkspace };
            restoredWindow.Show();
            try
            {
                restoredWindow.UpdateLayout();
                var after = ReadGeometry(restoredWorkspace);
                for (var i = 0; i < before.Length; i++) Assert.InRange(Math.Abs(before[i] - after[i]), 0, 1);
                Assert.Equal(1640, second.ViewModel.RightPanelWidth);
                Assert.Equal(views.Select(view => view.ViewId).Order(),
                    second.ViewModel.ListHotbarViews().Where(view => view.IsOpen).Select(view => view.ViewId).Order());
            }
            finally { restoredWindow.Close(); }
            return true;
        }, default);
    }

    [Fact]
    public async Task ClosedMiddleAndBottomRegionsStayClosedAfterReload()
    {
        var root = CreateTempDirectory();
        var views = FiveRegionViews();
        using (var first = CreateRestorationHarness(root, FiveRegionState(views), views, preserveState: true))
        {
            Assert.True(first.ViewModel.ClosePackageViewPanel("view.Middle"));
            Assert.True(first.ViewModel.ClosePackageViewPanel("view.LeftBottom"));
            Assert.True(first.ViewModel.ClosePackageViewPanel("view.RightBottom"));
            await first.ViewModel.FlushShellStateAsync();
        }
        using var service = new ShellStateService(Path.Combine(root, "shell-state.json"));
        var state = service.Load();
        // Exercise composition as startup does, not just JSON deserialization.
        ShellStateNormalizer.Normalize(state, views, ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        using var second = CreateRestorationHarness(root, state, views);
        Assert.False(second.ViewModel.HasMiddleSelection);
        Assert.False(second.ViewModel.HasAnyBottomPanelContent);
        Assert.Null(state.SelectedMiddleViewId);
        Assert.Null(state.SelectedLeftBottomViewId);
        Assert.Null(state.SelectedRightBottomViewId);
    }

    [Fact]
    public async Task IncompleteCoreShellCatalogKeepsSelectionsAndGeometryThroughDeferredStartupAndRecover()
    {
        var root = CreateTempDirectory();
        var views = FiveRegionViews();
        var state = FiveRegionState(views);
        state.ViewOrder["view.Middle"] = 7;
        ShellStateNormalizer.Normalize(state, [], ShellNormalizationPolicy.SafeModePreserveLayout);
        var host = CreateRegisteredPackageViewHostService();
        using (var unavailable = CreateHarness(root, new ThrowingRuntimeApiClientFactory(), host, host,
            deferInitialHostedViews: true, initialState: state, initialViews: [], preserveStateForRestart: true))
        {
            Assert.True(unavailable.ViewModel.LeftTopPanel.IsDockLayoutVisible);
            Assert.True(unavailable.ViewModel.RightTopPanel.IsDockLayoutVisible);
            Assert.True(unavailable.ViewModel.LeftBottomPanel.IsDockLayoutVisible);
            Assert.True(unavailable.ViewModel.RightBottomPanel.IsDockLayoutVisible);
            await unavailable.ViewModel.ActivateDeferredInitialHostedViewsAsync();
            unavailable.ViewModel.CompleteInitialReveal();
            await unavailable.ViewModel.FlushShellStateAsync();
        }
        using var service = new ShellStateService(Path.Combine(root, "shell-state.json"));
        var saved = service.Load();
        Assert.Equal("view.Middle", saved.SelectedMiddleViewId);
        Assert.Equal(7, saved.ViewOrder["view.Middle"]);
        using var recovered = CreateRestorationHarness(root, saved, views);
        Assert.Equal(5, recovered.ViewModel.ListHotbarViews().Count(view => view.IsOpen));
        Assert.Equal("view.LeftBottom", recovered.ViewModel.LeftBottomPanel.ActiveViewId);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("rows")]
    [InlineData("bottom")]
    public async Task EverySplitter_UsesStablePointerMovementAndReversesImmediatelyAtClamp(string region)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SplitterTestApplication));
        await ui.Dispatch(async () =>
        {
            var views = FiveRegionViews();
            using var harness = CreateRestorationHarness(CreateTempDirectory(), FiveRegionState(views), views);
            var workspace = new ShellWorkspace { DataContext = harness.ViewModel };
            foreach (var id in new[] { "LeftColumnGridSplitter", "RightColumnGridSplitter", "BottomRowGridSplitter", "BottomColumnGridSplitter" })
                workspace.FindControl<ShellGridSplitter>(id)!.Template = new Avalonia.Controls.Templates.FuncControlTemplate<GridSplitter>(
                    (_, _) => new Border { Background = Avalonia.Media.Brushes.Transparent });
            var window = new Window { Width = 2800, Height = 1000, Content = workspace };
            window.Show();
            try
            {
                window.UpdateLayout();
                var name = region switch
                {
                    "left" => "LeftColumnGridSplitter", "right" => "RightColumnGridSplitter",
                    "rows" => "BottomRowGridSplitter", _ => "BottomColumnGridSplitter",
                };
                var splitter = workspace.FindControl<ShellGridSplitter>(name)!;
                var direction = region == "rows" ? new Vector(0, 1) : new Vector(region == "right" ? -1 : 1, 0);
                var start = splitter.TranslatePoint(new Point(2, 2), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                var pointer = start + direction * 10;
                window.MouseMove(pointer);
                window.UpdateLayout();
                var original = ReadSplitExtent(workspace, region);
                pointer += direction * 20;
                window.MouseMove(pointer);
                window.UpdateLayout();
                Assert.InRange(Math.Abs(ReadSplitExtent(workspace, region) - original - 20), 0, 1);
                pointer += direction * 5000;
                window.MouseMove(pointer);
                window.UpdateLayout();
                var maximum = ReadSplitExtent(workspace, region);
                pointer -= direction * 10;
                window.MouseMove(pointer);
                window.UpdateLayout();
                Assert.InRange(Math.Abs(ReadSplitExtent(workspace, region) - maximum + 10), 0, 1);
                window.MouseUp(pointer, MouseButton.Left);
                splitter.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = region == "rows" ? Key.Up : region == "right" ? Key.Right : Key.Left,
                });
                window.UpdateLayout();
                Assert.InRange(Math.Abs(ReadSplitExtent(workspace, region) - maximum + 10 + splitter.KeyboardIncrement), 0, 1);
                await harness.ViewModel.FlushShellStateAsync();
                return true;
            }
            finally { window.Close(); }
        }, default);
    }

    private static double ReadSplitExtent(ShellWorkspace workspace, string region)
    {
        var geometry = ReadGeometry(workspace);
        return region switch { "left" => geometry[0], "right" => geometry[2], "rows" => geometry[3], _ => geometry[5] };
    }

    private static double[] ReadGeometry(ShellWorkspace workspace)
    {
        var top = workspace.FindControl<Grid>("TopContentGrid")!;
        var shell = workspace.FindControl<Grid>("ShellContentGrid")!;
        var bottom = workspace.FindControl<Grid>("BottomContentGrid")!;
        return [top.ColumnDefinitions[0].ActualWidth, top.ColumnDefinitions[2].ActualWidth,
            top.ColumnDefinitions[4].ActualWidth, shell.RowDefinitions[0].ActualHeight,
            shell.RowDefinitions[2].ActualHeight, bottom.ColumnDefinitions[0].ActualWidth, bottom.ColumnDefinitions[2].ActualWidth];
    }

    private static ShellPackageView[] FiveRegionViews()
        => ShellPlacementCatalog.All.Select(placement => new ShellPackageView(
            "view." + placement, "agent", "Agent", "1.0.0", placement.ToString(), "A", placement,
            PackageReadinessState.Ready, ShowInHotbarByDefault: true)).ToArray();

    private static ShellState FiveRegionState(ShellPackageView[] views)
    {
        var state = new ShellState { HasInitializedLayout = true };
        foreach (var view in views)
        {
            state.ViewPlacements[view.ViewId] = view.Placement;
            state.ViewPackageIds[view.ViewId] = view.PackageId;
            state.ViewOrder[view.ViewId] = 0;
            ShellSelectionState.SetSelectedViewId(state, view.Placement, view.ViewId);
        }
        return state;
    }

    private static MainWindowViewModelHarness CreateRestorationHarness(string root, ShellState state, ShellPackageView[] views, bool preserveState = false)
    {
        var host = CreateRegisteredPackageViewHostService(views.Select(view => (view.PackageId, view.ViewId)).ToArray());
        return CreateHarness(root, new ThrowingRuntimeApiClientFactory(), host, host,
            initialState: state, initialViews: views, preserveStateForRestart: preserveState);
    }
}
