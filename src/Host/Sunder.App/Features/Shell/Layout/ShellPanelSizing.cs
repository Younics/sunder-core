namespace Sunder.App.Features.Shell.Layout;

internal static class ShellPanelSizing
{
    private const double MinimumPanelWidth = 180;
    private const double MaximumPanelWidth = 1200;
    private const double MaximumTopRowRatio = 1.0;

    public static double ClampPanelWidth(double value, double maximumWidth)
    {
        var upperBound = double.IsNaN(maximumWidth) || double.IsInfinity(maximumWidth) || maximumWidth <= 0
            ? MaximumPanelWidth
            : Math.Min(maximumWidth, MaximumPanelWidth);
        upperBound = Math.Max(MinimumPanelWidth, upperBound);
        return Math.Clamp(value, MinimumPanelWidth, upperBound);
    }

    public static double ClampTopRowRatio(double value)
        => Math.Clamp(value, 0, MaximumTopRowRatio);

    public static double ClampBottomSplitRatio(double value)
        => Math.Clamp(value, 0.01, 0.99);
}
