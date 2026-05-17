using Sunder.App.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal static class PackageHotbarProjector
{
    public static IReadOnlyList<PackageHotbarView> Project(
        Func<RailPlacement, IEnumerable<ShellPackageView>> getOrderedViewsForPlacement,
        Func<RailPlacement, string?> getSelectedViewId)
    {
        var views = new List<PackageHotbarView>();
        foreach (var placement in ShellPlacementCatalog.All)
        {
            var order = 0;
            foreach (var view in getOrderedViewsForPlacement(placement))
            {
                views.Add(new PackageHotbarView(
                    view.ViewId,
                    view.PackageId,
                    view.PackageDisplayName,
                    view.Title,
                    view.Glyph,
                    ShellPlacementCatalog.ToPackageHotbarPlacement(view.Placement),
                    order++,
                    string.Equals(getSelectedViewId(placement), view.ViewId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return views;
    }
}
