using Sunder.App.ViewModels;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackagePlatformsViewModelTests
{
    [Theory]
    [InlineData("win-x64", "Windows x64")]
    [InlineData("win-arm64", "Windows ARM64")]
    [InlineData("linux-x64", "Linux x64")]
    [InlineData("linux-arm64", "Linux ARM64")]
    [InlineData("osx-x64", "macOS Intel")]
    [InlineData("osx-arm64", "macOS Apple Silicon")]
    public void DisplaysExactPlatformNamesForSingleRolePackages(string rid, string expected)
    {
        foreach (var role in new[] { "app", "runtime" })
        {
            var view = PackagePlatformsViewModel.Create([(role, rid)], rid, "2.0.0");
            Assert.Equal(expected, view.Summary);
            Assert.True(view.SupportsCurrentPlatform);
            Assert.Contains(rid, Assert.Single(view.Roles).Platforms, StringComparison.Ordinal);
            Assert.Equal("For version 2.0.0", view.VersionText);
        }
    }

    [Fact]
    public void RequiresEveryDeclaredRoleWithoutArchitectureFallback()
    {
        var view = PackagePlatformsViewModel.Create(
            [("runtime", "win-x64"), ("runtime", "osx-arm64"), ("app", "win-x64")], "osx-arm64", "1.0.0");
        Assert.Equal("Windows x64", view.Summary);
        Assert.True(view.IsUnsupported);
        Assert.Contains("partial platform support", view.DeviceSummary, StringComparison.Ordinal);
        Assert.Equal(2, view.Roles.Count);
        Assert.Contains("macOS Apple Silicon", view.Roles.Single(role => role.Role == "Background worker").Platforms, StringComparison.Ordinal);
        Assert.False(PackagePlatformsViewModel.Create([("app", "osx-x64")], "osx-arm64", "1.0.0").SupportsCurrentPlatform);
        Assert.Equal("No platform includes all declared roles.",
            PackagePlatformsViewModel.Create([("app", "win-x64"), ("runtime", "osx-arm64")], "win-x64", "1.0.0").Summary);
    }

    [Fact]
    public void MissingInformationDoesNotClaimPlatformIndependence()
    {
        var unknown = PackagePlatformsViewModel.Create(null, "linux-arm64", "1.0.0");
        Assert.Null(unknown.SupportsCurrentPlatform);
        Assert.Equal("Platform information unavailable.", unknown.Summary);
        Assert.False(unknown.HasDeviceSummary);
        var shared = PackagePlatformsViewModel.Create([], "linux-arm64", "1.0.0");
        Assert.True(shared.SupportsCurrentPlatform);
        Assert.Equal("Platform-independent — shared contracts only.", shared.Summary);
    }
}
