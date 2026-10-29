using System.Text.Json;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.Themes;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellStateServiceTests
{
    [Fact]
    public void TaskTerminologyPreservesExistingPopoverSettingKeys()
    {
        var path = Path.Combine(CreateTempDirectory(), "shell-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"LayoutVersion":2,"BackgroundProcessPopoverWidth":612,"BackgroundProcessPopoverHeight":418}""");
        var service = new ShellStateService(path);
        var state = service.Load();
        Assert.Equal(612, state.BackgroundTaskPopoverWidth);
        Assert.Equal(418, state.BackgroundTaskPopoverHeight);
        service.Save(state);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(612, document.RootElement.GetProperty("BackgroundProcessPopoverWidth").GetDouble());
        Assert.Equal(418, document.RootElement.GetProperty("BackgroundProcessPopoverHeight").GetDouble());
        Assert.False(document.RootElement.TryGetProperty("BackgroundTaskPopoverWidth", out _));
    }

    [Fact]
    public void Load_WhenFileIsMissing_ReturnsDefaults()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "missing-shell-state.json");
        var service = new ShellStateService(statePath);

        var state = service.Load();

        Assert.Equal(ShellState.CurrentLayoutVersion, state.LayoutVersion);
        Assert.Equal(SunderThemeDefinition.GraphiteDark.Id, state.ThemeId);
        Assert.Empty(state.ViewPlacements);
        Assert.Empty(state.ViewOrder);
        Assert.Empty(state.HiddenHotbarViewIds);
    }

    [Fact]
    public void Load_WhenLayoutVersionIsOld_PreservesThemeAndRuntimeUrlOnly()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "shell-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(new ShellState
        {
            LayoutVersion = ShellState.CurrentLayoutVersion - 1,
            ThemeId = SunderThemeDefinition.GraphiteDark.Id,
            PreferredRuntimeUrl = "http://127.0.0.1:5999/",
            ViewPlacements = new Dictionary<string, RailPlacement>
            {
                ["stale-view"] = RailPlacement.LeftTop,
            },
            SelectedMiddleViewId = "stale-view",
        }));
        var service = new ShellStateService(statePath);

        var state = service.Load();

        Assert.Equal(ShellState.CurrentLayoutVersion, state.LayoutVersion);
        Assert.Equal(SunderThemeDefinition.GraphiteDark.Id, state.ThemeId);
        Assert.Equal("http://127.0.0.1:5999/", state.PreferredRuntimeUrl);
        Assert.Empty(state.ViewPlacements);
        Assert.Null(state.SelectedMiddleViewId);
    }

    [Fact]
    public async Task SaveAsync_WritesSnapshotThatCanBeReloaded()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "shell-state.json");
        var service = new ShellStateService(statePath);
        var state = new ShellState
        {
            ViewPlacements = new Dictionary<string, RailPlacement>
            {
                ["agent.chat"] = RailPlacement.Middle,
            },
            ViewOrder = new Dictionary<string, int>
            {
                ["agent.chat"] = 0,
            },
            ViewPackageIds = new Dictionary<string, string> { ["agent.chat"] = "agent" },
            HiddenHotbarViewIds = ["agent.subsessions"],
            SelectedMiddleViewId = "agent.chat",
            SelectedLeftTopViewId = "left",
            SelectedRightTopViewId = "right",
            SelectedLeftBottomViewId = "bottom-left",
            SelectedRightBottomViewId = "bottom-right",
            LeftPanelWidth = 1410,
            RightPanelWidth = 1390,
            TopRowHeightRatio = 0.63,
            BottomSplitRatio = 0.27,
            MainWindowPlacement = new ShellWindowPlacement
            {
                X = 70, Y = 80, Width = 1900, Height = 1000, IsFullScreen = true,
            },
            SettingsSidebarWidth = 310,
            PackagesSidebarWidth = 520,
            StacksSidebarWidth = 470,
            SettingsWindowPlacement = new ShellWindowPlacement
            {
                X = 120,
                Y = 140,
                Width = 1100,
                Height = 760,
                IsMaximized = true,
            },
            PackagesWindowPlacement = new ShellWindowPlacement
            {
                X = 220,
                Y = 240,
                Width = 1280,
                Height = 820,
            },
            StacksWindowPlacement = new ShellWindowPlacement
            {
                X = 320,
                Y = 340,
                Width = 1180,
                Height = 780,
            },
            CreateStackWizardWindowPlacement = new ShellWindowPlacement
            {
                X = 420,
                Y = 440,
                Width = 1040,
                Height = 740,
            },
            UseStackWizardWindowPlacement = new ShellWindowPlacement
            {
                X = 520,
                Y = 540,
                Width = 1060,
                Height = 760,
            },
            PreferredRuntimeUrl = "http://127.0.0.1:5280/",
        };

        await service.SaveAsync(state);
        state.SelectedMiddleViewId = "mutated-after-save";

        var reloaded = service.Load();

        Assert.Equal("agent.chat", reloaded.SelectedMiddleViewId);
        Assert.Equal(RailPlacement.Middle, reloaded.ViewPlacements["agent.chat"]);
        Assert.Equal("agent", reloaded.ViewPackageIds["agent.chat"]);
        Assert.Contains("agent.subsessions", reloaded.HiddenHotbarViewIds);
        Assert.Equal(1410, reloaded.LeftPanelWidth);
        Assert.Equal(1390, reloaded.RightPanelWidth);
        Assert.Equal(0.63, reloaded.TopRowHeightRatio);
        Assert.Equal(0.27, reloaded.BottomSplitRatio);
        Assert.Equal("left", reloaded.SelectedLeftTopViewId);
        Assert.Equal("right", reloaded.SelectedRightTopViewId);
        Assert.Equal("bottom-left", reloaded.SelectedLeftBottomViewId);
        Assert.Equal("bottom-right", reloaded.SelectedRightBottomViewId);
        Assert.True(reloaded.MainWindowPlacement?.IsFullScreen);
        Assert.Equal(1900, reloaded.MainWindowPlacement?.Width);
        Assert.Equal(310, reloaded.SettingsSidebarWidth);
        Assert.Equal(520, reloaded.PackagesSidebarWidth);
        Assert.Equal(470, reloaded.StacksSidebarWidth);
        var settingsPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.SettingsWindowPlacement);
        Assert.Equal(120, settingsPlacement.X);
        Assert.Equal(1100, settingsPlacement.Width);
        Assert.True(settingsPlacement.IsMaximized);
        var packagesPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.PackagesWindowPlacement);
        Assert.Equal(220, packagesPlacement.X);
        Assert.Equal(1280, packagesPlacement.Width);
        var stacksPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.StacksWindowPlacement);
        Assert.Equal(320, stacksPlacement.X);
        Assert.Equal(1180, stacksPlacement.Width);
        var createStackWizardPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.CreateStackWizardWindowPlacement);
        Assert.Equal(420, createStackWizardPlacement.X);
        Assert.Equal(1040, createStackWizardPlacement.Width);
        var useStackWizardPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.UseStackWizardWindowPlacement);
        Assert.Equal(520, useStackWizardPlacement.X);
        Assert.Equal(1060, useStackWizardPlacement.Width);
        Assert.Equal("http://127.0.0.1:5280/", reloaded.PreferredRuntimeUrl);
    }

    [Fact]
    public async Task QueuedLayoutSave_InterleavedWithWindowPatch_PreservesBothChanges()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "shell-state.json");
        var service = new ShellStateService(statePath);
        var liveState = service.Load();
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new ShellStatePersistenceQueue(service, liveState, _ => releaseSave.Task);

        liveState.ViewPlacements["agent.chat"] = RailPlacement.Middle;
        liveState.HiddenHotbarViewIds.Add("agent.subsessions");
        liveState.LeftPanelWidth = 475;
        var queuedSave = queue.QueueSave();

        service.Update(liveState, state => state.SettingsWindowPlacement = new ShellWindowPlacement
        {
            X = 80,
            Y = 90,
            Width = 1200,
            Height = 800,
        });

        Assert.Equal(RailPlacement.Middle, liveState.ViewPlacements["agent.chat"]);
        Assert.Contains("agent.subsessions", liveState.HiddenHotbarViewIds);
        Assert.Equal(475, liveState.LeftPanelWidth);
        Assert.Equal(1200, liveState.SettingsWindowPlacement?.Width);

        var patchedPersisted = service.Load();
        Assert.Equal(RailPlacement.Middle, patchedPersisted.ViewPlacements["agent.chat"]);
        Assert.Contains("agent.subsessions", patchedPersisted.HiddenHotbarViewIds);
        Assert.Equal(475, patchedPersisted.LeftPanelWidth);
        Assert.Equal(1200, patchedPersisted.SettingsWindowPlacement?.Width);

        releaseSave.SetResult();
        await queuedSave;

        var persisted = service.Load();
        Assert.Equal(RailPlacement.Middle, persisted.ViewPlacements["agent.chat"]);
        Assert.Contains("agent.subsessions", persisted.HiddenHotbarViewIds);
        Assert.Equal(475, persisted.LeftPanelWidth);
        Assert.Equal(1200, persisted.SettingsWindowPlacement?.Width);
        Assert.Equal(persisted.Revision, liveState.Revision);
        Assert.Equal(2, persisted.Revision);

        liveState.RightPanelWidth = 525;
        await queue.QueueSave();

        persisted = service.Load();
        Assert.Equal(525, persisted.RightPanelWidth);
        // Background workers do not mutate the UI-owned live state.
        Assert.Equal(2, liveState.Revision);
        Assert.Equal(3, persisted.Revision);
    }

    [Fact]
    public async Task Flush_CapturesLatestStateAndSupersedesPendingDebounce()
    {
        using var service = new ShellStateService(Path.Combine(CreateTempDirectory(), "shell-state.json"));
        var live = service.Load();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new ShellStatePersistenceQueue(service, live, _ => never.Task);
        live.RightPanelWidth = 1500;
        var pending = queue.QueueSave();
        live.RightPanelWidth = 1700;
        live.SelectedMiddleViewId = "final";
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var reloaded = service.Load();
        Assert.Equal(1700, reloaded.RightPanelWidth);
        Assert.Equal("final", reloaded.SelectedMiddleViewId);
    }

    [Fact]
    public async Task IndependentStaleSaveCannotCancelAPendingSessionSnapshot()
    {
        using var service = new ShellStateService(Path.Combine(CreateTempDirectory(), "shell-state.json"));
        var live = service.Load();
        var stale = service.Load();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new ShellStatePersistenceQueue(service, live, _ => never.Task);
        live.RightPanelWidth = 1700;
        var pending = queue.QueueSave();
        service.Save(stale);
        await pending;
        Assert.Equal(1700, service.Load().RightPanelWidth);
        Assert.Equal(0, stale.Revision);
    }

    [Fact]
    public async Task Writer_FlushBehindRunningWriteIsNotRejectedForAnOldRevision()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var saved = new List<double>();
        using var writer = new ShellStateWriter();
        long Save(double width)
        {
            if (width == 600)
            {
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            saved.Add(width);
            return saved.Count;
        }
        var first = writer.Enqueue(() => Save(600));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var final = writer.Enqueue(() => Save(700));
        Assert.False(final.IsCompleted);
        release.Set();
        await Task.WhenAll(first, final).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([600d, 700d], saved);

        using var service = new ShellStateService(Path.Combine(CreateTempDirectory(), "shell-state.json"));
        var live = service.Load();
        service.Save(new ShellState { RightPanelWidth = 600 });
        // The UI snapshot predates the completed disk write, but its contents
        // are the newer session intent. A flush must not silently reject it.
        live.RightPanelWidth = 700;
        await service.FlushAsync(live);
        Assert.Equal(700, service.Load().RightPanelWidth);
    }

    [Theory]
    [InlineData(-1, 0.10, 0.01)]
    [InlineData(2, 0.90, 0.99)]
    public void LoadedRatiosUseTheSameBoundsAsRenderingAndInput(double value, double top, double bottom)
    {
        using var service = new ShellStateService(Path.Combine(CreateTempDirectory(), "shell-state.json"));
        service.Save(new ShellState { TopRowHeightRatio = value, BottomSplitRatio = value });
        var reloaded = service.Load();
        Assert.Equal(top, reloaded.TopRowHeightRatio);
        Assert.Equal(bottom, reloaded.BottomSplitRatio);
    }

    [Fact]
    public void Load_ClampsInvalidSecondarySplitterWidths()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "shell-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonSerializer.Serialize(new ShellState
        {
            SettingsSidebarWidth = 0,
            PackagesSidebarWidth = -1,
            StacksSidebarWidth = -2,
        }));
        var service = new ShellStateService(statePath);

        var state = service.Load();

        Assert.Equal(ShellState.DefaultSettingsSidebarWidth, state.SettingsSidebarWidth);
        Assert.Equal(ShellState.DefaultPackagesSidebarWidth, state.PackagesSidebarWidth);
        Assert.Equal(ShellState.DefaultStacksSidebarWidth, state.StacksSidebarWidth);
    }

}
