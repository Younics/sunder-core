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
    Func<ShellPackageView, Action<ShellItemViewModel>, ShellItemViewModel> createShellItem)
{
    public void Rebuild(IReadOnlyList<ShellPlacementSlot> slots, bool createHostedViews)
    {
        foreach (var slot in slots)
        {
            selectionPresenter.SetSelectedItem(slot.Placement, null);
            slot.Panel.ClearActiveView();
        }

        foreach (var slot in slots)
        {
            slot.Bar.SetItems(CreateItemsForPlacement(slot.Placement, slot.OnSelect));
            ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, slot.Placement, slot.Bar.Items.Select(item => item.Id).ToArray());
        }

        var middleBarItemCount = slots.FirstOrDefault(slot => slot.Placement == RailPlacement.Middle)?.Bar.Items.Count ?? 0;
        foreach (var slot in slots)
        {
            RestoreSelection(slot, ShellSelectionState.GetSelectedViewId(shellState, slot.Placement));
            var selectedViewId = ShellSelectionState.GetSelectedViewId(shellState, slot.Placement);
            panelContentPresenter.Apply(slot.Panel, slot.Placement, selectedViewId, viewsById, middleBarItemCount, createHostedViews);
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
            ShellViewOrderState.SetOrderForPlacement(viewsById.Values, shellState, slot.Placement, slot.Bar.Items.Select(item => item.Id).ToArray());
        }

        var middleBarItemCount = slots.FirstOrDefault(slot => slot.Placement == RailPlacement.Middle)?.Bar.Items.Count ?? 0;
        var selectedViewIdsAfterUpdate = new Dictionary<RailPlacement, string?>();
        foreach (var slot in affectedSlots)
        {
            var selectedViewIdBeforeUpdate = selectedViewIdsBeforeUpdate[slot.Placement];
            var selectedItem = RestoreSelection(slot, selectedViewIdBeforeUpdate);
            selectedViewIdsAfterUpdate[slot.Placement] = selectedItem?.Id;
        }

        foreach (var slot in affectedSlots)
        {
            if (ShouldApplyPanelContent(
                activeViewIdsBeforeUpdate[slot.Placement],
                selectedViewIdsAfterUpdate[slot.Placement],
                impactedPackageIds))
            {
                slot.Panel.ClearActiveView();
            }
        }

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

        if (selected is null && slot.Placement == RailPlacement.Middle && items.Count > 0)
        {
            selected = items[0];
            ShellSelectionState.SetSelectedViewId(shellState, slot.Placement, selected.Id);
        }

        if (selected is null)
        {
            selectionPresenter.Clear(slot.Bar, slot.Placement);
            ShellSelectionState.SetSelectedViewId(shellState, slot.Placement, null);
            return null;
        }

        selectionPresenter.Select(slot.Bar, slot.Placement, selected);
        ShellSelectionState.SetSelectedViewId(shellState, slot.Placement, selected.Id);
        return selected;
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
