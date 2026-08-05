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
    private readonly Border _leftTopPanelBorder;
    private readonly Border _rightTopPanelBorder;
    private readonly Border _leftBottomPanelBorder;
    private readonly Border _rightBottomPanelBorder;
    private readonly Func<MainWindowViewModel?> _viewModelAccessor;
    private LayoutGeometryState? _appliedState;

    public MainWindowLayoutController(
        Grid shellContentGrid,
        Grid topContentGrid,
        Grid bottomContentGrid,
        GridSplitter leftColumnGridSplitter,
        GridSplitter rightColumnGridSplitter,
        GridSplitter bottomRowGridSplitter,
        GridSplitter bottomColumnGridSplitter,
        Border leftTopPanelBorder,
        Border rightTopPanelBorder,
        Border leftBottomPanelBorder,
        Border rightBottomPanelBorder,
        Func<MainWindowViewModel?> viewModelAccessor)
    {
        _shellContentGrid = shellContentGrid;
        _topContentGrid = topContentGrid;
        _bottomContentGrid = bottomContentGrid;
        _leftColumnGridSplitter = leftColumnGridSplitter;
        _rightColumnGridSplitter = rightColumnGridSplitter;
        _bottomRowGridSplitter = bottomRowGridSplitter;
        _bottomColumnGridSplitter = bottomColumnGridSplitter;
        _leftTopPanelBorder = leftTopPanelBorder;
        _rightTopPanelBorder = rightTopPanelBorder;
        _leftBottomPanelBorder = leftBottomPanelBorder;
        _rightBottomPanelBorder = rightBottomPanelBorder;
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

    internal int GeometryCommitCount { get; private set; }

    public void ApplyAdaptiveLayout()
    {
        var viewModel = _viewModelAccessor();
        if (viewModel is null)
        {
            return;
        }

        var hasLeftTop = viewModel.LeftTopPanel.IsDockLayoutVisible;
        var hasRightTop = viewModel.RightTopPanel.IsDockLayoutVisible;
        var hasLeftBottom = viewModel.LeftBottomPanel.IsDockLayoutVisible;
        var hasRightBottom = viewModel.RightBottomPanel.IsDockLayoutVisible;
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

        var state = new LayoutGeometryState(
            ToStarLength(verticalWeights.TopWeight),
            new GridLength(verticalWeights.SplitterHeight),
            ToStarLength(verticalWeights.BottomWeight),
            ToPixelLength(topWidths.LeftWidth),
            new GridLength(leftSplitterWidth),
            ToStarLength(1),
            new GridLength(rightSplitterWidth),
            ToPixelLength(topWidths.RightWidth),
            ToStarLength(bottomWeights.LeftWeight),
            new GridLength(bottomColumnSplitterWidth),
            ToStarLength(bottomWeights.RightWeight),
            hasLeftTop,
            hasRightTop,
            hasLeftBottom,
            hasRightBottom,
            hasBottom,
            hasBottomSplit);
        if (_appliedState == state)
        {
            return;
        }

        SetHeight(_shellContentGrid.RowDefinitions[0], state.TopRowHeight);
        SetHeight(_shellContentGrid.RowDefinitions[1], state.BottomSplitterHeight);
        SetHeight(_shellContentGrid.RowDefinitions[2], state.BottomRowHeight);

        SetWidth(_topContentGrid.ColumnDefinitions[0], state.LeftPanelWidth);
        SetWidth(_topContentGrid.ColumnDefinitions[1], state.LeftSplitterWidth);
        SetWidth(_topContentGrid.ColumnDefinitions[2], state.MiddlePanelWidth);
        SetWidth(_topContentGrid.ColumnDefinitions[3], state.RightSplitterWidth);
        SetWidth(_topContentGrid.ColumnDefinitions[4], state.RightPanelWidth);

        SetWidth(_bottomContentGrid.ColumnDefinitions[0], state.LeftBottomWidth);
        SetWidth(_bottomContentGrid.ColumnDefinitions[1], state.BottomColumnSplitterWidth);
        SetWidth(_bottomContentGrid.ColumnDefinitions[2], state.RightBottomWidth);

        SetVisible(_leftTopPanelBorder, state.HasLeftTop);
        SetVisible(_rightTopPanelBorder, state.HasRightTop);
        SetVisible(_leftBottomPanelBorder, state.HasLeftBottom);
        SetVisible(_rightBottomPanelBorder, state.HasRightBottom);
        SetVisible(_leftColumnGridSplitter, state.HasLeftTop);
        SetVisible(_rightColumnGridSplitter, state.HasRightTop);
        SetVisible(_bottomRowGridSplitter, state.HasBottom);
        SetVisible(_bottomColumnGridSplitter, state.HasBottomSplit);

        _appliedState = state;
        GeometryCommitCount++;
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

    private static void SetHeight(RowDefinition definition, GridLength height)
    {
        if (definition.Height != height)
        {
            definition.Height = height;
        }
    }

    private static void SetWidth(ColumnDefinition definition, GridLength width)
    {
        if (definition.Width != width)
        {
            definition.Width = width;
        }
    }

    private static void SetVisible(Visual visual, bool isVisible)
    {
        if (visual.IsVisible != isVisible)
        {
            visual.IsVisible = isVisible;
        }
    }

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
            viewModel.LeftTopPanel.IsDockLayoutVisible ? ShellLayoutCalculator.SplitterThickness : 0,
            viewModel.RightTopPanel.IsDockLayoutVisible ? ShellLayoutCalculator.SplitterThickness : 0);
        return resizableWidth - ShellLayoutCalculator.MinimumMiddleContentWidth - (viewModel.RightTopPanel.IsDockLayoutVisible ? viewModel.RightPanelWidth : 0);
    }

    private double GetMaximumRightPanelWidth(MainWindowViewModel viewModel)
    {
        var resizableWidth = CalculateResizableExtent(
            GetTopContentWidth(),
            viewModel.LeftTopPanel.IsDockLayoutVisible ? ShellLayoutCalculator.SplitterThickness : 0,
            viewModel.RightTopPanel.IsDockLayoutVisible ? ShellLayoutCalculator.SplitterThickness : 0);
        return resizableWidth - ShellLayoutCalculator.MinimumMiddleContentWidth - (viewModel.LeftTopPanel.IsDockLayoutVisible ? viewModel.LeftPanelWidth : 0);
    }

    private sealed record LayoutGeometryState(
        GridLength TopRowHeight,
        GridLength BottomSplitterHeight,
        GridLength BottomRowHeight,
        GridLength LeftPanelWidth,
        GridLength LeftSplitterWidth,
        GridLength MiddlePanelWidth,
        GridLength RightSplitterWidth,
        GridLength RightPanelWidth,
        GridLength LeftBottomWidth,
        GridLength BottomColumnSplitterWidth,
        GridLength RightBottomWidth,
        bool HasLeftTop,
        bool HasRightTop,
        bool HasLeftBottom,
        bool HasRightBottom,
        bool HasBottom,
        bool HasBottomSplit);
}
