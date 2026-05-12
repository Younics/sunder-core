using System.Text.Json;
using Sunder.App.Models;
using Sunder.App.Themes;

namespace Sunder.App.Services;

public sealed class ShellStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _stateFilePath;
    private readonly object _syncRoot = new();

    public ShellStateService(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunder",
            "shell-state.json"
        );
    }

    public ShellState Load()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_stateFilePath))
            {
                return new ShellState();
            }

            try
            {
                var state = JsonSerializer.Deserialize<ShellState>(File.ReadAllText(_stateFilePath), JsonOptions) ?? new ShellState();
                state.ViewPlacements ??= [];
                state.ViewOrder ??= [];
                state.HiddenHotbarViewIds = new HashSet<string>(state.HiddenHotbarViewIds ?? [], StringComparer.OrdinalIgnoreCase);
                state.ThemeId = string.IsNullOrWhiteSpace(state.ThemeId) ? SunderThemeDefinition.GraphiteDark.Id : state.ThemeId;

                if (state.LayoutVersion != ShellState.CurrentLayoutVersion)
                {
                    return new ShellState { ThemeId = state.ThemeId, PreferredRuntimeUrl = state.PreferredRuntimeUrl };
                }

                state.LeftPanelWidth = ClampPanelWidth(state.LeftPanelWidth, ShellState.DefaultLeftPanelWidth);
                state.RightPanelWidth = ClampPanelWidth(state.RightPanelWidth, ShellState.DefaultRightPanelWidth);
                state.TopRowHeightRatio = ClampRatio(state.TopRowHeightRatio, ShellState.DefaultTopRowHeightRatio);
                state.BottomSplitRatio = ClampRatio(state.BottomSplitRatio, ShellState.DefaultBottomSplitRatio);
                state.SettingsSidebarWidth = ClampSecondarySidebarWidth(state.SettingsSidebarWidth, ShellState.DefaultSettingsSidebarWidth);
                state.PackagesSidebarWidth = ClampSecondarySidebarWidth(state.PackagesSidebarWidth, ShellState.DefaultPackagesSidebarWidth);
                return state;
            }
            catch
            {
                return new ShellState();
            }
        }
    }

    public void Save(ShellState state)
    {
        lock (_syncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
    }

    public Task SaveAsync(ShellState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Clone(state);
        return Task.Run(() => Save(snapshot), cancellationToken);
    }

    private static ShellState Clone(ShellState state)
        => new()
        {
            LayoutVersion = state.LayoutVersion,
            HasInitializedLayout = state.HasInitializedLayout,
            ViewPlacements = new Dictionary<string, RailPlacement>(state.ViewPlacements, StringComparer.OrdinalIgnoreCase),
            ViewOrder = new Dictionary<string, int>(state.ViewOrder, StringComparer.OrdinalIgnoreCase),
            HiddenHotbarViewIds = new HashSet<string>(state.HiddenHotbarViewIds, StringComparer.OrdinalIgnoreCase),
            SelectedLeftTopViewId = state.SelectedLeftTopViewId,
            SelectedMiddleViewId = state.SelectedMiddleViewId,
            SelectedRightTopViewId = state.SelectedRightTopViewId,
            SelectedLeftBottomViewId = state.SelectedLeftBottomViewId,
            SelectedRightBottomViewId = state.SelectedRightBottomViewId,
            LeftPanelWidth = state.LeftPanelWidth,
            RightPanelWidth = state.RightPanelWidth,
            TopRowHeightRatio = state.TopRowHeightRatio,
            BottomSplitRatio = state.BottomSplitRatio,
            SettingsSidebarWidth = state.SettingsSidebarWidth,
            PackagesSidebarWidth = state.PackagesSidebarWidth,
            SettingsWindowPlacement = ClonePlacement(state.SettingsWindowPlacement),
            PackagesWindowPlacement = ClonePlacement(state.PackagesWindowPlacement),
            ThemeId = state.ThemeId,
            PreferredRuntimeUrl = state.PreferredRuntimeUrl,
        };

    private static ShellWindowPlacement? ClonePlacement(ShellWindowPlacement? placement)
        => placement is null
            ? null
            : new ShellWindowPlacement
            {
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                IsMaximized = placement.IsMaximized,
            };

    private static double ClampRatio(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return Math.Clamp(value, 0.01, 0.99);
    }

    private static double ClampPanelWidth(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, 180, 1200);
    }

    private static double ClampSecondarySidebarWidth(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, 180, 900);
    }
}
