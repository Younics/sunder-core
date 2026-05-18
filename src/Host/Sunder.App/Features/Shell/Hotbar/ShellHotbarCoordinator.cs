using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Features.Shell.Hotbar;

internal sealed class ShellHotbarCoordinator(
    IDictionary<string, ShellPackageView> viewsById,
    ShellState shellState,
    Func<RailPlacement, List<string>> getOrderedViewIds,
    Func<string, IReadOnlyDictionary<string, string?>?, ValueTask<bool>> openPackageViewPanelAsync,
    Action<bool> rebuildRailCollections,
    Action<IReadOnlySet<RailPlacement>, IReadOnlySet<string>, bool> updateRailCollections,
    Action persistShellState)
{
    public bool IsViewInHotbar(string viewId)
        => viewsById.ContainsKey(viewId) && !shellState.HiddenHotbarViewIds.Contains(viewId);

    public async ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        return await AddViewToHotbarAsync(
            viewId,
            ShellPlacementCatalog.ToPackageHotbarPlacement(packageView.Placement),
            null,
            openPanel,
            parameters);
    }

    public async ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var sourcePlacement = viewsById.TryGetValue(viewId, out var packageView)
            ? packageView.Placement
            : ShellPlacementCatalog.ToRailPlacement(placement);
        var targetPlacement = ShellPlacementCatalog.ToRailPlacement(placement);
        if (!ShellHotbarStateMutator.AddViewToHotbar(
            viewId,
            placement,
            index,
            viewsById,
            shellState,
            getOrderedViewIds))
        {
            return false;
        }

        if (openPanel)
        {
            UpdateRailCollectionsForView(viewId, new HashSet<RailPlacement> { sourcePlacement, targetPlacement }, createHostedViews: true);
            return await openPackageViewPanelAsync(viewId, parameters);
        }

        UpdateRailCollectionsForView(viewId, new HashSet<RailPlacement> { sourcePlacement, targetPlacement }, createHostedViews: true);
        persistShellState();
        return true;
    }

    public bool RemoveViewFromHotbar(string viewId)
    {
        var placement = viewsById.TryGetValue(viewId, out var packageView)
            ? packageView.Placement
            : RailPlacement.Middle;
        if (!ShellHotbarStateMutator.RemoveViewFromHotbar(viewId, viewsById, shellState))
        {
            return false;
        }

        UpdateRailCollectionsForView(viewId, new HashSet<RailPlacement> { placement }, createHostedViews: true);
        persistShellState();
        return true;
    }

    public void MoveView(string viewId, RailPlacement placement, int? targetIndex)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return;
        }

        if (!IsViewInHotbar(viewId))
        {
            _ = AddViewToHotbarAsync(viewId, ShellPlacementCatalog.ToPackageHotbarPlacement(placement), targetIndex, openPanel: true);
            return;
        }

        var sourcePlacement = packageView.Placement;
        if (!ShellHotbarStateMutator.MoveView(
            viewId,
            placement,
            targetIndex,
            viewsById,
            shellState,
            getOrderedViewIds))
        {
            return;
        }

        UpdateRailCollectionsForView(viewId, new HashSet<RailPlacement> { sourcePlacement, placement }, createHostedViews: true);
        persistShellState();
    }

    private void UpdateRailCollectionsForView(
        string viewId,
        IReadOnlySet<RailPlacement> placements,
        bool createHostedViews)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            rebuildRailCollections(createHostedViews);
            return;
        }

        updateRailCollections(
            placements,
            new HashSet<string>([packageView.PackageId], StringComparer.OrdinalIgnoreCase),
            createHostedViews);
    }
}
