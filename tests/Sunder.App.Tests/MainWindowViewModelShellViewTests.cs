using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class MainWindowViewModelShellViewTests
{
    [Fact]
    public async Task OpenPackageViewPanelAsync_AddsHiddenViewToHotbarAndOpensPanel()
    {
        using var harness = CreateHarness();

        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));

        var opened = await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions", new Dictionary<string, string?>
        {
            ["sessionId"] = Guid.NewGuid().ToString("N"),
        });

        Assert.True(opened);
        Assert.True(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.True(harness.ViewModel.HasRightTopPanelContent);
        Assert.Contains(harness.ViewModel.ListHotbarViews(), view => view.ViewId == "agent.subsessions" && view.IsOpen);
    }

    [Fact]
    public async Task ClosePackageViewPanelAsync_ClosesPanelWithoutRemovingHotbarItem()
    {
        using var harness = CreateHarness();
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");

        var closed = harness.ViewModel.ClosePackageViewPanel("agent.subsessions");

        Assert.True(closed);
        Assert.True(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.False(harness.ViewModel.HasRightTopPanelContent);
        Assert.Contains(harness.ViewModel.ListHotbarViews(), view => view.ViewId == "agent.subsessions" && !view.IsOpen);
    }

    [Fact]
    public void ClosePackageViewPanel_WhenOnlyMiddleViewIsSelected_ClearsMiddleSelection()
    {
        using var harness = CreateHarness();

        var closed = harness.ViewModel.ClosePackageViewPanel("agent.chat");

        Assert.True(closed);
        Assert.False(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.MiddlePanel.HasHostedView);
        Assert.Contains(harness.ViewModel.ListHotbarViews(), view => view.ViewId == "agent.chat" && !view.IsOpen);
    }

    [Fact]
    public async Task ClosePackageViewPanel_WhenClosingFirstMiddleView_SelectsNextMiddleView()
    {
        using var harness = CreateHarness();
        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.Middle, 1);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));

        var closed = harness.ViewModel.ClosePackageViewPanel("agent.chat");

        var hotbarViews = harness.ViewModel.ListHotbarViews();
        Assert.True(closed);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.chat" && !view.IsOpen);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.workspaces" && view.Placement == PackageHotbarPlacement.Middle && view.IsOpen);
    }

    [Fact]
    public async Task RemovePackageViewFromHotbar_HidesViewAndClosesPanel()
    {
        using var harness = CreateHarness();
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");

        var removed = harness.ViewModel.RemovePackageViewFromHotbar("agent.subsessions");

        Assert.True(removed);
        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.False(harness.ViewModel.HasRightTopPanelContent);
        Assert.DoesNotContain(harness.ViewModel.ListHotbarViews(), view => view.ViewId == "agent.subsessions");
    }

    [Fact]
    public async Task ReloadPackageViewAsync_ReplacesOpenHostedView()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService();
        using var harness = CreateHarness(rootPath, new ThrowingRuntimeApiClientFactory(), packageViewHostService, packageViewHostService);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));
        var originalView = Assert.IsType<DisposablePackageView>(harness.ViewModel.MiddlePanel.HostedView);

        var reloaded = await harness.ViewModel.ReloadPackageViewAsync("agent.chat");
        var reloadedView = Assert.IsType<DisposablePackageView>(harness.ViewModel.MiddlePanel.HostedView);

        Assert.True(reloaded);
        Assert.True(originalView.IsDisposed);
        Assert.NotSame(originalView, reloadedView);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.Contains(harness.ViewModel.ListHotbarViews(), view => view.ViewId == "agent.chat" && view.IsOpen);
    }

    [Fact]
    public void GetPackageViewGroups_IncludesGlyphsAndHotbarState()
    {
        using var harness = CreateHarness();

        var group = Assert.Single(harness.ViewModel.GetPackageViewGroups());

        Assert.Equal("A", group.PackageGlyph);
        Assert.Contains(group.Views, view => view.ViewId == "agent.chat" && view.Glyph == "A" && view.IsInHotbar);
        Assert.Contains(group.Views, view => view.ViewId == "agent.workspaces" && view.Glyph == "W" && view.IsInHotbar);
        Assert.Contains(group.Views, view => view.ViewId == "agent.subsessions" && view.Glyph == "S" && !view.IsInHotbar);
    }

    [Fact]
    public async Task PackageShellViewService_ListHotbarViews_UsesSnapshotWhenCalledOffUiThread()
    {
        var shellViewService = new AppPackageShellViewService();
        using var harness = CreateHarness(shellViewService: shellViewService);

        var initialViews = await Task.Run(() => shellViewService.ListHotbarViews());

        Assert.Contains(initialViews, view => view.ViewId == "agent.chat" && view.IsOpen);
        Assert.False(shellViewService.IsViewInHotbar("agent.subsessions"));

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));

        var updatedViews = await Task.Run(() => shellViewService.ListHotbarViews());
        Assert.True(shellViewService.IsViewInHotbar("agent.subsessions"));
        Assert.Contains(updatedViews, view => view.ViewId == "agent.subsessions" && view.IsOpen);

        harness.ViewModel.Dispose();
        Assert.Empty(shellViewService.ListHotbarViews());
    }

    [Fact]
    public async Task SwitchingPackageViews_DoesNotChangePanelWidths()
    {
        using var harness = CreateHarness();
        var originalLeftWidth = harness.ViewModel.LeftPanelWidth;
        var originalRightWidth = harness.ViewModel.RightPanelWidth;

        await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces");
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces");
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");

        Assert.Equal(originalLeftWidth, harness.ViewModel.LeftPanelWidth);
        Assert.Equal(originalRightWidth, harness.ViewModel.RightPanelWidth);
    }

    [Fact]
    public async Task OpeningAndClosingPackageView_DoesNotChangePanelWidths()
    {
        using var harness = CreateHarness();
        var originalLeftWidth = harness.ViewModel.LeftPanelWidth;
        var originalRightWidth = harness.ViewModel.RightPanelWidth;

        for (var index = 0; index < 5; index++)
        {
            Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));
            Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.subsessions"));
        }

        Assert.Equal(originalLeftWidth, harness.ViewModel.LeftPanelWidth);
        Assert.Equal(originalRightWidth, harness.ViewModel.RightPanelWidth);
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenRuntimeHasNoActivePackages_RemovesLoadedPackageViews()
    {
        using var harness = CreateHarness(new EmptyRuntimeApiClientFactory());

        Assert.Contains(harness.ViewModel.GetPackageViewGroups(), group => group.PackageId == "agent");
        Assert.True(harness.ViewModel.IsViewInHotbar("agent.chat"));

        await harness.ViewModel.ApplyPackageLifecycleChangesAsync();

        Assert.Empty(harness.ViewModel.GetPackageViewGroups());
        Assert.Empty(harness.ViewModel.ListHotbarViews());
        Assert.False(harness.ViewModel.HasMiddleSelection);
        Assert.Equal("No packages loaded", harness.ViewModel.SyncStatusText);
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenPackageRemoved_DisposesCachedPackageViewsOnCallingThread()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var packageViewHostService = CreatePackageViewHostService();
        await packageViewHostService.ApplyPackageDeltaAsync(
            [CreateActiveAgentPackage()],
            [new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder)]);
        using var harness = CreateHarness(rootPath, new EmptyRuntimeApiClientFactory(), packageViewHostService, packageViewHostService);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));
        var hostedView = harness.ViewModel.MiddlePanel.HostedView;
        Assert.NotNull(hostedView);
        var ownerThreadId = Assert.IsType<int>(hostedView.GetType().GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.OwnerThreadId))?.GetValue(hostedView));

        await harness.ViewModel.ApplyPackageLifecycleChangesAsync();

        Assert.True(Assert.IsType<bool>(hostedView.GetType().GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))?.GetValue(hostedView)));
        Assert.Equal(ownerThreadId, Assert.IsType<int>(hostedView.GetType().GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.DisposeThreadId))?.GetValue(hostedView)));
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenImpactedPackageRemainsActive_PreservesShellLayout()
    {
        using var harness = CreateActivePackageHarness();
        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.LeftTop, 0);

        Assert.True(harness.ViewModel.HasLeftTopPanelContent);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));

        await harness.ViewModel.ApplyPackageLifecycleChangesAsync(["agent"]);

        var hotbarViews = harness.ViewModel.ListHotbarViews();
        Assert.True(harness.ViewModel.HasLeftTopPanelContent);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.Equal(333, harness.ViewModel.LeftPanelWidth);
        Assert.Equal(444, harness.ViewModel.RightPanelWidth);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.workspaces" && view.Placement == PackageHotbarPlacement.LeftTop && view.IsOpen);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.chat" && view.Placement == PackageHotbarPlacement.Middle && view.IsOpen);
        Assert.DoesNotContain(hotbarViews, view => view.ViewId == "agent.subsessions");
        Assert.Contains(harness.ViewModel.GetPackageViewGroups(), group => group.PackageId == "agent");
        Assert.Equal("1 package(s) active", harness.ViewModel.SyncStatusText);
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenPackageRepeatedlyReinstalled_DoesNotDuplicateHotbarViews()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var packageSource = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var runtimeApiClientFactory = new MutableRuntimeApiClientFactory
        {
            ActivePackages = [CreateActiveAgentPackage()],
            PackageSources = [packageSource],
        };
        var packageViewHostService = CreatePackageViewHostService();
        using var harness = CreateHarness(rootPath, runtimeApiClientFactory, packageViewHostService, packageViewHostService);

        for (var index = 0; index < 3; index++)
        {
            runtimeApiClientFactory.ActivePackages = [];
            runtimeApiClientFactory.PackageSources = [];
            await harness.ViewModel.ApplyPackageLifecycleChangesAsync(["agent"]);

            runtimeApiClientFactory.ActivePackages = [CreateActiveAgentPackage()];
            runtimeApiClientFactory.PackageSources = [packageSource];
            await harness.ViewModel.ApplyPackageLifecycleChangesAsync(["agent"]);
        }

        var hotbarViewIds = harness.ViewModel.ListHotbarViews()
            .Select(view => view.ViewId)
            .ToArray();

        Assert.Equal(hotbarViewIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(), hotbarViewIds.Length);
        Assert.Single(hotbarViewIds, viewId => string.Equals(viewId, "agent.chat", StringComparison.OrdinalIgnoreCase));
        Assert.Single(hotbarViewIds, viewId => string.Equals(viewId, "agent.workspaces", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(hotbarViewIds, viewId => string.Equals(viewId, "agent.subsessions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenCalledConcurrently_SerializesRuntimeRefreshes()
    {
        var gatedRuntime = new GatedPackageLifecycleRuntime();
        var runtimeApiClientFactory = new StaticRuntimeApiClientFactory(
            [],
            [],
            getActivePackagesAsync: gatedRuntime.GetActivePackagesAsync,
            getActivePackageSourcesAsync: gatedRuntime.GetActivePackageSourcesAsync);
        using var harness = CreateHarness(runtimeApiClientFactory);

        var firstRefresh = harness.ViewModel.ApplyPackageLifecycleChangesAsync(["agent"]);
        Assert.Equal(1, gatedRuntime.ActivePackageCallCount);

        var secondRefresh = harness.ViewModel.ApplyPackageLifecycleChangesAsync(["agent"]);
        Assert.Equal(1, gatedRuntime.ActivePackageCallCount);

        gatedRuntime.ReleaseNextActivePackageCall();
        await WaitForConditionAsync(() => gatedRuntime.ActivePackageCallCount == 2);

        gatedRuntime.ReleaseNextActivePackageCall();
        await Task.WhenAll(firstRefresh, secondRefresh);
    }

    [Fact]
    public void MovePackageView_WhenSelectedMiddleViewMovesOut_SelectsFirstRemainingMiddleView()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService();
        using var harness = CreateHarness(rootPath, new ThrowingRuntimeApiClientFactory(), packageViewHostService, packageViewHostService);

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.Middle, 1);
        Assert.Contains(harness.ViewModel.ListHotbarViews(), view => view.ViewId == "agent.workspaces" && view.Placement == PackageHotbarPlacement.Middle && view.IsOpen);

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.RightTop, 0);

        var hotbarViews = harness.ViewModel.ListHotbarViews();
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.chat" && view.Placement == PackageHotbarPlacement.Middle && view.IsOpen);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.workspaces" && view.Placement == PackageHotbarPlacement.RightTop && view.IsOpen);
        Assert.DoesNotContain(hotbarViews, view => view.ViewId == "agent.workspaces" && view.Placement == PackageHotbarPlacement.Middle);
        Assert.True(harness.ViewModel.MiddlePanel.HasHostedView);
        Assert.False(harness.ViewModel.MiddlePanel.ShowFallbackLines);
        Assert.IsType<DisposablePackageView>(harness.ViewModel.MiddlePanel.HostedView);
    }

    [Fact]
    public async Task MovePackageView_WhenMovedDownWithinSameBar_NormalizesTargetIndexAfterRemoval()
    {
        using var harness = CreateHarness();
        Assert.True(await harness.ViewModel.AddPackageViewToHotbarAsync("agent.workspaces", PackageHotbarPlacement.Middle, 1));
        Assert.True(await harness.ViewModel.AddPackageViewToHotbarAsync("agent.subsessions", PackageHotbarPlacement.Middle, 2));

        harness.ViewModel.MovePackageView("agent.chat", RailPlacement.Middle, 2);

        Assert.Equal(
            ["agent.workspaces", "agent.chat", "agent.subsessions"],
            GetMiddleHotbarOrder(harness.ViewModel));
    }

    [Fact]
    public async Task MovePackageView_WhenMovedUpWithinSameBar_PreservesRequestedTargetIndex()
    {
        using var harness = CreateHarness();
        Assert.True(await harness.ViewModel.AddPackageViewToHotbarAsync("agent.workspaces", PackageHotbarPlacement.Middle, 1));
        Assert.True(await harness.ViewModel.AddPackageViewToHotbarAsync("agent.subsessions", PackageHotbarPlacement.Middle, 2));

        harness.ViewModel.MovePackageView("agent.subsessions", RailPlacement.Middle, 1);

        Assert.Equal(
            ["agent.chat", "agent.subsessions", "agent.workspaces"],
            GetMiddleHotbarOrder(harness.ViewModel));
    }

    [Fact]
    public async Task BottomSplitPanelContent_IsTrueOnlyWhenBothBottomPanelsHaveContent()
    {
        using var harness = CreateHarness();

        Assert.False(harness.ViewModel.HasAnyBottomPanelContent);
        Assert.False(harness.ViewModel.HasBottomSplitPanelContent);

        Assert.True(await harness.ViewModel.AddPackageViewToHotbarAsync("agent.chat", PackageHotbarPlacement.LeftBottom, 0, openPanel: true));

        Assert.True(harness.ViewModel.HasAnyBottomPanelContent);
        Assert.False(harness.ViewModel.HasBottomSplitPanelContent);

        Assert.True(await harness.ViewModel.AddPackageViewToHotbarAsync("agent.workspaces", PackageHotbarPlacement.RightBottom, 0, openPanel: true));

        Assert.True(harness.ViewModel.HasAnyBottomPanelContent);
        Assert.True(harness.ViewModel.HasBottomSplitPanelContent);
    }

    [Fact]
    public void CalculateTopColumnWidths_PreservesRequestedSideWidthsWhenSpaceAllows()
    {
        var widths = MainWindow.CalculateTopColumnWidths(
            totalWidth: 1476,
            requestedLeftWidth: 360,
            requestedRightWidth: 360,
            hasLeftPanel: true,
            hasRightPanel: true);

        Assert.Equal(360, widths.LeftWidth);
        Assert.Equal(360, widths.RightWidth);
    }

    [Fact]
    public void CalculateTopColumnWidths_ClampsVisualWidthsWhenMiddleNeedsSpace()
    {
        var widths = MainWindow.CalculateTopColumnWidths(
            totalWidth: 900,
            requestedLeftWidth: 360,
            requestedRightWidth: 360,
            hasLeftPanel: true,
            hasRightPanel: true);

        Assert.True(widths.LeftWidth < 360);
        Assert.True(widths.RightWidth < 360);
        Assert.Equal(widths.LeftWidth, widths.RightWidth);
    }

    [Fact]
    public void ShellLayoutCalculator_CalculatesVerticalWeightsForBottomPanels()
    {
        var noBottom = ShellLayoutCalculator.CalculateVerticalWeights(0.42, hasBottom: false);
        var clampedLow = ShellLayoutCalculator.CalculateVerticalWeights(0, hasBottom: true);
        var clampedHigh = ShellLayoutCalculator.CalculateVerticalWeights(1, hasBottom: true);

        Assert.Equal((1, 0, 0), noBottom);
        Assert.Equal(0.10, clampedLow.TopWeight);
        Assert.Equal(ShellLayoutCalculator.SplitterThickness, clampedLow.SplitterHeight);
        Assert.Equal(0.90, clampedLow.BottomWeight, precision: 10);
        Assert.Equal(0.90, clampedHigh.TopWeight);
        Assert.Equal(0.10, clampedHigh.BottomWeight, precision: 10);
    }

    [Fact]
    public void ShellLayoutCalculator_CalculatesBottomColumnWeightsForVisiblePanels()
    {
        Assert.Equal((1, 0), ShellLayoutCalculator.CalculateBottomColumnWeights(0.35, hasLeftBottom: true, hasRightBottom: false));
        Assert.Equal((0, 1), ShellLayoutCalculator.CalculateBottomColumnWeights(0.35, hasLeftBottom: false, hasRightBottom: true));
        Assert.Equal((0, 0), ShellLayoutCalculator.CalculateBottomColumnWeights(0.35, hasLeftBottom: false, hasRightBottom: false));

        var both = ShellLayoutCalculator.CalculateBottomColumnWeights(1, hasLeftBottom: true, hasRightBottom: true);
        Assert.Equal(0.99, both.LeftWeight);
        Assert.Equal(0.01, both.RightWeight, precision: 10);
    }

    [Fact]
    public async Task ApplyRuntimeAddressAsync_WhenAddressIsInvalid_DoesNotUseRuntimeApiClient()
    {
        using var harness = CreateHarness(new ThrowingRuntimeApiClientFactory());

        harness.ViewModel.RuntimeAddressText = "not a url";
        await harness.ViewModel.ApplyRuntimeAddressCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsRuntimeBusy);
        Assert.True(harness.ViewModel.IsRuntimeRunning);
        Assert.Equal("Runtime address invalid", harness.ViewModel.SystemStatusText);
        Assert.Contains("not a url", harness.ViewModel.RuntimeLastError);
    }

    [Fact]
    public async Task RefreshRuntimeAsync_WhenStatusAndHealthFail_ClearsBusyState()
    {
        var runtimeApiClientFactory = new StaticRuntimeApiClientFactory(
            [],
            [],
            _ => Task.FromException<SystemStatusResponse?>(new InvalidOperationException("status failed")),
            _ => Task.FromException<bool>(new InvalidOperationException("health failed")));
        using var harness = CreateHarness(runtimeApiClientFactory);

        await harness.ViewModel.RefreshRuntimeCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsRuntimeBusy);
        Assert.False(harness.ViewModel.IsRuntimeRunning);
        Assert.False(harness.ViewModel.IsRuntimeReady);
        Assert.Equal("Runtime unavailable", harness.ViewModel.RuntimeStatusText);
        Assert.Equal("Runtime unavailable", harness.ViewModel.SystemStatusText);
        Assert.Equal("status failed", harness.ViewModel.RuntimeLastError);
    }

    private static MainWindowViewModelHarness CreateHarness(
        IRuntimeApiClientFactory? runtimeApiClientFactory = null,
        AppPackageShellViewService? shellViewService = null)
    {
        var rootPath = CreateTempDirectory();
        return CreateHarness(
            rootPath,
            runtimeApiClientFactory ?? new ThrowingRuntimeApiClientFactory(),
            PackageViewHostService.Empty,
            shellViewService: shellViewService);
    }

    private static MainWindowViewModelHarness CreateActivePackageHarness()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var runtimeApiClientFactory = new StaticRuntimeApiClientFactory(
            [CreateActiveAgentPackage()],
            [new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder)]);
        var packageViewHostService = CreatePackageViewHostService();
        return CreateHarness(rootPath, runtimeApiClientFactory, packageViewHostService, packageViewHostService);
    }

    private static MainWindowViewModelHarness CreateHarness(
        string rootPath,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        PackageViewHostService packageViewHostService,
        PackageViewHostService? disposablePackageViewHostService = null,
        AppPackageShellViewService? shellViewService = null)
    {
        var state = new ShellState
        {
            HasInitializedLayout = true,
            ViewPlacements = new Dictionary<string, RailPlacement>
            {
                ["agent.chat"] = RailPlacement.Middle,
                ["agent.workspaces"] = RailPlacement.RightTop,
                ["agent.subsessions"] = RailPlacement.RightTop,
            },
            LeftPanelWidth = 333,
            RightPanelWidth = 444,
            HiddenHotbarViewIds = ["agent.subsessions"],
            SelectedMiddleViewId = "agent.chat",
        };
        var snapshot = new ShellSnapshot(
            [
                new ShellPackageView("agent.chat", "agent", "Agent", "1.0.0", "Chat", "A", RailPlacement.Middle, PackageReadinessState.Ready, ShowInHotbarByDefault: true, PackageGlyph: "A"),
                new ShellPackageView("agent.workspaces", "agent", "Agent", "1.0.0", "Workspaces", "W", RailPlacement.RightTop, PackageReadinessState.Ready, ShowInHotbarByDefault: true, PackageGlyph: "A"),
                new ShellPackageView("agent.subsessions", "agent", "Agent", "1.0.0", "Subsessions", "S", RailPlacement.RightTop, PackageReadinessState.Ready, ShowInHotbarByDefault: false, PackageGlyph: "A"),
            ],
            state,
            StartupWarnings: [],
            StartupErrors: [],
            SystemStatusText: "Runtime Ready",
            SyncStatusText: "3 package view(s) active");
        var statePath = Path.Combine(rootPath, "shell-state.json");
        var viewModel = new MainWindowViewModel(
            new TestWindowLauncher(),
            new ShellStateService(statePath),
            snapshot,
            packageViewHostService,
            new RuntimeConnectionState(AppStartupOptions.DefaultRuntimeUrl),
            runtimeApiClientFactory,
            new RuntimeHostProcessManager(new AppStartupOptions()),
            new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow),
            new NotificationCenterService(Path.Combine(rootPath, "notifications.json")),
            shellViewService);
        return new MainWindowViewModelHarness(viewModel, rootPath, disposablePackageViewHostService);
    }

    private static ActivePackageDescriptor CreateActiveAgentPackage()
        => new(
            "agent",
            "Agent",
            "1.0.0",
            null,
            true,
            PackageReadinessState.Ready,
            [
                new PackageViewDescriptor("agent.chat", "agent", "Chat", new PackageIconDescriptor("A", AssetPath: null), "middle"),
                new PackageViewDescriptor("agent.workspaces", "agent", "Workspaces", new PackageIconDescriptor("W", AssetPath: null), "right-top"),
                new PackageViewDescriptor("agent.subsessions", "agent", "Subsessions", new PackageIconDescriptor("S", AssetPath: null), "right-top", ShowInHotbarByDefault: false),
            ]);

    private static PackageViewHostService CreatePackageViewHostService()
        => new(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);

    private static PackageViewHostService CreateRegisteredPackageViewHostService()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<DisposablePackageView>("agent", "agent.chat", serviceProvider);
        return new PackageViewHostService(
            registry,
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);
    }

    private static string CreateAppPackageSource(string rootPath, string packageId)
    {
        var packageSourceFolder = Path.Combine(rootPath, "package-source");
        var libraryFolder = Path.Combine(packageSourceFolder, "lib");
        Directory.CreateDirectory(libraryFolder);

        var assemblyPath = typeof(ShellLifecycleTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        File.WriteAllText(Path.Combine(packageSourceFolder, "sunder-package.json"), $$"""
            {
              "id": "{{packageId}}",
              "entryAssembly": "{{entryAssemblyFileName}}"
            }
            """);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(libraryFolder, Path.GetFileName(file)), overwrite: true);
        }

        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(depsPath, Path.Combine(libraryFolder, Path.GetFileName(depsPath)), overwrite: true);
        }

        return packageSourceFolder;
    }

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

    private static string[] GetMiddleHotbarOrder(MainWindowViewModel viewModel)
        => viewModel.ListHotbarViews()
            .Where(view => view.Placement == PackageHotbarPlacement.Middle)
            .OrderBy(view => view.Order)
            .Select(view => view.ViewId)
            .ToArray();

    private sealed class MainWindowViewModelHarness(
        MainWindowViewModel viewModel,
        string rootPath,
        PackageViewHostService? packageViewHostService = null) : IDisposable
    {
        public MainWindowViewModel ViewModel { get; } = viewModel;

        public void Dispose()
        {
            ViewModel.Dispose();
            packageViewHostService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    private sealed class TestWindowLauncher : IWindowLauncher
    {
        public void ShowSettings()
        {
        }

        public Task<bool> ShowPackageSettingsAsync(
            string packageId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public void ShowPackages()
        {
        }

        public void CloseForShutdown()
        {
        }
    }

    private sealed class ThrowingRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient()
            => throw new NotSupportedException("Runtime API is not used by these tests.");
    }

    private sealed class EmptyRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => new StaticRuntimeApiClient([], []);
    }

    private sealed class MutableRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; set; } = [];

        public IReadOnlyList<PackageSourceDescriptor> PackageSources { get; set; } = [];

        public IRuntimeApiClient CreateClient() => new StaticRuntimeApiClient(ActivePackages, PackageSources);
    }

    private sealed class StaticRuntimeApiClientFactory(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        Func<CancellationToken, Task<SystemStatusResponse?>>? getSystemStatusAsync = null,
        Func<CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<ActivePackageDescriptor>>>? getActivePackagesAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<PackageSourceDescriptor>>>? getActivePackageSourcesAsync = null) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => new StaticRuntimeApiClient(
            activePackages,
            packageSources,
            getSystemStatusAsync,
            isRuntimeHealthyAsync,
            getActivePackagesAsync,
            getActivePackageSourcesAsync);
    }

    private sealed class StaticRuntimeApiClient(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        Func<CancellationToken, Task<SystemStatusResponse?>>? getSystemStatusAsync = null,
        Func<CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<ActivePackageDescriptor>>>? getActivePackagesAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<PackageSourceDescriptor>>>? getActivePackageSourcesAsync = null) : IRuntimeApiClient
    {
        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => getSystemStatusAsync?.Invoke(cancellationToken) ?? Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => isRuntimeHealthyAsync?.Invoke(cancellationToken) ?? Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => getActivePackagesAsync?.Invoke(cancellationToken) ?? Task.FromResult(activePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => getActivePackageSourcesAsync?.Invoke(cancellationToken) ?? Task.FromResult(packageSources);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

        public void Dispose()
        {
        }
    }

    private sealed class GatedPackageLifecycleRuntime
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource> _activePackageCallReleases = [];
        private int _activePackageCallCount;

        public int ActivePackageCallCount
        {
            get
            {
                lock (_gate)
                {
                    return _activePackageCallCount;
                }
            }
        }

        public async Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _activePackageCallCount++;
                _activePackageCallReleases.Enqueue(release);
            }

            await release.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>([]);
        }

        public void ReleaseNextActivePackageCall()
        {
            TaskCompletionSource release;
            lock (_gate)
            {
                release = _activePackageCallReleases.Dequeue();
            }

            release.SetResult();
        }
    }

    private sealed class DisposablePackageView : Control, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}

