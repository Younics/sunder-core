using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

internal sealed class MainWindowLayoutController
{
    private readonly Grid _shellContentGrid;
    private readonly Grid _topContentGrid;
    private readonly Grid _bottomContentGrid;
    private readonly GridSplitter _leftColumnGridSplitter;
    private readonly GridSplitter _rightColumnGridSplitter;
    private readonly GridSplitter _bottomRowGridSplitter;
    private readonly GridSplitter _bottomColumnGridSplitter;
    private readonly Func<MainWindowViewModel?> _viewModelAccessor;

    public MainWindowLayoutController(
        Grid shellContentGrid,
        Grid topContentGrid,
        Grid bottomContentGrid,
        GridSplitter leftColumnGridSplitter,
        GridSplitter rightColumnGridSplitter,
        GridSplitter bottomRowGridSplitter,
        GridSplitter bottomColumnGridSplitter,
        Func<MainWindowViewModel?> viewModelAccessor)
    {
        _shellContentGrid = shellContentGrid;
        _topContentGrid = topContentGrid;
        _bottomContentGrid = bottomContentGrid;
        _leftColumnGridSplitter = leftColumnGridSplitter;
        _rightColumnGridSplitter = rightColumnGridSplitter;
        _bottomRowGridSplitter = bottomRowGridSplitter;
        _bottomColumnGridSplitter = bottomColumnGridSplitter;
        _viewModelAccessor = viewModelAccessor;

        _leftColumnGridSplitter.DragDelta += LayoutTopColumnSplitter_OnDragDelta;
        _leftColumnGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
        _rightColumnGridSplitter.DragDelta += LayoutTopColumnSplitter_OnDragDelta;
        _rightColumnGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
        _bottomRowGridSplitter.DragDelta += LayoutBottomSplitter_OnDragDelta;
        _bottomRowGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
        _bottomColumnGridSplitter.DragDelta += LayoutBottomSplitter_OnDragDelta;
        _bottomColumnGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
    }

    public void ApplyAdaptiveLayout()
    {
        var viewModel = _viewModelAccessor();
        if (viewModel is null)
        {
            return;
        }

        var hasLeftTop = viewModel.HasLeftTopPanelContent;
        var hasRightTop = viewModel.HasRightTopPanelContent;
        var hasLeftBottom = viewModel.HasLeftBottomPanelContent;
        var hasRightBottom = viewModel.HasRightBottomPanelContent;
        var hasBottom = hasLeftBottom || hasRightBottom;
        var hasBottomSplit = hasLeftBottom && hasRightBottom;

        var leftSplitterWidth = hasLeftTop ? ShellLayoutCalculator.SplitterThickness : 0;
        var rightSplitterWidth = hasRightTop ? ShellLayoutCalculator.SplitterThickness : 0;
        var bottomColumnSplitterWidth = hasBottomSplit ? ShellLayoutCalculator.SplitterThickness : 0;

        var topWidths = CalculateTopColumnWidths(
            GetTopContentWidth(),
            viewModel.LeftPanelWidth,
            viewModel.RightPanelWidth,
            hasLeftTop,
            hasRightTop,
            leftSplitterWidth,
            rightSplitterWidth);
        var verticalWeights = ShellLayoutCalculator.CalculateVerticalWeights(viewModel.TopRowHeightRatio, hasBottom);
        var bottomWeights = ShellLayoutCalculator.CalculateBottomColumnWeights(viewModel.BottomSplitRatio, hasLeftBottom, hasRightBottom);

        _shellContentGrid.RowDefinitions[0].Height = ToStarLength(verticalWeights.TopWeight);
        _shellContentGrid.RowDefinitions[1].Height = new GridLength(verticalWeights.SplitterHeight);
        _shellContentGrid.RowDefinitions[2].Height = ToStarLength(verticalWeights.BottomWeight);

        _topContentGrid.ColumnDefinitions[0].Width = ToPixelLength(topWidths.LeftWidth);
        _topContentGrid.ColumnDefinitions[1].Width = new GridLength(leftSplitterWidth);
        _topContentGrid.ColumnDefinitions[2].Width = ToStarLength(1);
        _topContentGrid.ColumnDefinitions[3].Width = new GridLength(rightSplitterWidth);
        _topContentGrid.ColumnDefinitions[4].Width = ToPixelLength(topWidths.RightWidth);

        _bottomContentGrid.ColumnDefinitions[0].Width = ToStarLength(bottomWeights.LeftWeight);
        _bottomContentGrid.ColumnDefinitions[1].Width = new GridLength(bottomColumnSplitterWidth);
        _bottomContentGrid.ColumnDefinitions[2].Width = ToStarLength(bottomWeights.RightWeight);

        _leftColumnGridSplitter.IsVisible = hasLeftTop;
        _rightColumnGridSplitter.IsVisible = hasRightTop;
        _bottomRowGridSplitter.IsVisible = hasBottom;
        _bottomColumnGridSplitter.IsVisible = hasBottomSplit;
    }

