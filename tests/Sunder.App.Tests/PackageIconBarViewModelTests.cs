using Avalonia.Layout;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageIconBarViewModelTests
{
    [Fact]
    public void UpdateVisibleCapacity_WhenItemsExceedCapacity_ReservesSlotForOverflowButton()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces"), CreateItem("agent.subsessions")]);

        bar.UpdateVisibleCapacity(2);

        Assert.Equal(["agent.chat"], bar.VisibleItems.Select(item => item.Id).ToArray());
        Assert.Equal(["agent.workspaces", "agent.subsessions"], bar.OverflowItems.Select(item => item.Id).ToArray());
        Assert.True(bar.HasOverflow);
    }

    [Fact]
    public void UpdateVisibleCapacity_WhenCapacityCoversItems_ShowsAllItems()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces")]);

        bar.UpdateVisibleCapacity(2);

        Assert.Equal(["agent.chat", "agent.workspaces"], bar.VisibleItems.Select(item => item.Id).ToArray());
        Assert.Empty(bar.OverflowItems);
        Assert.False(bar.HasOverflow);
    }

    [Fact]
    public void BeginDragLayout_RemovesDraggedVisibleItemUntilDragEnds()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces"), CreateItem("agent.subsessions")]);

        bar.BeginDragLayout("agent.workspaces");

        Assert.Equal(["agent.chat", "agent.subsessions"], bar.VisibleItems.Select(item => item.Id).ToArray());

        bar.EndDragLayout();

        Assert.Equal(["agent.chat", "agent.workspaces", "agent.subsessions"], bar.VisibleItems.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void BeginDragLayout_DoesNotPromoteOverflowItemIntoVisibleSlot()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces"), CreateItem("agent.subsessions")]);
        bar.UpdateVisibleCapacity(2);

        bar.BeginDragLayout("agent.chat");

        Assert.Empty(bar.VisibleItems);
        Assert.Equal(["agent.workspaces", "agent.subsessions"], bar.OverflowItems.Select(item => item.Id).ToArray());

        bar.EndDragLayout();

        Assert.Equal(["agent.chat"], bar.VisibleItems.Select(item => item.Id).ToArray());
        Assert.Equal(["agent.workspaces", "agent.subsessions"], bar.OverflowItems.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void ShowPreviewItem_AfterBeginDragLayout_InsertsPreviewWithoutSourcePlaceholder()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces"), CreateItem("agent.subsessions")]);
        var draggedItem = bar.Items[1];

        bar.BeginDragLayout(draggedItem.Id);
        bar.ShowPreviewItem(draggedItem, insertIndex: 1);

        Assert.Equal(["agent.chat", "__drag-preview__", "agent.subsessions"], bar.VisibleItems.Select(item => item.Id).ToArray());
        Assert.True(bar.VisibleItems[1].IsDragPreview);
    }

    [Fact]
    public void ShowPreviewItem_WhenDraggedFromAnotherBar_InsertsPreviewWithoutRemovingTargetItems()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces")]);
        var draggedItem = CreateItem("agent.subsessions");

        bar.ShowPreviewItem(draggedItem, insertIndex: 1);

        Assert.Equal(["agent.chat", "__drag-preview__", "agent.workspaces"], bar.VisibleItems.Select(item => item.Id).ToArray());
        Assert.True(bar.VisibleItems[1].IsDragPreview);
    }

    [Fact]
    public void ShowPreviewItem_WhenInsertIndexChanges_ReplacesExistingPreviewItem()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces")]);
        var draggedItem = CreateItem("agent.subsessions");

        bar.ShowPreviewItem(draggedItem, insertIndex: 0);
        bar.ShowPreviewItem(draggedItem, insertIndex: 2);

        Assert.Equal(["agent.chat", "agent.workspaces", "__drag-preview__"], bar.VisibleItems.Select(item => item.Id).ToArray());
        Assert.Single(bar.VisibleItems, item => item.IsDragPreview);
    }

    [Fact]
    public void ClearPreviewItem_AfterBeginDragLayout_RestoresSourceFilteredLayout()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces"), CreateItem("agent.subsessions")]);
        var draggedItem = bar.Items[1];

        bar.BeginDragLayout(draggedItem.Id);
        bar.ShowPreviewItem(draggedItem, insertIndex: 1);
        bar.ClearPreviewItem();

        Assert.Equal(["agent.chat", "agent.subsessions"], bar.VisibleItems.Select(item => item.Id).ToArray());

        bar.EndDragLayout();

        Assert.Equal(["agent.chat", "agent.workspaces", "agent.subsessions"], bar.VisibleItems.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void ShowPreviewItem_WhenDraggedItemHasNoGlyphOrIcon_DoesNotShowPreview()
    {
        var bar = CreateBar();
        bar.SetItems([CreateItem("agent.chat")]);
        var draggedItem = CreateItem("agent.empty", glyph: string.Empty);

        bar.ShowPreviewItem(draggedItem, insertIndex: 0);

        Assert.Equal(["agent.chat"], bar.VisibleItems.Select(item => item.Id).ToArray());
    }

    private static PackageIconBarViewModel CreateBar()
        => new(
            RailPlacement.Middle,
            Orientation.Horizontal,
            (_, _, _) => { },
            _ => ValueTask.FromResult(false),
            _ => false);

    private static ShellItemViewModel CreateItem(string id, string? glyph = null)
        => new(
            id,
            glyph: glyph ?? id[0].ToString(),
            iconUri: null,
            title: id,
            packageDisplayName: "Agent",
            toolTipText: id,
            RailPlacement.Middle,
            _ => { },
            ownsIconImage: false);
}
