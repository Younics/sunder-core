using Xunit;

namespace Sunder.App.Tests;

public sealed class MacOsReleasePackagingTests
{
    [Fact]
    public void DmgPresentation_UsesCanonicalSunderThemeAndFinderLayout()
    {
        var root = LocateRepositoryRoot();
        var renderer = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "release",
            "macos",
            "render-sunder-dmg-background.swift"));
        var builder = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "release",
            "macos",
            "create-sunder-dmg.sh"));

        foreach (var color in new[] { "0x121313", "0x171818", "0x1e1f1f", "0xd09132", "0xe7b765" })
        {
            Assert.Contains(color, renderer, StringComparison.Ordinal);
        }
        Assert.Contains("logicalWidth: CGFloat = 820", renderer, StringComparison.Ordinal);
        Assert.Contains("logicalHeight: CGFloat = 460", renderer, StringComparison.Ordinal);
        Assert.Contains("Drag Sunder to Applications", renderer, StringComparison.Ordinal);
        Assert.Contains("Sunder@2x.png", renderer, StringComparison.Ordinal);
        Assert.Contains(".background/Sunder.tiff", builder, StringComparison.Ordinal);
        Assert.Contains("set position of item \"Sunder.app\"", builder, StringComparison.Ordinal);
        Assert.Contains("set position of item \"Applications\"", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void MacRelease_PublishesDmgButRetainsVelopackUpdateAssets()
    {
        var root = LocateRepositoryRoot();
        var packager = File.ReadAllText(Path.Combine(root, "scripts", "release", "package-sunder.sh"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "sunder-app-release.yml"));
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "install", "install.sh"));

        Assert.Contains("--noInst", packager, StringComparison.Ordinal);
        Assert.Contains("create-sunder-dmg.sh", packager, StringComparison.Ordinal);
        Assert.Contains("map(select(.RelativeFileName != $portable", packager, StringComparison.Ordinal);
        Assert.Contains(".Type == \"Full\" or .Type == \"Delta\"", packager, StringComparison.Ordinal);
        Assert.DoesNotContain("portable_entry=", packager, StringComparison.Ordinal);
        Assert.Contains("Sunder-app-osx-x64-$RELEASE_CHANNEL.dmg", workflow, StringComparison.Ordinal);
        Assert.Contains("Sunder-app-osx-arm64-$RELEASE_CHANNEL.dmg", workflow, StringComparison.Ordinal);
        Assert.Contains("*-Setup.pkg|*-Portable.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("vpk \"${upload_args[@]}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Sunder-app-osx-x64-stable.dmg", installer, StringComparison.Ordinal);
        Assert.Contains("Sunder-app-osx-arm64-stable.dmg", installer, StringComparison.Ordinal);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder core repository root.");
    }
}
