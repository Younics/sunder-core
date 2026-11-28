using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views.Controls;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
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
    public void SetItemsPreservingExisting_WhenOrderChanges_MovesItemsWithoutResettingCollection()
    {
        var bar = CreateBar();
        var chat = CreateItem("agent.chat");
        var workspaces = CreateItem("agent.workspaces");
        var subsessions = CreateItem("agent.subsessions");
        var actions = new List<NotifyCollectionChangedAction>();
        bar.SetItems([chat, workspaces, subsessions]);
        bar.Items.CollectionChanged += (_, args) => actions.Add(args.Action);

        bar.SetItemsPreservingExisting([subsessions, chat, workspaces]);

        Assert.Equal([subsessions, chat, workspaces], bar.Items);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Contains(NotifyCollectionChangedAction.Move, actions);
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
    public Task CrossBarDragReleasesTheRetiredIconInThePreviewAndGhost()
    {
        return HeadlessAppCollection.Session.Dispatch(() =>
        {
            var source = CreateBar();
            var target = CreateBar();
            target.SetItems([CreateItem("agent.chat"), CreateItem("agent.workspaces")]);
            var bar = new PackageIconBar { DataContext = target };
            var overlay = new PackageDragOverlay();
            var window = new Window { Width = 400, Height = 100, Content = new Grid { Children = { bar, overlay } } };
            window.Show();
            try
            {
                PackageIconView previewIcon;
                var ghostIcon = Assert.Single(overlay.GetVisualDescendants().OfType<PackageIconView>());
                using (var bitmap = new WriteableBitmap(new PixelSize(24, 24), new Vector(96, 96)))
                {
                    var dragged = CreateItem("agent.subsessions", "S");
                    dragged.IconImage = bitmap;
                    source.SetItems([dragged]);
                    target.ShowPreviewItem(dragged, insertIndex: 1);
                    overlay.ShowPackageDragGhost(dragged, compact: true, new Point(60, 40));
                    window.UpdateLayout();

                    Assert.Equal(["agent.chat", "__drag-preview__", "agent.workspaces"], target.VisibleItems.Select(item => item.Id));
                    Assert.True(target.VisibleItems[1].IsDragPreview);
                    previewIcon = Assert.Single(bar.GetVisualDescendants().OfType<PackageIconView>(),
                        icon => icon.IsEffectivelyVisible && ReferenceEquals(icon.IconImage, bitmap));
                    Assert.Same(bitmap, ghostIcon.IconImage);
                    // A package commit replaces the source item before retiring its generation's bitmap.
                    source.SetItems([CreateItem(dragged.Id)]);
                }

                window.Width += 20;
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                foreach (var icon in new[] { previewIcon, ghostIcon })
                {
                    Assert.Null(Assert.Single(icon.GetVisualDescendants().OfType<Image>()).Source);
                    Assert.True(icon.ShowBareGlyphFallback);
                    Assert.Equal("S", icon.Glyph);
                }
            }
            finally { window.Close(); }
        }, default);
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
