namespace Sunder.App.Views.Controls;

internal static class PackageIconBarLayoutMetrics
{
    public const double SideLaneInset = 10;
    public const double DragThreshold = 6;
    public const double ItemExtent = 38;
    public const double ItemSpacing = 10;

    public static int CalculateVisibleCapacity(double availableLength)
        => Math.Max(1, (int)Math.Floor((availableLength + ItemSpacing) / (ItemExtent + ItemSpacing)));
}
