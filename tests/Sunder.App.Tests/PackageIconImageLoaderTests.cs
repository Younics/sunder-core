using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageIconImageLoaderTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/vnd.microsoft.icon")]
    public void ResolveIconFormat_TreatsImageContentTypesAsRaster(string contentType)
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Raster,
            PackageIconImageLoader.ResolveIconFormat(contentType));
    }

    [Fact]
    public void ResolveIconFormat_TreatsSvgContentTypeAsSvg()
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Svg,
            PackageIconImageLoader.ResolveIconFormat("image/svg+xml"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    public void ResolveIconFormat_RejectsNonImageContentTypes(string? contentType)
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Unsupported,
            PackageIconImageLoader.ResolveIconFormat(contentType));
    }
}
