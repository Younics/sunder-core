using Sunder.App.Models;

namespace Sunder.App.Features.Shell.State;

internal static class ShellViewOrdering
{
    public static IEnumerable<ShellPackageView> GetOrderedViewsForPlacement(
        IEnumerable<ShellPackageView> packageViews,
        ShellState shellState,
        RailPlacement placement)
    {
        return packageViews
            .Where(view => view.Placement == placement)
            .Where(view => !shellState.HiddenHotbarViewIds.Contains(view.ViewId))
            .OrderBy(view => shellState.ViewOrder.TryGetValue(view.ViewId, out var order) ? 0 : 1)
            .ThenBy(view => shellState.ViewOrder.TryGetValue(view.ViewId, out var order) ? order : int.MaxValue)
            .ThenBy(view => view.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Title, StringComparer.OrdinalIgnoreCase);
    }
}
