using Sunder.App.Models;

namespace Sunder.App.Features.Shell.State;

internal static class ShellStateSnapshotFactory
{
    public static ShellState Clone(ShellState state)
        => new()
        {
            Revision = state.Revision,
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
            StacksSidebarWidth = state.StacksSidebarWidth,
            BackgroundProcessPopoverWidth = state.BackgroundProcessPopoverWidth,
            BackgroundProcessPopoverHeight = state.BackgroundProcessPopoverHeight,
            SettingsWindowPlacement = ClonePlacement(state.SettingsWindowPlacement),
            PackagesWindowPlacement = ClonePlacement(state.PackagesWindowPlacement),
            StacksWindowPlacement = ClonePlacement(state.StacksWindowPlacement),
            CreateStackWizardWindowPlacement = ClonePlacement(state.CreateStackWizardWindowPlacement),
            UseStackWizardWindowPlacement = ClonePlacement(state.UseStackWizardWindowPlacement),
            ThemeId = state.ThemeId,
            PreferredRuntimeUrl = state.PreferredRuntimeUrl,
        };

    public static void CopyFrom(ShellState source, ShellState target)
    {
        var copy = Clone(source);
        target.Revision = copy.Revision;
        target.LayoutVersion = copy.LayoutVersion;
        target.HasInitializedLayout = copy.HasInitializedLayout;
        target.ViewPlacements = copy.ViewPlacements;
        target.ViewOrder = copy.ViewOrder;
        target.HiddenHotbarViewIds = copy.HiddenHotbarViewIds;
        target.SelectedLeftTopViewId = copy.SelectedLeftTopViewId;
        target.SelectedMiddleViewId = copy.SelectedMiddleViewId;
        target.SelectedRightTopViewId = copy.SelectedRightTopViewId;
        target.SelectedLeftBottomViewId = copy.SelectedLeftBottomViewId;
        target.SelectedRightBottomViewId = copy.SelectedRightBottomViewId;
        target.LeftPanelWidth = copy.LeftPanelWidth;
        target.RightPanelWidth = copy.RightPanelWidth;
        target.TopRowHeightRatio = copy.TopRowHeightRatio;
        target.BottomSplitRatio = copy.BottomSplitRatio;
        target.SettingsSidebarWidth = copy.SettingsSidebarWidth;
        target.PackagesSidebarWidth = copy.PackagesSidebarWidth;
        target.StacksSidebarWidth = copy.StacksSidebarWidth;
        target.BackgroundProcessPopoverWidth = copy.BackgroundProcessPopoverWidth;
        target.BackgroundProcessPopoverHeight = copy.BackgroundProcessPopoverHeight;
        target.SettingsWindowPlacement = copy.SettingsWindowPlacement;
        target.PackagesWindowPlacement = copy.PackagesWindowPlacement;
        target.StacksWindowPlacement = copy.StacksWindowPlacement;
        target.CreateStackWizardWindowPlacement = copy.CreateStackWizardWindowPlacement;
        target.UseStackWizardWindowPlacement = copy.UseStackWizardWindowPlacement;
        target.ThemeId = copy.ThemeId;
        target.PreferredRuntimeUrl = copy.PreferredRuntimeUrl;
    }

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
