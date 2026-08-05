using Sunder.App.Models;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Layout;

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
