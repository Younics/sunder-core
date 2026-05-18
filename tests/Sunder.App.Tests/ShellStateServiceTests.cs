using System.Text.Json;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.Themes;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellStateServiceTests
{
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
            HiddenHotbarViewIds = ["agent.subsessions"],
            SelectedMiddleViewId = "agent.chat",
            LeftPanelWidth = 410,
            RightPanelWidth = 390,
            SettingsSidebarWidth = 310,
            PackagesSidebarWidth = 520,
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
            PreferredRuntimeUrl = "http://127.0.0.1:5280/",
        };

        await service.SaveAsync(state);
        state.SelectedMiddleViewId = "mutated-after-save";

        var reloaded = service.Load();

        Assert.Equal("agent.chat", reloaded.SelectedMiddleViewId);
        Assert.Equal(RailPlacement.Middle, reloaded.ViewPlacements["agent.chat"]);
        Assert.Contains("agent.subsessions", reloaded.HiddenHotbarViewIds);
        Assert.Equal(410, reloaded.LeftPanelWidth);
        Assert.Equal(390, reloaded.RightPanelWidth);
        Assert.Equal(310, reloaded.SettingsSidebarWidth);
        Assert.Equal(520, reloaded.PackagesSidebarWidth);
        var settingsPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.SettingsWindowPlacement);
        Assert.Equal(120, settingsPlacement.X);
        Assert.Equal(1100, settingsPlacement.Width);
        Assert.True(settingsPlacement.IsMaximized);
        var packagesPlacement = Assert.IsType<ShellWindowPlacement>(reloaded.PackagesWindowPlacement);
        Assert.Equal(220, packagesPlacement.X);
        Assert.Equal(1280, packagesPlacement.Width);
        Assert.Equal("http://127.0.0.1:5280/", reloaded.PreferredRuntimeUrl);
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
        }));
        var service = new ShellStateService(statePath);

        var state = service.Load();

        Assert.Equal(ShellState.DefaultSettingsSidebarWidth, state.SettingsSidebarWidth);
        Assert.Equal(ShellState.DefaultPackagesSidebarWidth, state.PackagesSidebarWidth);
    }

}
