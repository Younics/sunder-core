using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellLayoutCalculatorTests
{
    [Theory]
    [InlineData(900)]
    [InlineData(1600)]
    [InlineData(2600)]
    [InlineData(5000)]
    public void MiddleMinimumIsReachableAtEveryWindowSize(double width)
    {
        var resized = ShellLayoutCalculator.ResizeTopColumn(width, 360, 360, false, true, false, width);
        Assert.Equal(320, width - 4 - resized.RightWidth, 8);
        Assert.Equal(resized, ShellLayoutCalculator.CalculateTopColumnWidths(width, 360, resized.RightWidth, false, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompressedDragStartsAtDisplayedWidthAndLeavesOtherPaneStationary(bool left)
    {
        var before = ShellLayoutCalculator.CalculateTopColumnWidths(1100, 500, 1700, true, true);
        var after = ShellLayoutCalculator.ResizeTopColumn(1100, 500, 1700, true, true, left, -10);
        Assert.Equal(before.LeftWidth - (left ? 10 : 0), after.LeftWidth, 8);
        Assert.Equal(before.RightWidth - (left ? 0 : 10), after.RightWidth, 8);
    }

    [Fact]
    public void CompressionPreservesMinimaBeforeReducingExcessSpace()
    {
        var widths = ShellLayoutCalculator.CalculateTopColumnWidths(900, 180, 1200, true, true);
        Assert.Equal(180, widths.LeftWidth);
        Assert.Equal(392, widths.RightWidth);
        var tiny = ShellLayoutCalculator.CalculateTopColumnWidths(348, 180, 1200, true, true);
        Assert.Equal(90, tiny.LeftWidth);
        Assert.Equal(90, tiny.RightWidth);
    }

    [Fact]
    public void LargeFinitePreferencesCannotOverflowCompression()
    {
        var widths = ShellLayoutCalculator.CalculateTopColumnWidths(1200, double.MaxValue, double.MaxValue, true, true);
        Assert.Equal(436, widths.LeftWidth);
        Assert.Equal(436, widths.RightWidth);
    }

    [Fact]
    public void WindowCompressionDoesNotMutatePreferredSizes()
    {
        var small = ShellLayoutCalculator.CalculateTopColumnWidths(1000, 400, 1600, true, true);
        Assert.True(small.RightWidth < 1600);
        var restored = ShellLayoutCalculator.CalculateTopColumnWidths(3000, 400, 1600, true, true);
        Assert.Equal((400d, 1600d), restored);
    }
}
