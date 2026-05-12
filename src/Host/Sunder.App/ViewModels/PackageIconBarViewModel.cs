using System.Collections.ObjectModel;
using Avalonia.Layout;
using Avalonia.Media;
using Sunder.App.Models;

namespace Sunder.App.ViewModels;

public sealed partial class PackageIconBarViewModel : ViewModelBase
{
    private readonly Action<string, RailPlacement, int?> _onMove;
    private readonly Func<string, ValueTask<bool>> _onReload;
    private readonly Func<string, bool> _onRemove;
    private int _visibleCapacity = int.MaxValue;
    private string? _previewDraggedViewId;
    private string? _previewGlyph;
    private IImage? _previewIconImage;
    private int? _previewInsertIndex;

    public PackageIconBarViewModel(
        RailPlacement placement,
        Orientation orientation,
        Action<string, RailPlacement, int?> onMove,
        Func<string, ValueTask<bool>> onReload,
        Func<string, bool> onRemove)
    {
        Placement = placement;
        LayoutOrientation = orientation;
        _onMove = onMove;
        _onReload = onReload;
        _onRemove = onRemove;
    }

    public RailPlacement Placement { get; }

    public Orientation LayoutOrientation { get; }

    public ObservableCollection<ShellItemViewModel> Items { get; } = [];

    public ObservableCollection<ShellItemViewModel> VisibleItems { get; } = [];

    public ObservableCollection<ShellItemViewModel> OverflowItems { get; } = [];

    public bool HasOverflow => OverflowItems.Count > 0;

    public bool IsHorizontal => LayoutOrientation == Orientation.Horizontal;

    public bool IsVertical => LayoutOrientation == Orientation.Vertical;

    public bool ShowEmptyDropHint => Items.Count == 0;

    public void SetItems(IEnumerable<ShellItemViewModel> items)
    {
        foreach (var item in Items)
        {
            item.Dispose();
        }

        ReplaceCollection(Items, items);
        RefreshVisibleItems();
        OnPropertyChanged(nameof(ShowEmptyDropHint));
    }

    public void UpdateVisibleCapacity(int visibleCapacity)
    {
        var normalizedCapacity = Math.Max(0, visibleCapacity);
        if (_visibleCapacity == normalizedCapacity)
        {
            return;
        }

        _visibleCapacity = normalizedCapacity;
        RefreshVisibleItems();
    }

    public void MoveItem(string viewId, int? targetIndex)
    {
        _onMove(viewId, Placement, targetIndex);
    }

    public ValueTask<bool> ReloadItemAsync(string viewId)
        => _onReload(viewId);

    public bool RemoveItem(string viewId)
        => _onRemove(viewId);

    public void ShowPreviewItem(ShellItemViewModel? draggedItem, int? insertIndex)
    {
        if (draggedItem is null)
        {
            ClearPreviewItem();
            return;
        }

        if (string.Equals(_previewDraggedViewId, draggedItem.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_previewGlyph, draggedItem.Glyph, StringComparison.Ordinal)
            && ReferenceEquals(_previewIconImage, draggedItem.IconImage)
            && _previewInsertIndex == insertIndex)
        {
            return;
        }

        _previewDraggedViewId = draggedItem.Id;
        _previewGlyph = draggedItem.Glyph;
        _previewIconImage = draggedItem.IconImage;
        _previewInsertIndex = insertIndex;
        RefreshVisibleItems();
    }

    public void ClearPreviewItem()
    {
        if (_previewDraggedViewId is null && _previewGlyph is null && _previewIconImage is null && _previewInsertIndex is null)
        {
            return;
        }

        _previewDraggedViewId = null;
        _previewGlyph = null;
        _previewIconImage = null;
        _previewInsertIndex = null;
        RefreshVisibleItems();
    }

    private void RefreshVisibleItems()
    {
        var itemCount = Items.Count;
        if (itemCount == 0)
        {
            ReplaceCollection(VisibleItems, BuildVisibleItems([]));
            ReplaceCollection(OverflowItems, []);
            OnPropertyChanged(nameof(HasOverflow));
            return;
        }

        if (_visibleCapacity <= 0)
        {
            ReplaceCollection(VisibleItems, BuildVisibleItems([]));
            ReplaceCollection(OverflowItems, Items);
            OnPropertyChanged(nameof(HasOverflow));
            return;
        }

        if (itemCount <= _visibleCapacity)
        {
            ReplaceCollection(VisibleItems, BuildVisibleItems(Items));
            ReplaceCollection(OverflowItems, []);
            OnPropertyChanged(nameof(HasOverflow));
            return;
        }

        var visibleCount = Math.Max(0, _visibleCapacity - 1);
        ReplaceCollection(VisibleItems, BuildVisibleItems(Items.Take(visibleCount)));
        ReplaceCollection(OverflowItems, Items.Skip(visibleCount));
        OnPropertyChanged(nameof(HasOverflow));
    }

    private IEnumerable<ShellItemViewModel> BuildVisibleItems(IEnumerable<ShellItemViewModel> items)
    {
        var visibleItems = items.ToList();
        if (string.IsNullOrWhiteSpace(_previewDraggedViewId) || string.IsNullOrWhiteSpace(_previewGlyph))
        {
            return visibleItems;
        }

        visibleItems = visibleItems
            .Where(item => !string.Equals(item.Id, _previewDraggedViewId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var normalizedIndex = _previewInsertIndex.HasValue
            ? Math.Clamp(_previewInsertIndex.Value, 0, visibleItems.Count)
            : visibleItems.Count;
        visibleItems.Insert(normalizedIndex, new ShellItemViewModel(
            id: "__drag-preview__",
            glyph: _previewGlyph,
            iconUri: null,
            title: string.Empty,
            packageDisplayName: string.Empty,
            toolTipText: string.Empty,
            placement: Placement,
            onSelect: _ => { },
            isDragPreview: true,
            iconImage: _previewIconImage,
            ownsIconImage: false));
        return visibleItems;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
