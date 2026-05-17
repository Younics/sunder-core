using Sunder.App.Models;

namespace Sunder.App.ViewModels;

internal static class ShellPanelCloseSelector
{
    public static ShellItemViewModel? FindFallbackItem(
        RailPlacement placement,
        IEnumerable<ShellItemViewModel> items,
        string closingViewId)
    {
        return placement == RailPlacement.Middle
            ? items.FirstOrDefault(item => !string.Equals(item.Id, closingViewId, StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
