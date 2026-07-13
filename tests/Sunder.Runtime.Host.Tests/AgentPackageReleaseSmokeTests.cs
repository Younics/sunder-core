using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class AgentPackageReleaseSmokeTests
{
    [Fact]
    public async Task ReleaseArchives_InstallActivateUnloadReloadAndReinstallAsOneFamily()
    {
        var archiveDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR");
        if (string.IsNullOrWhiteSpace(archiveDirectory))
        {
            return;
        }

        var archives = Directory.GetFiles(Path.GetFullPath(archiveDirectory), "*.sunderpkg")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(archives);

        var root = Path.Combine(Path.GetTempPath(), "sunder-agent-release-smoke", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(root);
        var store = new InstalledPackageStore(paths);
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            new SunderPackageArchiveInstaller(paths));

        try
        {
            await host.InitializeAsync();
            foreach (var archive in archives)
            {
                var result = await host.InstallPackageFromRuntimePathAsync(archive);
                Assert.True(result.Success, $"{Path.GetFileName(archive)}: {string.Join(Environment.NewLine, result.Errors)}");
            }

            var installed = await host.GetInstalledPackagesAsync();
            Assert.Equal(archives.Length, installed.Count);
            var activation = await host.LoadInstalledPackagesAsync();
            Assert.True(activation.Success, string.Join(Environment.NewLine, activation.Errors));
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
            Assert.Contains(host.GetActivePackageUiSnapshots(), snapshot => snapshot.PackageId == "sunder.package.agent");

            foreach (var package in installed.OrderByDescending(static package => package.PackageId, StringComparer.Ordinal))
            {
                var result = await host.UnloadPackageSessionAsync(package.PackageId, PackageSourceKind.Installed);
                Assert.True(result.Success, $"Unload {package.PackageId}: {string.Join(Environment.NewLine, result.Errors)}");
            }
            Assert.Empty(host.GetActivePackages());

            foreach (var package in installed.OrderBy(static package => package.PackageId, StringComparer.Ordinal))
            {
                var result = await host.LoadPackageSessionAsync(new PackageSessionLoadRequest(PackageSourceKind.Installed, package.PackageId));
                Assert.True(result.Success, $"Reload {package.PackageId}: {string.Join(Environment.NewLine, result.Errors)}");
            }
            Assert.Equal(archives.Length, host.GetActivePackages().Count);

            foreach (var package in installed.OrderBy(static package => package.PackageId, StringComparer.Ordinal))
            {
                var expectedFileName = $"{package.PackageId}.{package.Version}.sunderpkg";
                var archive = Assert.Single(archives, path => string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase));
                var result = await host.ReinstallPackageFromRuntimePathAsync(package.PackageId, archive);
                Assert.True(result.Success, $"Reinstall {package.PackageId}: {string.Join(Environment.NewLine, result.Errors)}");
            }
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
        }
        finally
        {
            await host.ShutdownAsync();
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
