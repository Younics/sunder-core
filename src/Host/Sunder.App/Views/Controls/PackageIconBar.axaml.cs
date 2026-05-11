using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sunder.App.Views;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

public partial class PackageIconBar : UserControl
{
    private const double SideLaneInset = 10;

    private sealed class DragLayoutSnapshot
    {
        public DragLayoutSnapshot(Border[] orderedHosts, double[] midpoints, Point[] slotCenters, double horizontalSlotPitch)
        {
            OrderedHosts = orderedHosts;
            Midpoints = midpoints;
            SlotCenters = slotCenters;
            HorizontalSlotPitch = horizontalSlotPitch;
        }

        public Border[] OrderedHosts { get; }

        public double[] Midpoints { get; }

        public Point[] SlotCenters { get; }

        public double HorizontalSlotPitch { get; }

        public int GetInsertIndex(Point rootPosition, bool isHorizontal)
        {
            var axis = isHorizontal ? rootPosition.X : rootPosition.Y;
            var index = 0;
            while (index < Midpoints.Length && axis >= Midpoints[index])
            {
                index++;
            }

            return index;
        }

        public bool TryGetSlotCenter(int insertIndex, out Point center)
        {
            if ((uint)insertIndex < (uint)SlotCenters.Length)
            {
                center = SlotCenters[insertIndex];
                return true;
            }

            center = default;
            return false;
        }

        public bool TryGetPreviewAnchor(int insertIndex, out Border? host, out bool insertAfter)
        {
            if (OrderedHosts.Length == 0)
            {
                host = null;
                insertAfter = false;
                return insertIndex == 0;
            }

            if (insertIndex < 0 || insertIndex > OrderedHosts.Length)
            {
                host = null;
                insertAfter = false;
                return false;
            }

            if (insertIndex < OrderedHosts.Length)
            {
                host = OrderedHosts[insertIndex];
                insertAfter = false;
                return true;
            }

            host = OrderedHosts[^1];
            insertAfter = true;
            return true;
        }
    }

    private const double DragThreshold = 6;
    private const double ItemExtent = 38;
    private const double ItemSpacing = 10;
    private const double PreviewGapSize = 46;

    private PackageIconBarViewModel? ViewModel => DataContext as PackageIconBarViewModel;
    private Point? _dragStartPoint;
    private ShellItemViewModel? _pendingDragItem;
    private Border? _pressedHost;
    private string? _draggedViewId;
    private string? _draggedGlyph;
    private bool _draggedCompact;
    private PackageIconBar? _targetBar;
    private Border? _previewAnchorHost;
    private int? _targetIndex;
    private DragLayoutSnapshot? _dragLayoutSnapshot;

    public PackageIconBar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => UpdateVisibleCapacity();
        DataContextChanged += (_, _) => UpdateVisibleCapacity();
        SizeChanged += (_, _) => UpdateVisibleCapacity();

        BarRoot.AddHandler(InputElement.PointerPressedEvent, BarRoot_OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        BarRoot.AddHandler(InputElement.PointerMovedEvent, BarRoot_OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        BarRoot.AddHandler(InputElement.PointerReleasedEvent, BarRoot_OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        BarRoot.AddHandler(InputElement.PointerCaptureLostEvent, BarRoot_OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
    }

    private void UpdateVisibleCapacity()
    {
        if (ViewModel is null)
        {
            return;
        }

        var availableLength = ViewModel.IsHorizontal
            ? Bounds.Width
            : Math.Max(0, Bounds.Height - GetVerticalContentInset());
        if (availableLength <= 0)
        {
            return;
        }

        var visibleCapacity = Math.Max(1, (int)Math.Floor((availableLength + ItemSpacing) / (ItemExtent + ItemSpacing)));
        ViewModel.UpdateVisibleCapacity(visibleCapacity);
    }

    private double GetVerticalContentInset()
    {
        if (ViewModel?.IsVertical != true)
        {
            return 0;
        }

        var inset = 0d;
        if (Classes.Contains("side-top-lane"))
        {
            inset += SideLaneInset;
        }

        if (Classes.Contains("side-bottom-lane"))
        {
            inset += SideLaneInset;
        }

        return inset;
    }

    private void BarRoot_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (TryResolveItemFromSource(e.Source, out var contextHost, out var contextItem) && contextHost is not null && contextItem is not null)
            {
                ClearPendingGesture();
                ShowItemContextMenu(contextHost, contextItem);
                e.Handled = true;
            }

            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!TryResolveItemFromSource(e.Source, out var host, out var item))
        {
            return;
        }

        e.Pointer.Capture(BarRoot);
        _dragStartPoint = GetRootPosition(e);
        _pendingDragItem = item;
        _pressedHost = host;
        _draggedViewId = null;
        ClearDropIndicators();
        e.Handled = true;
    }

    private void ShowItemContextMenu(Control host, ShellItemViewModel item)
    {
        var reloadItem = new MenuItem
        {
            Header = "Reload",
            Classes = { "package-bar-context-menu-item" },
        };
        reloadItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.ReloadItemAsync(item.Id);
            }
        };

