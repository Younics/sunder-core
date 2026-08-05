using Avalonia.Media;
using Sunder.App.Models;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Features.Shell.Menus;

internal static class ShellMenuProjector
{
    public static IReadOnlyList<ShellMenuItem> Project(
        IEnumerable<ShellPackageView> packageViews,
        Func<string, PackageIconDescriptor?, IImage?> getPackageIcon,
        Func<string, bool> isViewInHotbar,
        Func<string, CancellationToken, Task> openPackageViewAsync,
        bool includeDeveloperMenu,
        Func<CancellationToken, Task> openDeveloperLogsAsync)
    {
        var packageGroups = packageViews
            .OrderBy(view => view.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Title, StringComparer.OrdinalIgnoreCase)
            .GroupBy(view => view.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => ProjectPackageGroup(group, getPackageIcon, isViewInHotbar, openPackageViewAsync))
            .ToArray();
        IReadOnlyList<ShellMenuItem> packageItems = packageGroups.Length == 0
            ? [new ShellMenuItem("packages:empty", "No package views", null, null, false, [])]
            : packageGroups;
        var roots = new List<ShellMenuItem>
        {
            new(
                "view",
                "View",
                null,
                null,
                true,
                [new ShellMenuItem("view:packages", "Packages", null, null, true, packageItems)]),
        };

        if (includeDeveloperMenu)
        {
            roots.Add(new ShellMenuItem(
                "developer",
                "Developer",
                null,
                null,
                true,
                [new ShellMenuItem("developer:logs", "Logs", null, null, true, [], openDeveloperLogsAsync)]));
        }

        return roots;
    }

    private static ShellMenuItem ProjectPackageGroup(
        IGrouping<string, ShellPackageView> group,
        Func<string, PackageIconDescriptor?, IImage?> getPackageIcon,
        Func<string, bool> isViewInHotbar,
        Func<string, CancellationToken, Task> openPackageViewAsync)
    {
        var first = group.First();
        return new ShellMenuItem(
            $"package:{first.PackageId}",
            first.PackageDisplayName,
            first.PackageGlyph,
            getPackageIcon(first.PackageId, first.PackageIcon),
            true,
            group.Select(view => new ShellMenuItem(
                $"view:{view.ViewId}",
                view.Title,
                view.Glyph,
                getPackageIcon(view.PackageId, view.Icon),
                !isViewInHotbar(view.ViewId),
                [],
                cancellationToken => openPackageViewAsync(view.ViewId, cancellationToken))).ToArray());
    }
}
