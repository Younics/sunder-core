using Sunder.App.Models;

namespace Sunder.App.Features.Shell.State;

internal static class ShellStateSnapshotFactory
{
    public static ShellState Clone(ShellState state)
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
            BackgroundProcessPopoverWidth = state.BackgroundProcessPopoverWidth,
            BackgroundProcessPopoverHeight = state.BackgroundProcessPopoverHeight,
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
}
