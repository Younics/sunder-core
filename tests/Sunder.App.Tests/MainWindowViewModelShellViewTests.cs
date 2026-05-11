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

    private static MainWindowViewModelHarness CreateHarness(IRuntimeApiClientFactory? runtimeApiClientFactory = null)
    {
        var rootPath = CreateTempDirectory();
        return CreateHarness(rootPath, runtimeApiClientFactory ?? new ThrowingRuntimeApiClientFactory(), PackageViewHostService.Empty);
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
        PackageViewHostService? disposablePackageViewHostService = null)
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
            new NotificationCenterService(Path.Combine(rootPath, "notifications.json")));
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

    private sealed class StaticRuntimeApiClientFactory(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => new StaticRuntimeApiClient(activePackages, packageSources);
    }

    private sealed class StaticRuntimeApiClient(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources) : IRuntimeApiClient
    {
        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(activePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(packageSources);

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
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
    }
}
