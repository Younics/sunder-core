using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

public sealed class SunderWindowSizingTests
{
    [Fact]
    public void CalculateDefaultSize_WhenScreenCanFitLoadingDesign_ReturnsLoadingDesignSize()
    {
        var size = CalculateLoadingSize(1800, 1100);

        Assert.Equal(SunderWindowSizing.LoadingDesignWidth, size.Size.Width);
        Assert.Equal(SunderWindowSizing.LoadingDesignHeight, size.Size.Height);
    }

    [Fact]
    public void CalculateDefaultSize_WhenLoadingScreenIsSmall_ScalesDownWithinWorkingAreaRatio()
    {
        var size = CalculateLoadingSize(1280, 800);

        Assert.Equal(716, size.Size.Width);
        Assert.Equal(402, size.Size.Height);
        Assert.True(size.Size.Width <= 1280 * 0.56);
        Assert.True(size.Size.Height <= 800 * 0.52);
    }

    [Fact]
    public void CalculateDefaultSize_ConvertsScreenPixelsThroughScaling()
    {
        var size = SunderWindowSizing.CalculateDefaultSize(
            2560,
            1600,
            scaling: 2,
            LoadingProfile);

        Assert.Equal(716, size.Size.Width);
        Assert.Equal(402, size.Size.Height);
    }

    [Fact]
    public void CalculateDefaultSize_WhenMainScreenIsSmall_ClampsDefaultAndMinimumSize()
    {
        var profile = new SunderWindowSizeProfile(
            DesignWidth: 1600,
            DesignHeight: 920,
            MinimumWidth: 1040,
            MinimumHeight: 680,
            MaximumWorkingAreaWidthRatio: 0.94,
            MaximumWorkingAreaHeightRatio: 0.90,
            PreserveAspectRatio: false);

        var result = SunderWindowSizing.CalculateDefaultSize(1280, 800, scaling: 1, profile);

        Assert.Equal(1203, result.Size.Width);
        Assert.Equal(720, result.Size.Height);
        Assert.Equal(1040, result.MinimumSize.Width);
        Assert.Equal(680, result.MinimumSize.Height);
    }

    [Fact]
    public void CalculateDefaultSize_WhenSecondaryScreenIsSmallerThanNominalMinimum_LowersEffectiveMinimum()
    {
        var profile = new SunderWindowSizeProfile(
            DesignWidth: 1200,
            DesignHeight: 820,
            MinimumWidth: 980,
            MinimumHeight: 680,
            MaximumWorkingAreaWidthRatio: 0.88,
            MaximumWorkingAreaHeightRatio: 0.86,
            PreserveAspectRatio: false);

        var result = SunderWindowSizing.CalculateDefaultSize(1024, 640, scaling: 1, profile);

        Assert.Equal(901, result.Size.Width);
        Assert.Equal(550, result.Size.Height);
        Assert.Equal(result.Size.Width, result.MinimumSize.Width);
        Assert.Equal(result.Size.Height, result.MinimumSize.Height);
    }

    [Fact]
    public void ClampSizeToWorkingArea_WhenSavedPlacementIsTooLarge_ShrinksToWorkingArea()
    {
        var size = SunderWindowSizing.ClampSizeToWorkingArea(
            width: 1800,
            height: 1100,
            minimumWidth: 980,
            minimumHeight: 680,
            workingAreaPixelWidth: 1280,
            workingAreaPixelHeight: 800,
            scaling: 1);

        Assert.Equal(1280, size.Width);
        Assert.Equal(800, size.Height);
    }

    [Fact]
    public void CalculateDefaultSize_WhenScreenSizeIsInvalid_ReturnsDesignSize()
    {
        var size = SunderWindowSizing.CalculateDefaultSize(double.NaN, 800, scaling: 1, LoadingProfile);

        Assert.Equal(SunderWindowSizing.LoadingDesignWidth, size.Size.Width);
        Assert.Equal(SunderWindowSizing.LoadingDesignHeight, size.Size.Height);
    }

    private static SunderWindowSizeResult CalculateLoadingSize(double width, double height)
        => SunderWindowSizing.CalculateDefaultSize(width, height, scaling: 1, LoadingProfile);

    private static readonly SunderWindowSizeProfile LoadingProfile = new(
        SunderWindowSizing.LoadingDesignWidth,
        SunderWindowSizing.LoadingDesignHeight,
        MinimumWidth: 1,
        MinimumHeight: 1,
        MaximumWorkingAreaWidthRatio: 0.56,
        MaximumWorkingAreaHeightRatio: 0.52,
        PreserveAspectRatio: true);
}
