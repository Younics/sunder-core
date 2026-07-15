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
        ShellNormalizationPolicy policy)
    {
        if (packageViews.Count == 0 && policy == ShellNormalizationPolicy.SafeModePreserveLayout)
        {
            return;
        }

        var activeViewIds = packageViews
            .Select(view => view.ViewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveStaleEntries(state.ViewPlacements, activeViewIds);
        RemoveStaleEntries(state.ViewOrder, activeViewIds);
        state.HiddenHotbarViewIds.RemoveWhere(viewId => !activeViewIds.Contains(viewId));

        foreach (var view in packageViews)
        {
            var isNewView = !state.ViewPlacements.ContainsKey(view.ViewId);
            if (isNewView && !view.ShowInHotbarByDefault)
            {
                state.HiddenHotbarViewIds.Add(view.ViewId);
            }

            state.ViewPlacements[view.ViewId] = view.Placement;
        }

        foreach (var placement in ShellPlacementCatalog.All)
        {
            var selectedViewId = ShellSelectionState.GetSelectedViewId(state, placement);
            if (!string.IsNullOrWhiteSpace(selectedViewId)
                && (!activeViewIds.Contains(selectedViewId)
                    || state.HiddenHotbarViewIds.Contains(selectedViewId)
                    || packageViews.All(view => !string.Equals(view.ViewId, selectedViewId, StringComparison.OrdinalIgnoreCase)
                                                || view.Placement != placement)))
            {
                ShellSelectionState.SetSelectedViewId(state, placement, null);
            }
        }

        if (string.IsNullOrWhiteSpace(state.SelectedMiddleViewId)
            && (policy == ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot || !state.HasInitializedLayout))
        {
            state.SelectedMiddleViewId = packageViews.FirstOrDefault(view =>
                view.Placement == RailPlacement.Middle
                && !state.HiddenHotbarViewIds.Contains(view.ViewId))?.ViewId;
        }
    }

    private static void RemoveStaleEntries<T>(IDictionary<string, T> entries, IReadOnlySet<string> activeViewIds)
    {
        foreach (var staleViewId in entries.Keys.Where(viewId => !activeViewIds.Contains(viewId)).ToArray())
        {
            entries.Remove(staleViewId);
        }
    }
}
