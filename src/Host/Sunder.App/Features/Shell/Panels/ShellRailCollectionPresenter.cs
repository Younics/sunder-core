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
            slot.Panel.HostedView = null;
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

    private IReadOnlyList<ShellItemViewModel> CreateItemsForPlacement(RailPlacement placement, Action<ShellItemViewModel> onSelect)
    {
        return ShellViewOrdering.GetOrderedViewsForPlacement(viewsById.Values, shellState, placement)
            .Select(packageView => createShellItem(packageView, onSelect))
            .ToArray();
    }

    private void RestoreSelection(ShellPlacementSlot slot, string? selectedId)
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
            return;
        }

        selectionPresenter.Select(slot.Bar, slot.Placement, selected);
    }
}