public sealed class ShellLifecycleTestPackageModule : ISunderPackageModule
{
    public const string SkipViewMarkerFileName = "skip-view";

    public const string ThrowAfterViewMarkerFileName = "throw-after-view";

    public const string RegisterBackgroundServiceMarkerFileName = "register-background-service";

    public const string ThrowOnBackgroundStartMarkerFileName = "throw-background-start";

    public const string BackgroundServiceStartedFileName = "background-service-started";

    public const string BackgroundServiceStoppedFileName = "background-service-stopped";

    private string? _packageFolder;

    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        _packageFolder = context.InstallPath;
        if (HasMarker(RegisterBackgroundServiceMarkerFileName) || HasMarker(ThrowOnBackgroundStartMarkerFileName))
        {
            services.AddSingleton(new ShellLifecycleTestBackgroundService(context.InstallPath));
        }
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        if (HasMarker(RegisterBackgroundServiceMarkerFileName) || HasMarker(ThrowOnBackgroundStartMarkerFileName))
        {
            registry.RegisterBackgroundService<ShellLifecycleTestBackgroundService>();
        }

        if (!HasMarker(SkipViewMarkerFileName))
        {
            registry.RegisterPackageView<ShellLifecycleThreadAffinedPackageView>(new PackageViewRegistration("agent.chat", "Chat"));
        }

