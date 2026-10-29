using Sunder.App.Features.Shell.State;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Models;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Panels;

internal sealed class ShellRailCollectionPresenter(
    IReadOnlyDictionary<string, ShellPackageView> viewsById,
    ShellState shellState,
    ShellSelectionPresenter selectionPresenter,
    ShellPanelContentPresenter panelContentPresenter,
    Func<ShellPackageView, Action<ShellItemViewModel>, ShellItemViewModel> createShellItem,
    Action<string> notifyViewNavigated,
    Action<string> cancelViewNavigation)
{
    public void Rebuild(
        IReadOnlyList<ShellPlacementSlot> slots,
        bool createHostedViews,
        IReadOnlySet<string>? stabilizedViewIds = null,
        IReadOnlySet<string>? retainedViewIds = null)
    {
        var viewIdsToCancel = new List<string>();
        var retainedPlacements = new HashSet<RailPlacement>();
        foreach (var slot in slots)
        {
            if (slot.Panel.ActiveViewId is { } active && retainedViewIds?.Contains(active) == true
                && active == ShellSelectionState.GetSelectedViewId(shellState, slot.Placement))
            {
                retainedPlacements.Add(slot.Placement);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(slot.Panel.ActiveViewId))
            {
                viewIdsToCancel.Add(slot.Panel.ActiveViewId);
            }
            selectionPresenter.SetSelectedItem(slot.Placement, null);
            slot.Panel.ClearActiveView();
        }

        foreach (var slot in slots)
        {
            slot.Bar.SetItems(CreateItemsForPlacement(slot.Placement, slot.OnSelect));
            PruneRetainedHostedViews(slot);
        }

        var middleBarItemCount = slots.FirstOrDefault(slot => slot.Placement == RailPlacement.Middle)?.Bar.Items.Count ?? 0;
        var viewIdsToNavigate = new List<string>();
        foreach (var slot in slots)
        {
            RestoreSelection(slot, ShellSelectionState.GetSelectedViewId(shellState, slot.Placement));
            var selectedViewId = ShellSelectionState.GetSelectedViewId(shellState, slot.Placement);
            if (retainedPlacements.Contains(slot.Placement)) continue;
            panelContentPresenter.Apply(slot.Panel, slot.Placement, selectedViewId, viewsById, middleBarItemCount, createHostedViews);
            if (createHostedViews
                && !string.IsNullOrWhiteSpace(selectedViewId)
                && stabilizedViewIds?.Contains(selectedViewId) != true)
            {
                viewIdsToNavigate.Add(selectedViewId);
            }
        }

        foreach (var viewId in viewIdsToCancel)
        {
            cancelViewNavigation(viewId);
        }
        foreach (var viewId in viewIdsToNavigate)
        {
            notifyViewNavigated(viewId);
        }
    }

    public void Update(
        IReadOnlyList<ShellPlacementSlot> slots,
        IReadOnlySet<RailPlacement> placements,
        IReadOnlySet<string> impactedPackageIds,
        bool createHostedViews)
    {
        var affectedSlots = slots
            .Where(slot => placements.Contains(slot.Placement))
            .ToArray();
        if (affectedSlots.Length == 0)
        {
            return;
        }

        var activeViewIdsBeforeUpdate = affectedSlots.ToDictionary(
            slot => slot.Placement,
            slot => slot.Panel.ActiveViewId);
        var selectedViewIdsBeforeUpdate = affectedSlots.ToDictionary(
            slot => slot.Placement,
            slot => ShellSelectionState.GetSelectedViewId(shellState, slot.Placement));

        foreach (var slot in affectedSlots)
        {
            slot.Bar.SetItemsPreservingExisting(CreateItemsForPlacement(slot.Placement, slot.OnSelect, slot.Bar, impactedPackageIds));
            PruneRetainedHostedViews(slot);
        }

        var middleBarItemCount = slots.FirstOrDefault(slot => slot.Placement == RailPlacement.Middle)?.Bar.Items.Count ?? 0;
        var selectedViewIdsAfterUpdate = new Dictionary<RailPlacement, string?>();
        var viewIdsToCancel = new List<string>();
        foreach (var slot in affectedSlots)
        {
            var selectedViewIdBeforeUpdate = selectedViewIdsBeforeUpdate[slot.Placement];
            RestoreSelection(slot, selectedViewIdBeforeUpdate);
            selectedViewIdsAfterUpdate[slot.Placement] = ShellSelectionState.GetSelectedViewId(shellState, slot.Placement);
        }

        foreach (var slot in affectedSlots)
        {
            if (ShouldApplyPanelContent(
                activeViewIdsBeforeUpdate[slot.Placement],
                selectedViewIdsAfterUpdate[slot.Placement],
                impactedPackageIds))
            {
                if (!string.IsNullOrWhiteSpace(activeViewIdsBeforeUpdate[slot.Placement]))
                {
                    viewIdsToCancel.Add(activeViewIdsBeforeUpdate[slot.Placement]!);
                }
                slot.Panel.ClearActiveView();
            }
        }

        var viewIdsToNavigate = new List<string>();
        foreach (var slot in affectedSlots)
        {
            var selectedViewIdAfterUpdate = selectedViewIdsAfterUpdate[slot.Placement];
            if (!ShouldApplyPanelContent(
                activeViewIdsBeforeUpdate[slot.Placement],
                selectedViewIdAfterUpdate,
                impactedPackageIds))
            {
                continue;
            }

            panelContentPresenter.Apply(
                slot.Panel,
                slot.Placement,
                selectedViewIdAfterUpdate,
                viewsById,
                middleBarItemCount,
                createHostedViews);
            if (createHostedViews && !string.IsNullOrWhiteSpace(selectedViewIdAfterUpdate))
            {
                viewIdsToNavigate.Add(selectedViewIdAfterUpdate);
            }
        }

        foreach (var viewId in viewIdsToCancel)
        {
            cancelViewNavigation(viewId);
        }
        foreach (var viewId in viewIdsToNavigate)
        {
            notifyViewNavigated(viewId);
        }
    }

    public void RefreshPackageMetadata(
        IReadOnlyList<ShellPlacementSlot> slots,
        IReadOnlySet<string> impactedPackageIds)
    {
        foreach (var slot in slots)
        {
            var selectedViewId = ShellSelectionState.GetSelectedViewId(shellState, slot.Placement);
            if (string.IsNullOrWhiteSpace(selectedViewId)
                || !viewsById.TryGetValue(selectedViewId, out var packageView)
                || !impactedPackageIds.Contains(packageView.PackageId))
            {
                continue;
            }
            panelContentPresenter.RefreshMetadata(
                slot.Panel,
                slot.Placement,
                packageView);
        }
    }

    private IReadOnlyList<ShellItemViewModel> CreateItemsForPlacement(RailPlacement placement, Action<ShellItemViewModel> onSelect)
    {
        return ShellViewOrdering.GetOrderedViewsForPlacement(viewsById.Values, shellState, placement)
            .Select(packageView => createShellItem(packageView, onSelect))
            .ToArray();
    }

    private IReadOnlyList<ShellItemViewModel> CreateItemsForPlacement(
        RailPlacement placement,
        Action<ShellItemViewModel> onSelect,
        PackageIconBarViewModel bar,
        IReadOnlySet<string> impactedPackageIds)
    {
        var existingItemsById = bar.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        return ShellViewOrdering.GetOrderedViewsForPlacement(viewsById.Values, shellState, placement)
            .Select(packageView => !impactedPackageIds.Contains(packageView.PackageId)
                                   && existingItemsById.TryGetValue(packageView.ViewId, out var existingItem)
                ? existingItem
                : createShellItem(packageView, onSelect))
            .ToArray();
    }

    private ShellItemViewModel? RestoreSelection(ShellPlacementSlot slot, string? selectedId)
    {
        var items = slot.Bar.Items;
        var selected = string.IsNullOrWhiteSpace(selectedId)
            ? null
            : items.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected is null && string.IsNullOrWhiteSpace(selectedId)
            && !shellState.HasInitializedLayout && slot.Placement == RailPlacement.Middle && items.Count > 0)
        {
            selected = items[0];
            ShellSelectionState.SetSelectedViewId(shellState, slot.Placement, selected.Id);
        }

        if (selected is null)
        {
            selectionPresenter.Clear(slot.Bar, slot.Placement);
            return null;
        }

        selectionPresenter.Select(slot.Bar, slot.Placement, selected);
        ShellSelectionState.SetSelectedViewId(shellState, slot.Placement, selected.Id);
        return selected;
    }

    private void PruneRetainedHostedViews(ShellPlacementSlot slot)
    {
        var retainedViewIds = viewsById.Values
            .Where(view => view.Placement == slot.Placement)
            .Select(view => view.ViewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        slot.Panel.PruneHostedViews(retainedViewIds);
    }

    private bool IsViewImpacted(string? viewId, IReadOnlySet<string> impactedPackageIds)
    {
        if (string.IsNullOrWhiteSpace(viewId))
        {
            return false;
        }

        return !viewsById.TryGetValue(viewId, out var packageView)
               || impactedPackageIds.Contains(packageView.PackageId);
    }

    private bool ShouldApplyPanelContent(
        string? activeViewIdBeforeUpdate,
        string? selectedViewIdAfterUpdate,
        IReadOnlySet<string> impactedPackageIds)
    {
        return !string.Equals(activeViewIdBeforeUpdate, selectedViewIdAfterUpdate, StringComparison.OrdinalIgnoreCase)
               || IsViewImpacted(selectedViewIdAfterUpdate, impactedPackageIds)
               || IsViewImpacted(activeViewIdBeforeUpdate, impactedPackageIds);
    }
}