        var removeItem = new MenuItem
        {
            Header = "Remove",
            Classes = { "package-bar-context-menu-item" },
        };
        removeItem.Click += (_, _) => ViewModel?.RemoveItem(item.Id);

        var menu = new ContextMenu
        {
            ItemsSource = new[] { reloadItem, removeItem },
            Classes = { "package-bar-context-menu" },
        };

        host.ContextMenu = menu;
        menu.Open(host);
    }

    private void BarRoot_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, BarRoot))
        {
            return;
        }

        if (_pendingDragItem is not null && _dragStartPoint is not null && _draggedViewId is null)
        {
            var currentPosition = GetRootPosition(e);
            if (Math.Abs(currentPosition.X - _dragStartPoint.Value.X) >= DragThreshold
                || Math.Abs(currentPosition.Y - _dragStartPoint.Value.Y) >= DragThreshold)
            {
                StartLocalDrag(e, _pendingDragItem);
            }

            UpdateDragGhostPosition(e);
            e.Handled = true;
            return;
        }

        if (_draggedViewId is null)
        {
            return;
        }

        UpdateDragTarget(e);
        UpdateDragGhostPosition(e);
        e.Handled = true;
    }

    private void BarRoot_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, BarRoot))
        {
            return;
        }

        e.Pointer.Capture(null);

        if (_draggedViewId is not null)
        {
            CompleteLocalDrag();
            e.Handled = true;
        }
        else if (_pendingDragItem is not null)
        {
            _pendingDragItem.Activate();
            e.Handled = true;
        }

        ClearPendingGesture();
    }

    private void BarRoot_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pendingDragItem is not null || _draggedViewId is not null)
        {
            ClearPendingGesture();
        }
    }

    private void OverflowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || ViewModel is null || ViewModel.OverflowItems.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu
        {
            ItemsSource = ViewModel.OverflowItems.Select(item => new MenuItem
            {
                Header = item.MenuText,
                Command = item.SelectCommand,
            }).ToArray(),
        };

        control.ContextMenu = menu;
        menu.Open(control);
    }

    private void ApplyDropPreview(int? targetIndex, string? previewViewId, string? previewGlyph)
    {
        ClearDropIndicators();

        if (targetIndex is null || ViewModel is null)
        {
            return;
        }

        BarRoot.Classes.Add("drag-over");
        if (ViewModel.IsHorizontal)
        {
            ViewModel.ShowPreviewItem(previewViewId, previewGlyph ?? string.Empty, targetIndex);
            return;
        }

        if (_dragLayoutSnapshot?.TryGetPreviewAnchor(targetIndex.Value, out var host, out var insertAfter) != true || host is null)
        {
            return;
        }

        _previewAnchorHost = host;
        _previewAnchorHost.Classes.Add(insertAfter ? "drop-after" : "drop-before");
    }

    private void ClearDropIndicators()
    {
        ResetPreviewTransforms();
        ViewModel?.ClearPreviewItem();

        if (_previewAnchorHost is not null)
        {
            _previewAnchorHost.Classes.Remove("drop-before");
            _previewAnchorHost.Classes.Remove("drop-after");
        }

        _previewAnchorHost = null;
        BarRoot.Classes.Remove("drag-over");
    }

    private void ApplyHorizontalDropPreview(int targetIndex)
    {
        if (_dragLayoutSnapshot is null)
        {
            return;
        }

        for (var index = 0; index < _dragLayoutSnapshot.OrderedHosts.Length; index++)
        {
            var halfPitch = _dragLayoutSnapshot.HorizontalSlotPitch / 2;
            var offset = index < targetIndex
                ? -halfPitch
                : halfPitch;
            _dragLayoutSnapshot.OrderedHosts[index].RenderTransform = CreateHorizontalPreviewTransform(offset);
        }
    }

    private void ResetPreviewTransforms()
    {
        if (_dragLayoutSnapshot is null)
        {
            return;
        }

        foreach (var host in _dragLayoutSnapshot.OrderedHosts)
        {
            host.RenderTransform = CreateIdentityPreviewTransform();
        }
    }

    private static ITransform CreateIdentityPreviewTransform()
        => new TranslateTransform();

    private static ITransform CreateHorizontalPreviewTransform(double offset)
        => new TranslateTransform { X = offset };

    private void PrepareDragLayoutSnapshot(MainWindow ownerWindow)
    {
        ClearDropIndicators();

        if (ViewModel is null)
        {
            _dragLayoutSnapshot = null;
            return;
        }

        var orderedHosts = GetOrderedItemHosts(ownerWindow).ToArray();
        if (orderedHosts.Length == 0)
        {
            var barOrigin = GetVisualOrigin(BarRoot, ownerWindow);
            var center = ViewModel.IsHorizontal
                ? new Point(barOrigin.X + BarRoot.Bounds.Width / 2, barOrigin.Y + BarRoot.Bounds.Height / 2)
                : new Point(barOrigin.X + BarRoot.Bounds.Width / 2, barOrigin.Y + 2 + ItemExtent / 2);
            _dragLayoutSnapshot = new DragLayoutSnapshot([], [], [center], 0);
            return;
        }

        var midpoints = new double[orderedHosts.Length];
        var itemCenters = new Point[orderedHosts.Length];
        var slotCenters = new Point[orderedHosts.Length + 1];
        for (var index = 0; index < orderedHosts.Length; index++)
        {
            var host = orderedHosts[index];
            var origin = GetVisualOrigin(host, ownerWindow);
            var center = new Point(origin.X + host.Bounds.Width / 2, origin.Y + host.Bounds.Height / 2);
            itemCenters[index] = center;
            if (ViewModel.IsHorizontal)
            {
                midpoints[index] = center.X;
            }
            else
            {
                midpoints[index] = center.Y;
                slotCenters[index] = new Point(center.X, origin.Y + PreviewGapSize / 2);
            }
        }

        var horizontalSlotPitch = 0d;
        if (ViewModel.IsHorizontal)
        {
            horizontalSlotPitch = GetHorizontalSlotPitch(orderedHosts, itemCenters);
            var halfPitch = horizontalSlotPitch / 2;
            slotCenters[0] = new Point(itemCenters[0].X - halfPitch, itemCenters[0].Y);
            for (var index = 1; index < itemCenters.Length; index++)
            {
                slotCenters[index] = new Point((itemCenters[index - 1].X + itemCenters[index].X) / 2, itemCenters[index].Y);
            }

            slotCenters[^1] = new Point(itemCenters[^1].X + halfPitch, itemCenters[^1].Y);
        }
        else
        {
            var lastHost = orderedHosts[^1];
            var lastOrigin = GetVisualOrigin(lastHost, ownerWindow);
            slotCenters[^1] = new Point(itemCenters[^1].X, lastOrigin.Y + lastHost.Bounds.Height + PreviewGapSize / 2);
        }

        _dragLayoutSnapshot = new DragLayoutSnapshot(orderedHosts, midpoints, slotCenters, horizontalSlotPitch);
    }

    private static double GetHorizontalSlotPitch(IReadOnlyList<Border> orderedHosts, IReadOnlyList<Point> itemCenters)
    {
        if (itemCenters.Count > 1)
        {
            var totalDistance = 0d;
            for (var index = 1; index < itemCenters.Count; index++)
            {
                totalDistance += itemCenters[index].X - itemCenters[index - 1].X;
            }

            return totalDistance / (itemCenters.Count - 1);
        }

        return orderedHosts[0].Bounds.Width + ItemSpacing;
    }

    private static void PrepareDragLayoutSnapshots(MainWindow ownerWindow)
    {
        foreach (var bar in GetBars(ownerWindow))
        {
            bar.PrepareDragLayoutSnapshot(ownerWindow);
        }
    }

    private static void ClearDragLayoutSnapshots(MainWindow ownerWindow)
    {
        foreach (var bar in GetBars(ownerWindow))
        {
            bar._dragLayoutSnapshot = null;
        }
    }

    private void StartLocalDrag(PointerEventArgs e, ShellItemViewModel item)
    {
        if (_draggedViewId is not null)
        {
            return;
        }

        _draggedViewId = item.Id;
        _draggedGlyph = item.Glyph;
        _draggedCompact = item.IsHorizontalBar;
        if (_pressedHost is not null)
        {
            _pressedHost.IsVisible = false;
        }

        var ownerWindow = GetOwnerWindow();
        if (ownerWindow is not null)
        {
            PrepareDragLayoutSnapshots(ownerWindow);
            ownerWindow.ShowPackageDragGhost(item.Glyph, item.IsHorizontalBar, GetRootPosition(e));
        }

        UpdateDragTarget(e);
        UpdateDragGhostPosition(e);
        _pendingDragItem = null;
        _dragStartPoint = null;
    }

    private void UpdateDragTarget(PointerEventArgs e)
    {
        var ownerWindow = GetOwnerWindow();
        if (_draggedViewId is null || ownerWindow is null)
        {
            return;
        }

        var rootPosition = e.GetPosition(ownerWindow);
        var targetBar = FindTargetBar(ownerWindow, rootPosition);
        if (targetBar?.ViewModel is null)
        {
            SetDragTarget(null, null);
            return;
        }

        if (targetBar._dragLayoutSnapshot is null)
        {
            targetBar.PrepareDragLayoutSnapshot(ownerWindow);
        }

        var targetIndex = targetBar.GetStableTargetIndex(rootPosition);
        SetDragTarget(targetBar, targetIndex);
    }

    private int? GetStableTargetIndex(Point rootPosition)
    {
        if (ViewModel is null || _dragLayoutSnapshot is null)
        {
            return null;
        }

        return _dragLayoutSnapshot.GetInsertIndex(rootPosition, ViewModel.IsHorizontal);
    }

    private void SetDragTarget(PackageIconBar? targetBar, int? targetIndex)
    {
        if (ReferenceEquals(_targetBar, targetBar) && _targetIndex == targetIndex)
        {
            return;
        }

        _targetBar?.ClearDropIndicators();
        _targetBar = targetBar;
        _targetIndex = targetIndex;

        if (targetBar is null)
        {
            return;
        }

        targetBar.ApplyDropPreview(targetIndex, _draggedViewId, _draggedGlyph);
    }

    private void CompleteLocalDrag()
    {
        var draggedViewId = _draggedViewId;
        var targetViewModel = _targetBar?.ViewModel;
        var targetIndex = _targetIndex;

        CancelLocalDrag();

        if (draggedViewId is not null && targetViewModel is not null)
        {
            targetViewModel.MoveItem(draggedViewId, targetIndex);
        }
    }

    private void CancelLocalDrag()
    {
        _targetBar?.ClearDropIndicators();
        _targetBar = null;
        _targetIndex = null;
        _draggedViewId = null;
        _draggedGlyph = null;
        _draggedCompact = false;
        var ownerWindow = GetOwnerWindow();
        if (ownerWindow is not null)
        {
            ClearDragLayoutSnapshots(ownerWindow);
            ownerWindow.HidePackageDragGhost();
        }

        if (_pressedHost is not null)
        {
            _pressedHost.IsVisible = true;
        }
    }

    private void ClearPendingGesture()
    {
        CancelLocalDrag();
        _dragStartPoint = null;
        _pendingDragItem = null;
        _pressedHost = null;
    }

    private Point GetRootPosition(PointerEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        return topLevel is null ? e.GetPosition(this) : e.GetPosition(topLevel);
    }

    private void UpdateDragGhostPosition(PointerEventArgs e)
    {
        var ownerWindow = GetOwnerWindow();
        if (ownerWindow is null || _draggedViewId is null)
        {
            return;
        }

        if (_targetBar?.ViewModel?.IsHorizontal == true)
        {
            ownerWindow.HidePackageDragGhost();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_draggedGlyph))
        {
            ownerWindow.ShowPackageDragGhost(_draggedGlyph, _draggedCompact, GetRootPosition(e));
        }

        if (_targetBar?.TryGetPreviewGhostCenter(_targetIndex, out var previewCenter) == true)
        {
            ownerWindow.MovePackageDragGhost(previewCenter);
            return;
        }

        ownerWindow.MovePackageDragGhost(GetRootPosition(e));
    }

    private MainWindow? GetOwnerWindow()
        => this.GetSelfAndVisualAncestors().OfType<MainWindow>().FirstOrDefault();

    private IEnumerable<Border> GetOrderedItemHosts(MainWindow ownerWindow)
    {
        var hosts = BarRoot.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.IsVisible && border.Classes.Contains("package-bar-item-host"))
            .ToArray();

        return ViewModel?.IsHorizontal == true
            ? hosts.OrderBy(host => GetVisualOrigin(host, ownerWindow).X)
            : hosts.OrderBy(host => GetVisualOrigin(host, ownerWindow).Y);
    }

    private static PackageIconBar? FindTargetBar(MainWindow ownerWindow, Point rootPosition)
    {
        return ownerWindow.GetVisualDescendants()
            .OfType<PackageIconBar>()
            .FirstOrDefault(bar => bar.IsPointerWithinBar(ownerWindow, rootPosition));
    }

    private bool IsPointerWithinBar(MainWindow ownerWindow, Point rootPosition)
    {
        if (!IsVisible || !BarRoot.IsVisible || ViewModel is null)
        {
            return false;
        }

        var origin = GetVisualOrigin(BarRoot, ownerWindow);
        var rect = ViewModel?.IsHorizontal == true
            ? new Rect(origin.X - 12, origin.Y - 10, BarRoot.Bounds.Width + 24, BarRoot.Bounds.Height + 20)
            : new Rect(origin, BarRoot.Bounds.Size);
        return rect.Contains(rootPosition);
    }

    private bool TryGetPreviewGhostCenter(int? targetIndex, out Point center)
    {
        center = default;
        if (!targetIndex.HasValue || _dragLayoutSnapshot is null)
        {
            return false;
        }

        return _dragLayoutSnapshot.TryGetSlotCenter(targetIndex.Value, out center);
    }

    private static IEnumerable<PackageIconBar> GetBars(MainWindow ownerWindow)
    {
        return ownerWindow.GetVisualDescendants()
            .OfType<PackageIconBar>()
            .Where(bar => bar.IsVisible && bar.BarRoot.IsVisible);
    }

    private static Point GetVisualOrigin(Visual visual, Visual ancestor)
    {
        return visual.TranslatePoint(default, ancestor) ?? default;
    }

    private static bool TryResolveItemFromSource(object? source, out Border? host, out ShellItemViewModel? item)
    {
        host = null;
        item = null;

        if (source is not Visual visual)
        {
            return false;
        }

        host = visual.GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("package-bar-item-host"));

        if (host?.DataContext is not ShellItemViewModel shellItem || shellItem.IsDragPreview)
        {
            host = null;
            return false;
        }

        item = shellItem;
        return true;
    }
}
