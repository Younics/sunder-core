using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Sunder.App.Views;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

internal sealed class PackageIconBarDragController(
    PackageIconBar owner,
    Border barRoot,
    Func<PackageIconBarViewModel?> viewModelAccessor)
{
    private Point? _dragStartPoint;
    private ShellItemViewModel? _pendingDragItem;
    private Border? _pressedHost;
    private ShellItemViewModel? _draggedItem;
    private string? _draggedViewId;
    private PackageIconBarDragController? _targetController;
    private Border? _previewAnchorHost;
    private int? _targetIndex;
    private PackageIconBarDragLayoutSnapshot? _dragLayoutSnapshot;
    private PackageIconBarViewModel? ViewModel => viewModelAccessor();

    public void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(owner);
        if (point.Properties.IsRightButtonPressed)
        {
            var viewModel = viewModelAccessor();
            if (viewModel is not null
                && TryResolveItemFromSource(e.Source, out var contextHost, out var contextItem)
                && contextHost is not null
                && contextItem is not null)
            {
                ClearPendingGesture();
                PackageIconBarContextMenu.OpenItemMenu(contextHost, viewModel, contextItem);
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

        e.Pointer.Capture(barRoot);
        _dragStartPoint = GetRootPosition(e);
        _pendingDragItem = item;
        _pressedHost = host;
        _draggedViewId = null;
        ClearDropIndicators();
        e.Handled = true;
    }

    public void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, barRoot))
        {
            return;
        }

        if (_pendingDragItem is not null && _dragStartPoint is not null && _draggedViewId is null)
        {
            var currentPosition = GetRootPosition(e);
            if (Math.Abs(currentPosition.X - _dragStartPoint.Value.X) >= PackageIconBarLayoutMetrics.DragThreshold
                || Math.Abs(currentPosition.Y - _dragStartPoint.Value.Y) >= PackageIconBarLayoutMetrics.DragThreshold)
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

    public void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, barRoot))
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

    public void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pendingDragItem is not null || _draggedViewId is not null)
        {
            ClearPendingGesture();
        }
    }

    private void ApplyDropPreview(int? targetIndex, ShellItemViewModel? previewItem)
    {
        ClearDropIndicators();

        var viewModel = viewModelAccessor();
        if (targetIndex is null || viewModel is null)
        {
            return;
        }

        barRoot.Classes.Add("drag-over");
        if (viewModel.IsHorizontal)
        {
            viewModel.ShowPreviewItem(previewItem, targetIndex);
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
        viewModelAccessor()?.ClearPreviewItem();

        if (_previewAnchorHost is not null)
        {
            _previewAnchorHost.Classes.Remove("drop-before");
            _previewAnchorHost.Classes.Remove("drop-after");
        }

        _previewAnchorHost = null;
        barRoot.Classes.Remove("drag-over");
    }

    private void PrepareDragLayoutSnapshot(MainWindow ownerWindow)
    {
        ClearDropIndicators();

        var viewModel = viewModelAccessor();
        if (viewModel is null)
        {
            _dragLayoutSnapshot = null;
            return;
        }

        var orderedHosts = GetOrderedItemHosts(ownerWindow, viewModel).ToArray();
        if (orderedHosts.Length == 0)
        {
            var barOrigin = GetVisualOrigin(barRoot, ownerWindow);
            var center = viewModel.IsHorizontal
                ? new Point(barOrigin.X + barRoot.Bounds.Width / 2, barOrigin.Y + barRoot.Bounds.Height / 2)
                : new Point(barOrigin.X + barRoot.Bounds.Width / 2, barOrigin.Y + 2 + PackageIconBarLayoutMetrics.ItemExtent / 2);
            _dragLayoutSnapshot = new PackageIconBarDragLayoutSnapshot([], [], [center]);
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
            if (viewModel.IsHorizontal)
            {
                midpoints[index] = center.X;
            }
            else
            {
                midpoints[index] = center.Y;
                slotCenters[index] = new Point(center.X, origin.Y + PackageIconBarLayoutMetrics.PreviewGapSize / 2);
            }
        }

        if (!viewModel.IsHorizontal)
        {
            var lastHost = orderedHosts[^1];
            var lastOrigin = GetVisualOrigin(lastHost, ownerWindow);
            slotCenters[^1] = new Point(itemCenters[^1].X, lastOrigin.Y + lastHost.Bounds.Height + PackageIconBarLayoutMetrics.PreviewGapSize / 2);
        }

        _dragLayoutSnapshot = new PackageIconBarDragLayoutSnapshot(orderedHosts, midpoints, slotCenters);
    }

    private void ClearDragLayoutSnapshot()
    {
        _dragLayoutSnapshot = null;
    }

    private static void PrepareDragLayoutSnapshots(MainWindow ownerWindow)
    {
        foreach (var bar in GetBars(ownerWindow))
        {
            bar.DragController.PrepareDragLayoutSnapshot(ownerWindow);
        }
    }

    private static void ClearDragLayoutSnapshots(MainWindow ownerWindow)
    {
        foreach (var bar in GetBars(ownerWindow))
        {
            bar.DragController.ClearDragLayoutSnapshot();
        }
    }

    private void StartLocalDrag(PointerEventArgs e, ShellItemViewModel item)
    {
        if (_draggedViewId is not null)
        {
            return;
        }

        _draggedViewId = item.Id;
        _draggedItem = item;
        if (_pressedHost is not null)
        {
            _pressedHost.IsVisible = false;
        }

        var ownerWindow = GetOwnerWindow();
        if (ownerWindow is not null)
        {
            PrepareDragLayoutSnapshots(ownerWindow);
            ownerWindow.ShowPackageDragGhost(item, item.IsHorizontalBar, GetRootPosition(e));
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
        var targetController = FindTargetController(ownerWindow, rootPosition);
        if (targetController?.ViewModel is null)
        {
            SetDragTarget(null, null);
            return;
        }

        if (targetController._dragLayoutSnapshot is null)
        {
            targetController.PrepareDragLayoutSnapshot(ownerWindow);
        }

        var targetIndex = targetController.GetStableTargetIndex(rootPosition);
        SetDragTarget(targetController, targetIndex);
    }

    private int? GetStableTargetIndex(Point rootPosition)
    {
        var viewModel = viewModelAccessor();
        if (viewModel is null || _dragLayoutSnapshot is null)
        {
            return null;
        }

        return _dragLayoutSnapshot.GetInsertIndex(rootPosition, viewModel.IsHorizontal);
    }

    private void SetDragTarget(PackageIconBarDragController? targetController, int? targetIndex)
    {
        if (ReferenceEquals(_targetController, targetController) && _targetIndex == targetIndex)
        {
            return;
        }

        _targetController?.ClearDropIndicators();
        _targetController = targetController;
        _targetIndex = targetIndex;

        if (targetController is null)
        {
            return;
        }

        targetController.ApplyDropPreview(targetIndex, _draggedItem);
    }

    private void CompleteLocalDrag()
    {
        var draggedViewId = _draggedViewId;
        var targetViewModel = _targetController?.ViewModel;
        var targetIndex = _targetIndex;

        CancelLocalDrag();

        if (draggedViewId is not null && targetViewModel is not null)
        {
            targetViewModel.MoveItem(draggedViewId, targetIndex);
        }
    }

    private void CancelLocalDrag()
    {
        _targetController?.ClearDropIndicators();
        _targetController = null;
        _targetIndex = null;
        _draggedItem = null;
        _draggedViewId = null;
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
        var topLevel = TopLevel.GetTopLevel(owner);
        return topLevel is null ? e.GetPosition(owner) : e.GetPosition(topLevel);
    }

    private void UpdateDragGhostPosition(PointerEventArgs e)
    {
        var ownerWindow = GetOwnerWindow();
        if (ownerWindow is null || _draggedViewId is null)
        {
            return;
        }

        if (_targetController?.ViewModel?.IsHorizontal == true)
        {
            ownerWindow.HidePackageDragGhost();
            return;
        }

        if (_draggedItem is not null)
        {
            ownerWindow.ShowPackageDragGhost(_draggedItem, _draggedItem.IsHorizontalBar, GetRootPosition(e));
        }

        if (_targetController?.TryGetPreviewGhostCenter(_targetIndex, out var previewCenter) == true)
        {
            ownerWindow.MovePackageDragGhost(previewCenter);
            return;
        }

        ownerWindow.MovePackageDragGhost(GetRootPosition(e));
    }

    private MainWindow? GetOwnerWindow()
        => owner.GetSelfAndVisualAncestors().OfType<MainWindow>().FirstOrDefault();

    private IEnumerable<Border> GetOrderedItemHosts(MainWindow ownerWindow, PackageIconBarViewModel viewModel)
    {
        var hosts = barRoot.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.IsVisible && border.Classes.Contains("package-bar-item-host"))
            .ToArray();

        return viewModel.IsHorizontal
            ? hosts.OrderBy(host => GetVisualOrigin(host, ownerWindow).X)
            : hosts.OrderBy(host => GetVisualOrigin(host, ownerWindow).Y);
    }

    private static PackageIconBarDragController? FindTargetController(MainWindow ownerWindow, Point rootPosition)
        => GetBars(ownerWindow)
            .Select(bar => bar.DragController)
            .FirstOrDefault(controller => controller.IsPointerWithinBar(ownerWindow, rootPosition));

    private bool IsPointerWithinBar(MainWindow ownerWindow, Point rootPosition)
    {
        var viewModel = viewModelAccessor();
        if (!owner.IsVisible || !barRoot.IsVisible || viewModel is null)
        {
            return false;
        }

        var origin = GetVisualOrigin(barRoot, ownerWindow);
        var rect = viewModel.IsHorizontal
            ? new Rect(origin.X - 12, origin.Y - 10, barRoot.Bounds.Width + 24, barRoot.Bounds.Height + 20)
            : new Rect(origin, barRoot.Bounds.Size);
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
            .Where(bar => bar.IsVisible && bar.IsBarRootVisible);
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
