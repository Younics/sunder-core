using Sunder.App.Models;
using Sunder.Protocol;

namespace Sunder.App.Features.Shell.Menus;

internal static class PackageViewMenuProjector
{
    public static IReadOnlyList<PackageViewMenuGroup> Project(
        IEnumerable<ShellPackageView> packageViews,
        Func<string, PackageIconDescriptor?, Uri?> createPackageIconUri,
        Func<string, bool> isViewInHotbar)
    {
        return packageViews
            .OrderBy(view => view.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Title, StringComparer.OrdinalIgnoreCase)
            .GroupBy(view => view.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new PackageViewMenuGroup(
                    first.PackageId,
                    first.PackageDisplayName,
                    first.PackageGlyph,
                    createPackageIconUri(first.PackageId, first.PackageIcon),
                    group.Select(view => new PackageViewMenuItem(
                        view.ViewId,
                        view.Title,
                        view.Glyph,
                        createPackageIconUri(view.PackageId, view.Icon),
                        view.Placement,
                        isViewInHotbar(view.ViewId))).ToArray());
            })
            .ToArray();
    }
}
