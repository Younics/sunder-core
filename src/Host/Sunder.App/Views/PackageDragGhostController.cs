using Avalonia;
using Avalonia.Controls;
using Sunder.App.ViewModels;
using Sunder.App.Views.Controls;

namespace Sunder.App.Views;

internal sealed class PackageDragGhostController(Border dragGhost, PackageIconView dragGhostIcon)
{
    public void Show(ShellItemViewModel item, bool compact, Point centerPosition)
    {
        dragGhostIcon.IconImage = item.IconImage;
        dragGhostIcon.HasIconImage = item.HasIconImage;
        dragGhostIcon.Glyph = item.Glyph;
        dragGhostIcon.ShowBareGlyphFallback = item.ShowGlyphFallback;
        dragGhost.Classes.Set("top-bar-surface", compact);
        dragGhost.IsVisible = true;
        Move(centerPosition);
    }

    public void Move(Point centerPosition)
    {
        if (!dragGhost.IsVisible)
        {
            return;
        }

        var width = dragGhost.Bounds.Width > 0 ? dragGhost.Bounds.Width : (dragGhost.Classes.Contains("top-bar-surface") ? 36 : 38);
        var height = dragGhost.Bounds.Height > 0 ? dragGhost.Bounds.Height : (dragGhost.Classes.Contains("top-bar-surface") ? 36 : 38);
        Canvas.SetLeft(dragGhost, centerPosition.X - width / 2);
        Canvas.SetTop(dragGhost, centerPosition.Y - height / 2);
    }

    public void Hide()
    {
        dragGhost.IsVisible = false;
        dragGhostIcon.IconImage = null;
        dragGhostIcon.HasIconImage = false;
        dragGhostIcon.Glyph = string.Empty;
        dragGhostIcon.ShowBareGlyphFallback = false;
    }
}
