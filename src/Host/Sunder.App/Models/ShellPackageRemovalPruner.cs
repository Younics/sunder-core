namespace Sunder.App.Models;

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
            .ToArray();

        if (removedViewIds.Length == 0)
        {
            return false;
        }

        foreach (var viewId in removedViewIds)
        {
            viewsById.Remove(viewId);
            shellState.ViewPlacements.Remove(viewId);
            shellState.ViewOrder.Remove(viewId);
            shellState.HiddenHotbarViewIds.Remove(viewId);
            ClearSelectedView(shellState, viewId);
        }

        return true;
    }

    private static void ClearSelectedView(ShellState shellState, string viewId)
    {
        if (string.Equals(shellState.SelectedLeftTopViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            shellState.SelectedLeftTopViewId = null;
        }

        if (string.Equals(shellState.SelectedMiddleViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            shellState.SelectedMiddleViewId = null;
        }

        if (string.Equals(shellState.SelectedRightTopViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            shellState.SelectedRightTopViewId = null;
        }

        if (string.Equals(shellState.SelectedLeftBottomViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            shellState.SelectedLeftBottomViewId = null;
        }

        if (string.Equals(shellState.SelectedRightBottomViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            shellState.SelectedRightBottomViewId = null;
        }
    }
}
