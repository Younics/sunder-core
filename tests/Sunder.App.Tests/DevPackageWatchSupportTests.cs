using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class DevPackageWatchSupportTests
{
    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("package.tmp")]
    [InlineData("package.swp")]
    [InlineData("package.lock")]
    public void ShouldIgnorePath_IgnoresTemporaryFiles(string fileName)
    {
        Assert.True(DevPackageWatchSupport.ShouldIgnorePath(Path.Combine("package", fileName)));
    }

    [Fact]
    public void IsLoadableDevPackageFolder_RequiresManifestAndOptionalLibraryFolder()
    {
        var folder = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(folder, "sunder-package.json"), "{}");

            Assert.True(DevPackageWatchSupport.IsLoadableDevPackageFolder(folder));
            Assert.False(DevPackageWatchSupport.IsLoadableDevPackageFolder(folder, requireLibraryFolder: true));

            Directory.CreateDirectory(Path.Combine(folder, "lib"));

            Assert.True(DevPackageWatchSupport.IsLoadableDevPackageFolder(folder, requireLibraryFolder: true));
        }
        finally
        {
            TryDeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task WaitForStableFoldersAsync_ReturnsTrueForStableLoadableFolder()
    {
        var folder = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(folder, "sunder-package.json"), "{}");
            var libFolder = Path.Combine(folder, "lib");
            Directory.CreateDirectory(libFolder);
            File.WriteAllText(Path.Combine(libFolder, "package.dll"), "placeholder");

            var stable = await DevPackageWatchSupport.WaitForStableFoldersAsync(
                [folder],
                NoDelayAsync,
                CancellationToken.None,
                requireLibraryFolder: true);

            Assert.True(stable);
        }
        finally
        {
            TryDeleteDirectory(folder);
        }
    }

    private static Task NoDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
