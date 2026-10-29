using Sunder.App.Models;

namespace Sunder.App.Views;

internal static class ShellLayoutCalculator
{
    public const double SplitterThickness = 4;
    public const double MinimumMiddleContentWidth = 320;
    public const double MinimumSideContentWidth = 180;
    private const double MinimumVerticalRegionRatio = 0.10;

    public static (double LeftWidth, double RightWidth) CalculateTopColumnWidths(
        double totalWidth,
        double requestedLeftWidth,
        double requestedRightWidth,
        bool hasLeftPanel,
        bool hasRightPanel,
        double leftSplitterWidth = SplitterThickness,
        double rightSplitterWidth = SplitterThickness)
    {
        if (!hasLeftPanel && !hasRightPanel)
        {
            return (0, 0);
        }

        var leftWidth = hasLeftPanel ? NormalizePanelWidth(requestedLeftWidth, ShellState.DefaultLeftPanelWidth) : 0;
        var rightWidth = hasRightPanel ? NormalizePanelWidth(requestedRightWidth, ShellState.DefaultRightPanelWidth) : 0;
        if (totalWidth <= 0)
        {
            return (leftWidth, rightWidth);
        }

        var fixedWidth = (hasLeftPanel ? leftSplitterWidth : 0) + (hasRightPanel ? rightSplitterWidth : 0);
        var resizableWidth = Math.Max(0, totalWidth - fixedWidth);
        var minimumScale = CalculateMinimumScale(resizableWidth, hasLeftPanel, hasRightPanel);
        var leftMinimum = hasLeftPanel ? MinimumSideContentWidth * minimumScale : 0;
        var rightMinimum = hasRightPanel ? MinimumSideContentWidth * minimumScale : 0;
        var maximumSideWidth = Math.Max(0, resizableWidth - MinimumMiddleContentWidth * minimumScale);
        if (leftWidth + rightWidth <= maximumSideWidth)
        {
            return (leftWidth, rightWidth);
        }

        // Only preferred space above the minima is compressed. Normalize the weights
        // before adding them so even very large finite saved widths cannot overflow.
        var leftExtra = Math.Max(0, leftWidth - leftMinimum);
        var rightExtra = Math.Max(0, rightWidth - rightMinimum);
        var largestExtra = Math.Max(leftExtra, rightExtra);
        var budget = Math.Max(0, maximumSideWidth - leftMinimum - rightMinimum);
        var leftWeight = largestExtra > 0 ? leftExtra / largestExtra : 0;
        var rightWeight = largestExtra > 0 ? rightExtra / largestExtra : 0;
        var weight = leftWeight + rightWeight;
        return (leftMinimum + (weight > 0 ? budget * leftWeight / weight : 0),
            rightMinimum + (weight > 0 ? budget * rightWeight / weight : 0));
    }

    public static (double LeftWidth, double RightWidth) ResizeTopColumn(
        double totalWidth, double preferredLeft, double preferredRight,
        bool hasLeft, bool hasRight, bool resizeLeft, double delta)
    {
        var widths = CalculateTopColumnWidths(totalWidth, preferredLeft, preferredRight, hasLeft, hasRight);
        if (!double.IsFinite(delta) || delta == 0 || !(resizeLeft ? hasLeft : hasRight)) return widths;
        var available = CalculateResizableExtent(totalWidth, hasLeft ? SplitterThickness : 0, hasRight ? SplitterThickness : 0);
        var scale = CalculateMinimumScale(available, hasLeft, hasRight);
        var minimum = MinimumSideContentWidth * scale;
        var other = resizeLeft ? widths.RightWidth : widths.LeftWidth;
        var maximum = Math.Max(minimum, available - other - MinimumMiddleContentWidth * scale);
        var resized = Math.Clamp((resizeLeft ? widths.LeftWidth : widths.RightWidth) + delta, minimum, maximum);
        return resizeLeft ? (resized, other) : (other, resized);
    }

    private static double CalculateMinimumScale(double available, bool hasLeft, bool hasRight)
        => Math.Min(1, available / (MinimumMiddleContentWidth
            + (hasLeft ? MinimumSideContentWidth : 0) + (hasRight ? MinimumSideContentWidth : 0)));

    public static double NormalizePanelWidth(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? Math.Max(MinimumSideContentWidth, value) : fallback;

    public static double ClampTopRowRatio(double value)
        => Math.Clamp(double.IsFinite(value) ? value : ShellState.DefaultTopRowHeightRatio,
            MinimumVerticalRegionRatio, 1 - MinimumVerticalRegionRatio);

    public static double ClampBottomSplitRatio(double value)
        => Math.Clamp(double.IsFinite(value) ? value : ShellState.DefaultBottomSplitRatio, 0.01, 0.99);

    public static (double TopWeight, double SplitterHeight, double BottomWeight) CalculateVerticalWeights(
        double requestedTopRatio,
        bool hasBottom)
    {
        if (!hasBottom)
        {
            return (1, 0, 0);
        }

        var topWeight = ClampTopRowRatio(requestedTopRatio);
        return (topWeight, SplitterThickness, 1 - topWeight);
    }

    public static (double LeftWeight, double RightWeight) CalculateBottomColumnWeights(
        double requestedLeftRatio,
        bool hasLeftBottom,
        bool hasRightBottom)
    {
        if (hasLeftBottom && hasRightBottom)
        {
            var leftWeight = ClampBottomSplitRatio(requestedLeftRatio);
            return (leftWeight, 1 - leftWeight);
        }

        if (hasLeftBottom)
        {
            return (1, 0);
        }

        if (hasRightBottom)
        {
            return (0, 1);
        }

        return (0, 0);
    }

    public static double CalculateResizableExtent(double totalExtent, params double[] fixedExtents)
    {
        var resizableExtent = totalExtent;
        foreach (var fixedExtent in fixedExtents)
        {
            if (fixedExtent > 0)
            {
                resizableExtent -= fixedExtent;
            }
        }

        return Math.Max(0, resizableExtent);
    }
}
