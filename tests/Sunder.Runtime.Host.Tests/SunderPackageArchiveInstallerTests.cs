using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.PackageManagement;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class SunderPackageArchiveInstallerTests
{
    [Fact]
    public async Task InstallFromPathAsync_WhenArchiveIsValid_InstallsPackageAndRecordsState()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");

        var result = await installer.InstallFromPathAsync(archivePath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var installedPackages = await store.ListAsync();
        var installedPackage = Assert.Single(installedPackages);
        Assert.Equal("test.package", installedPackage.PackageId);
        Assert.True(File.Exists(installedPackage.ManifestPath));
        Assert.True(File.Exists(installedPackage.EntryAssemblyPath));
    }

    [Fact]
    public async Task InstallFromPathAsync_WhenArchiveContainsUnsafePath_ReturnsFailure()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var archivePath = Path.Combine(root, "unsafe.sunderpkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        var result = await installer.InstallFromPathAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains("unsafe path", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallFromPathAsync_WhenContentIndexHashDoesNotMatch_ReturnsValidationFailure()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", corruptHash: true);

        var result = await installer.InstallFromPathAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpgradeFromPathAsync_WhenArchiveIsNewer_ReplacesInstalledPackage()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var originalArchivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var upgradedArchivePath = CreatePackageArchive(root, "test.package", "1.1.0");

        var installResult = await installer.InstallFromPathAsync(originalArchivePath);
        Assert.True(installResult.Success, string.Join(Environment.NewLine, installResult.Errors));
        var originalInstallPath = Assert.Single(await store.ListAsync()).InstallPath;

        var upgradeResult = await installer.UpgradeFromPathAsync("test.package", upgradedArchivePath);

        Assert.True(upgradeResult.Success, string.Join(Environment.NewLine, upgradeResult.Errors));
        var installedPackage = Assert.Single(await store.ListAsync());
        Assert.Equal("1.1.0", installedPackage.Version);
        Assert.True(File.Exists(installedPackage.EntryAssemblyPath));
        Assert.False(Directory.Exists(originalInstallPath));
    }

    [Fact]
    public async Task UpgradeFromPathAsync_WhenArchivePackageIdDoesNotMatch_ReturnsFailure()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");

        var result = await installer.UpgradeFromPathAsync("other.package", archivePath);

        Assert.False(result.Success);
        Assert.Contains("does not match", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeFromPathAsync_WhenArchiveIsSameVersionAndReinstallNotAllowed_ReturnsFailureAndKeepsPackage()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var originalArchivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var reinstallArchivePath = CreatePackageArchive(root, "test.package", "1.0.0");

        var installResult = await installer.InstallFromPathAsync(originalArchivePath);
        Assert.True(installResult.Success, string.Join(Environment.NewLine, installResult.Errors));
        var originalInstallPath = Assert.Single(await store.ListAsync()).InstallPath;

        var result = await installer.UpgradeFromPathAsync("test.package", reinstallArchivePath);

        Assert.False(result.Success);
        Assert.Contains("already installed", result.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(originalInstallPath));
        Assert.Equal("1.0.0", Assert.Single(await store.ListAsync()).Version);
    }

    private static string CreatePackageArchive(string root, string packageId, string version, bool corruptHash = false)
    {
        var sourceRoot = Path.Combine(root, "package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "lib"));

        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new RuntimePackageManifest
        {
            ManifestVersion = 1,
            Id = packageId,
            Name = "Test Package",
            Version = version,
            EntryAssembly = "Test.Package.dll",
        }));

        var entryAssemblyPath = Path.Combine(sourceRoot, "payload", "lib", "Test.Package.dll");
        File.WriteAllText(entryAssemblyPath, "not a real assembly");

        var contentIndex = new SunderPackageContentIndex(
            1,
            Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("content-index.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => CreateIndexEntry(sourceRoot, path, corruptHash && path == entryAssemblyPath))
                .ToArray());
        File.WriteAllText(Path.Combine(sourceRoot, "manifest", "content-index.json"), JsonSerializer.Serialize(contentIndex));

        var archivePath = Path.Combine(root, $"{packageId}.{version}.{Guid.NewGuid():N}.sunderpkg");
        ZipFile.CreateFromDirectory(sourceRoot, archivePath);
        return archivePath;
    }

    private static SunderPackageContentIndexEntry CreateIndexEntry(string sourceRoot, string path, bool corruptHash)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (corruptHash)
        {
            hash = new string('0', hash.Length);
        }

        return new SunderPackageContentIndexEntry(
            Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'),
            hash,
            new FileInfo(path).Length,
            Role: "runtime");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
