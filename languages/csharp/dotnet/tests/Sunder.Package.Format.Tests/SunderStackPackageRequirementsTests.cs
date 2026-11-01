using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderStackPackageRequirementsTests
{
    [Fact]
    public void Merge_PreservesStrongestMinimumAndRequiredAcrossEditedAndGeneratedContent()
    {
        var result = Assert.Single(SunderStackPackageRequirements.Merge(
        [
            new() { PackageId = "example.package", InstallTag = "preview", MinimumVersion = "0.5.0", Required = false },
            new() { PackageId = "example.package", InstallTag = "preview", MinimumVersion = "2.0.0", Required = true },
        ]));
        Assert.Equal("2.0.0", result.MinimumVersion);
        Assert.Equal("preview", result.InstallTag);
        Assert.True(result.Required);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("LATEST")]
    public void Merge_NeverSilentlyChoosesBetweenDifferentTags(string tag)
        => Assert.Throws<InvalidDataException>(() => SunderStackPackageRequirements.Merge(
        [
            new() { PackageId = "example.package", InstallTag = "latest", Required = true },
            new() { PackageId = "example.package", InstallTag = tag, Required = true },
        ]));
}