        if (HasMarker(ThrowAfterViewMarkerFileName))
        {
            throw new InvalidOperationException("Test package requested activation failure after registering contributions.");
        }
    }

    private bool HasMarker(string fileName)
        => !string.IsNullOrWhiteSpace(_packageFolder)
           && File.Exists(Path.Combine(_packageFolder, fileName));
}

public sealed class ShellLifecycleTestBackgroundService(string packageFolder) : IPackageBackgroundService
{
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(packageFolder, ShellLifecycleTestPackageModule.BackgroundServiceStartedFileName), string.Empty);
        if (File.Exists(Path.Combine(packageFolder, ShellLifecycleTestPackageModule.ThrowOnBackgroundStartMarkerFileName)))
        {
            throw new InvalidOperationException("Test package requested background service start failure.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(packageFolder, ShellLifecycleTestPackageModule.BackgroundServiceStoppedFileName), string.Empty);
        return Task.CompletedTask;
    }
}

public sealed class ShellLifecycleThreadAffinedPackageView : Control, IDisposable
{
    public int OwnerThreadId { get; } = Environment.CurrentManagedThreadId;

    public bool IsDisposed { get; private set; }

    public int DisposeThreadId { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        DisposeThreadId = Environment.CurrentManagedThreadId;
    }
}
