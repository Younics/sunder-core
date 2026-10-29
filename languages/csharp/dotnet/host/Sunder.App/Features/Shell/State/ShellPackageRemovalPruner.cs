using Sunder.App.Models;

namespace Sunder.App.Features.Shell.State;

internal static class ShellPackageRemovalPruner
{
    public static bool RemovePackageViews(
        string packageId,
        IDictionary<string, ShellPackageView> viewsById,
        ShellState shellState)
    {
        var removedViewIds = viewsById.Values
            .Where(view => string.Equals(view.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .Select(view => view.ViewId)
            .Concat(shellState.ViewPackageIds.Where(pair => string.Equals(pair.Value, packageId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removedViewIds.Count == 0)
        {
            return false;
        }

        var removedMiddleSelection = shellState.SelectedMiddleViewId is { } middle && removedViewIds.Contains(middle);
        foreach (var viewId in removedViewIds)
        {
            viewsById.Remove(viewId);
        }
        ShellStateNormalizer.RemoveViews(shellState, removedViewIds);
        if (removedMiddleSelection)
        {
            ShellStateNormalizer.SelectMiddleFallback(shellState, viewsById.Values);
        }

        return true;
    }
}
