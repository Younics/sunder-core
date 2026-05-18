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
            rebuildRailCollections(true);
            return await openPackageViewPanelAsync(viewId, parameters);
        }

        rebuildRailCollections(true);
        persistShellState();
        return true;
    }

    public bool RemoveViewFromHotbar(string viewId)
    {
        if (!ShellHotbarStateMutator.RemoveViewFromHotbar(viewId, viewsById, shellState))
        {
            return false;
        }

        rebuildRailCollections(true);
        persistShellState();
        return true;
    }

    public void MoveView(string viewId, RailPlacement placement, int? targetIndex)
    {
        if (!viewsById.ContainsKey(viewId))
        {
            return;
        }

        if (!IsViewInHotbar(viewId))
        {
            _ = AddViewToHotbarAsync(viewId, ShellPlacementCatalog.ToPackageHotbarPlacement(placement), targetIndex, openPanel: true);
            return;
        }

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

        rebuildRailCollections(true);
        persistShellState();
    }
}