    internal static (double LeftWidth, double RightWidth) CalculateTopColumnWidths(
        double totalWidth,
        double requestedLeftWidth,
        double requestedRightWidth,
        bool hasLeftPanel,
        bool hasRightPanel,
        double leftSplitterWidth = ShellLayoutCalculator.SplitterThickness,
        double rightSplitterWidth = ShellLayoutCalculator.SplitterThickness)
        => ShellLayoutCalculator.CalculateTopColumnWidths(
            totalWidth,
            requestedLeftWidth,
            requestedRightWidth,
            hasLeftPanel,
            hasRightPanel,
            leftSplitterWidth,
            rightSplitterWidth);

    internal static double CalculateResizableExtent(double totalExtent, params double[] fixedExtents)
        => ShellLayoutCalculator.CalculateResizableExtent(totalExtent, fixedExtents);

    private static GridLength ToStarLength(double weight)
    {
        if (weight <= 0)
        {
            return new GridLength(0);
        }

        return new GridLength(weight, GridUnitType.Star);
    }

    private static GridLength ToPixelLength(double width) => new(Math.Max(0, width));

    private void LayoutSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        _viewModelAccessor()?.CommitLayoutState();
        ApplyAdaptiveLayout();
    }

    private void LayoutBottomSplitter_OnDragDelta(object? sender, VectorEventArgs e)
    {
        var viewModel = _viewModelAccessor();
        if (viewModel is null)
        {
            return;
        }

        if (ReferenceEquals(sender, _bottomRowGridSplitter))
        {
            var resizableHeight = CalculateResizableExtent(
                _shellContentGrid.Bounds.Height,
                _bottomRowGridSplitter.IsVisible ? ShellLayoutCalculator.SplitterThickness : 0);
            if (resizableHeight <= 0)
            {
                return;
            }

            viewModel.AdjustLiveTopRowHeightRatio(e.Vector.Y / resizableHeight);
        }
        else if (ReferenceEquals(sender, _bottomColumnGridSplitter))
        {
            var resizableWidth = CalculateResizableExtent(
                _bottomContentGrid.Bounds.Width,
                _bottomColumnGridSplitter.IsVisible ? ShellLayoutCalculator.SplitterThickness : 0);
            if (resizableWidth <= 0)
            {
                return;
            }

            viewModel.AdjustLiveBottomSplitRatio(e.Vector.X / resizableWidth);
        }

        ApplyAdaptiveLayout();
    }

    private void LayoutTopColumnSplitter_OnDragDelta(object? sender, VectorEventArgs e)
    {
        var viewModel = _viewModelAccessor();
        if (viewModel is null || GetTopContentWidth() <= 0)
        {
            return;
        }

        if (ReferenceEquals(sender, _leftColumnGridSplitter))
        {
            viewModel.AdjustLiveLeftPanelWidth(e.Vector.X, GetMaximumLeftPanelWidth(viewModel));
        }
        else if (ReferenceEquals(sender, _rightColumnGridSplitter))
        {
            viewModel.AdjustLiveRightPanelWidth(-e.Vector.X, GetMaximumRightPanelWidth(viewModel));
        }

        ApplyAdaptiveLayout();
    }

    private double GetTopContentWidth()
        => _topContentGrid.Bounds.Width > 0 ? _topContentGrid.Bounds.Width : _shellContentGrid.Bounds.Width;

    private double GetMaximumLeftPanelWidth(MainWindowViewModel viewModel)
    {
        var resizableWidth = CalculateResizableExtent(
            GetTopContentWidth(),
            viewModel.HasLeftTopPanelContent ? ShellLayoutCalculator.SplitterThickness : 0,
            viewModel.HasRightTopPanelContent ? ShellLayoutCalculator.SplitterThickness : 0);
        return resizableWidth - ShellLayoutCalculator.MinimumMiddleContentWidth - (viewModel.HasRightTopPanelContent ? viewModel.RightPanelWidth : 0);
    }

    private double GetMaximumRightPanelWidth(MainWindowViewModel viewModel)
    {
        var resizableWidth = CalculateResizableExtent(
            GetTopContentWidth(),
            viewModel.HasLeftTopPanelContent ? ShellLayoutCalculator.SplitterThickness : 0,
            viewModel.HasRightTopPanelContent ? ShellLayoutCalculator.SplitterThickness : 0);
        return resizableWidth - ShellLayoutCalculator.MinimumMiddleContentWidth - (viewModel.HasLeftTopPanelContent ? viewModel.LeftPanelWidth : 0);
    }
}
