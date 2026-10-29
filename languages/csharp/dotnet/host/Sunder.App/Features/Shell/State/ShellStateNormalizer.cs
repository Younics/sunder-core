using Sunder.App.Models;

namespace Sunder.App.Features.Shell.State;

public enum ShellNormalizationPolicy
{
    SafeModePreserveLayout,
    AuthoritativeRuntimeSnapshot,
}

internal static class ShellStateNormalizer
{
    public static void Normalize(
        ShellState state,
        IReadOnlyList<ShellPackageView> packageViews,
        ShellNormalizationPolicy policy,
        IReadOnlySet<string>? availablePackageIds = null,
        IReadOnlySet<string>? unavailablePackageIds = null)
    {
        var availableIds = packageViews.Select(view => view.ViewId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableOwners = availablePackageIds
            ?? packageViews.Select(view => view.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only a completed catalog establishes absence. Failed App activation is
        // explicitly uncertain, not removal. Legacy ownerless ids can be resolved
        // when the complete catalog has no unavailable package that might own them.
        var removedIds = state.ViewPlacements.Keys.Concat(state.ViewOrder.Keys)
            .Concat(state.ViewPackageIds.Keys).Concat(state.HiddenHotbarViewIds)
            .Concat(ShellSelectionState.GetDeferredActivationSelections(state)
                .Select(selection => selection.ViewId).OfType<string>())
            .Where(id => !availableIds.Contains(id) && CanEstablishRemoval(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedMiddleSelection = state.SelectedMiddleViewId is { } middle && removedIds.Contains(middle);
        RemoveViews(state, removedIds);

        foreach (var view in packageViews)
        {
            var isNewView = !state.ViewPlacements.ContainsKey(view.ViewId);
            if (isNewView && !view.ShowInHotbarByDefault)
            {
                state.HiddenHotbarViewIds.Add(view.ViewId);
            }

            state.ViewPlacements[view.ViewId] = view.Placement;
            state.ViewPackageIds[view.ViewId] = view.PackageId;
            if (!state.ViewOrder.ContainsKey(view.ViewId))
            {
                var last = state.ViewOrder.Where(pair => state.ViewPlacements.TryGetValue(pair.Key, out var placement)
                    && placement == view.Placement).Select(pair => pair.Value).DefaultIfEmpty(-1).Max();
                state.ViewOrder[view.ViewId] = last < int.MaxValue ? last + 1 : last;
            }
        }

        foreach (var placement in ShellPlacementCatalog.All)
        {
            var selectedViewId = ShellSelectionState.GetSelectedViewId(state, placement);
            if (!string.IsNullOrWhiteSpace(selectedViewId)
                && (state.HiddenHotbarViewIds.Contains(selectedViewId)
                    || state.ViewPlacements.TryGetValue(selectedViewId, out var savedPlacement) && savedPlacement != placement))
            {
                ShellSelectionState.SetSelectedViewId(state, placement, null);
            }
        }

        if (string.IsNullOrWhiteSpace(state.SelectedMiddleViewId)
            && (removedMiddleSelection || !state.HasInitializedLayout))
        {
            SelectMiddleFallback(state, packageViews, unavailablePackageIds);
        }

        bool CanEstablishRemoval(string id)
        {
            var owner = state.ViewPackageIds.GetValueOrDefault(id);
            if (policy == ShellNormalizationPolicy.SafeModePreserveLayout)
                return !string.IsNullOrWhiteSpace(owner) && availableOwners.Contains(owner);
            return string.IsNullOrWhiteSpace(owner)
                ? unavailablePackageIds is null or { Count: 0 }
                : unavailablePackageIds?.Contains(owner) != true;
        }
    }

    internal static void SelectMiddleFallback(
        ShellState state,
        IEnumerable<ShellPackageView> packageViews,
        IReadOnlySet<string>? unavailablePackageIds = null)
        => state.SelectedMiddleViewId = ShellViewOrdering.GetOrderedViewsForPlacement(packageViews, state, RailPlacement.Middle)
            .FirstOrDefault(view => unavailablePackageIds?.Contains(view.PackageId) != true)?.ViewId;

    internal static void RemoveViews(ShellState state, IReadOnlySet<string> removedIds)
    {
        foreach (var id in removedIds)
        {
            state.ViewPlacements.Remove(id);
            state.ViewOrder.Remove(id);
            state.ViewPackageIds.Remove(id);
            state.HiddenHotbarViewIds.Remove(id);
            foreach (var placement in ShellPlacementCatalog.All) ShellSelectionState.ClearSelectedViewId(state, placement, id);
        }
    }
}
