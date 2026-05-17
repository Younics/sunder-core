using Sunder.App.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal static class ShellHotbarStateMutator
{
    public static bool AddViewToHotbar(
        string viewId,
        PackageHotbarPlacement placement,
        int? index,
        IDictionary<string, ShellPackageView> viewsById,
        ShellState shellState,
        Func<RailPlacement, List<string>> getOrderedViewIds)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var targetPlacement = ShellPlacementCatalog.ToRailPlacement(placement);
        var sourcePlacement = packageView.Placement;
        var sourceOrder = getOrderedViewIds(sourcePlacement);
        sourceOrder.RemoveAll(id => string.Equals(id, viewId, StringComparison.OrdinalIgnoreCase));

        var targetOrder = sourcePlacement == targetPlacement
            ? sourceOrder
            : getOrderedViewIds(targetPlacement);
        targetOrder.RemoveAll(id => string.Equals(id, viewId, StringComparison.OrdinalIgnoreCase));
        InsertAt(targetOrder, viewId, index);

        shellState.HiddenHotbarViewIds.Remove(viewId);
        shellState.ViewPlacements[viewId] = targetPlacement;
        viewsById[viewId] = packageView with { Placement = targetPlacement };

        ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, sourcePlacement, sourceOrder);
        if (sourcePlacement != targetPlacement)
        {
            ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, targetPlacement, targetOrder);
            ShellSelectionState.ClearSelectedViewId(shellState, sourcePlacement, viewId);
        }
        else
        {
            ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, targetPlacement, targetOrder);
        }

        return true;
    }

    public static bool RemoveViewFromHotbar(
        string viewId,
        IDictionary<string, ShellPackageView> viewsById,
        ShellState shellState)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        shellState.HiddenHotbarViewIds.Add(viewId);
        ShellSelectionState.ClearSelectedViewId(shellState, packageView.Placement, viewId);
        return true;
    }

    public static bool MoveView(
        string viewId,
        RailPlacement placement,
        int? targetIndex,
        IDictionary<string, ShellPackageView> viewsById,
        ShellState shellState,
        Func<RailPlacement, List<string>> getOrderedViewIds)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var sourcePlacement = packageView.Placement;
        var sourceOrder = getOrderedViewIds(sourcePlacement);
        var originalIndex = sourceOrder.IndexOf(viewId);
        if (originalIndex < 0)
        {
            return false;
        }

        sourceOrder.RemoveAt(originalIndex);

        if (sourcePlacement == placement)
        {
            if (!TryMoveWithinPlacement(viewId, sourceOrder, originalIndex, targetIndex))
            {
                return false;
            }

            ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, placement, sourceOrder);
            return true;
        }

        var targetOrder = getOrderedViewIds(placement);
        InsertAt(targetOrder, viewId, targetIndex);

        shellState.ViewPlacements[viewId] = placement;
        viewsById[viewId] = packageView with { Placement = placement };

        ShellSelectionState.ClearSelectedViewId(shellState, sourcePlacement, viewId);
        ShellSelectionState.SetSelectedViewId(shellState, placement, viewId);
        ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, sourcePlacement, sourceOrder);
        ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, placement, targetOrder);
        return true;
    }

    public static void InsertAt(IList<string> orderedViewIds, string viewId, int? targetIndex)
    {
        var normalizedIndex = targetIndex.HasValue
            ? Math.Clamp(targetIndex.Value, 0, orderedViewIds.Count)
            : orderedViewIds.Count;

        orderedViewIds.Insert(normalizedIndex, viewId);
    }

    public static bool TryMoveWithinPlacement(
        string viewId,
        IList<string> sourceOrder,
        int originalIndex,
        int? targetIndex)
    {
        var normalizedIndex = targetIndex.HasValue
            ? Math.Clamp(targetIndex.Value, 0, sourceOrder.Count)
            : sourceOrder.Count;

        if (normalizedIndex == originalIndex)
        {
            return false;
        }

        InsertAt(sourceOrder, viewId, normalizedIndex);
        return true;
    }
}
