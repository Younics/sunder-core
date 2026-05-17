using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageIconBarViewModelTests
{
    [Fact]
    public void ShowPreviewItem_WhenDraggedItemHasOnlyIconImage_ShowsPreview()
    {
        var bar = CreateBar();
        var draggedItem = CreateItem("agent.icon", glyph: string.Empty, iconImage: new FakeImage());

        bar.ShowPreviewItem(draggedItem, insertIndex: 0);

        var preview = Assert.Single(bar.VisibleItems);
        Assert.True(preview.IsDragPreview);
        Assert.True(preview.HasIconImage);
        Assert.Equal(string.Empty, preview.Glyph);
    }

    [Fact]
    public void ShowPreviewItem_WhenDraggedItemHasNoGlyphOrIcon_DoesNotShowPreview()
    {
        var bar = CreateBar();
        var draggedItem = CreateItem("agent.empty", glyph: string.Empty, iconImage: null);

        bar.ShowPreviewItem(draggedItem, insertIndex: 0);

        Assert.Empty(bar.VisibleItems);
    }

    private static PackageIconBarViewModel CreateBar()
        => new(
            RailPlacement.Middle,
            Orientation.Horizontal,
            (_, _, _) => { },
            _ => ValueTask.FromResult(false),
            _ => false);

    private static ShellItemViewModel CreateItem(string id, string glyph, IImage? iconImage)
        => new(
            id,
            glyph,
            iconUri: null,
            title: id,
            packageDisplayName: "Agent",
            toolTipText: id,
            RailPlacement.Middle,
            _ => { },
            iconImage: iconImage,
            ownsIconImage: false);

    private sealed class FakeImage : IImage
    {
        public Size Size => new(16, 16);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
