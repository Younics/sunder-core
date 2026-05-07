using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageAssetPathResolverTests
{
    [Fact]
    public void TryResolveDevAssetPath_ResolvesAssetsPrefixUnderAssetsRoot()
    {
        var root = CreateTempDirectory();
        var iconPath = Path.Combine(root, "assets", "icons", "agent.svg");
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        File.WriteAllText(iconPath, "<svg />");

        var resolvedPath = PackageAssetPathResolver.TryResolveDevAssetPath(root, "assets/icons/agent.svg");

        Assert.Equal(Path.GetFullPath(iconPath), resolvedPath);
    }

    [Fact]
    public void TryResolveInstalledAssetPath_RejectsTraversalOutsideAssetsRoot()
    {
        var installPath = CreateTempDirectory();
        var escapedPath = Path.Combine(installPath, "payload", "agent.svg");
        Directory.CreateDirectory(Path.GetDirectoryName(escapedPath)!);
        File.WriteAllText(escapedPath, "<svg />");

        var resolvedPath = PackageAssetPathResolver.TryResolveInstalledAssetPath(installPath, "../agent.svg");

        Assert.Null(resolvedPath);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
