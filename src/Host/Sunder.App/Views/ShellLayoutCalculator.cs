namespace Sunder.App.Views;

internal static class ShellLayoutCalculator
{
    public const double SplitterThickness = 4;
    public const double MinimumMiddleContentWidth = 320;
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

        var leftWidth = hasLeftPanel ? Math.Max(0, requestedLeftWidth) : 0;
        var rightWidth = hasRightPanel ? Math.Max(0, requestedRightWidth) : 0;
        if (totalWidth <= 0)
        {
            return (leftWidth, rightWidth);
        }

        var fixedWidth = (hasLeftPanel ? leftSplitterWidth : 0) + (hasRightPanel ? rightSplitterWidth : 0);
        var resizableWidth = Math.Max(0, totalWidth - fixedWidth);
        var maximumSideWidth = Math.Max(0, resizableWidth - MinimumMiddleContentWidth);
        var requestedSideWidth = leftWidth + rightWidth;
        if (requestedSideWidth <= maximumSideWidth || requestedSideWidth <= 0)
        {
            return (leftWidth, rightWidth);
        }

        var scale = maximumSideWidth / requestedSideWidth;
        return (leftWidth * scale, rightWidth * scale);
    }

    public static (double TopWeight, double SplitterHeight, double BottomWeight) CalculateVerticalWeights(
        double requestedTopRatio,
        bool hasBottom)
    {
        if (!hasBottom)
        {
            return (1, 0, 0);
        }

        var topWeight = Math.Clamp(requestedTopRatio, MinimumVerticalRegionRatio, 1 - MinimumVerticalRegionRatio);
        return (topWeight, SplitterThickness, 1 - topWeight);
    }

    public static (double LeftWeight, double RightWeight) CalculateBottomColumnWeights(
        double requestedLeftRatio,
        bool hasLeftBottom,
        bool hasRightBottom)
    {
        if (hasLeftBottom && hasRightBottom)
        {
            var leftWeight = Math.Clamp(requestedLeftRatio, 0.01, 0.99);
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
