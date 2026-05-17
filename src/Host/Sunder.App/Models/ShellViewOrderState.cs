namespace Sunder.App.Models;

internal static class ShellViewOrderState
{
    public static void SetOrderForPlacement(
        IEnumerable<ShellPackageView> packageViews,
        ShellState shellState,
        RailPlacement placement,
        IReadOnlyList<string> orderedViewIds)
    {
        var placementViewIds = packageViews
            .Where(view => view.Placement == placement)
            .Select(view => view.ViewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var viewId in placementViewIds)
        {
            shellState.ViewOrder.Remove(viewId);
        }

        for (var index = 0; index < orderedViewIds.Count; index++)
        {
            shellState.ViewOrder[orderedViewIds[index]] = index;
        }
    }
}
