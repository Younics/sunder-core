using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
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
        var installer = new SunderPackageArchiveInstaller(paths);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");

        var result = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archivePath));

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
        var installer = new SunderPackageArchiveInstaller(paths);
        var archivePath = Path.Combine(root, "unsafe.sunderpkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        var result = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archivePath));

        Assert.False(result.Success);
        Assert.Contains("unsafe", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallFromPathAsync_WhenContentIndexHashDoesNotMatch_ReturnsValidationFailure()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", corruptHash: true);

        var result = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archivePath));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StagePackageStoreChangesAsync_WhenDiscarded_DoesNotRecordOrInstallPackage()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer, transferStore: transfers);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        await using var archive = File.OpenRead(archivePath);
        var upload = await transfers.CreateUploadAsync(RuntimeUploadKind.Package, archive, archive.Length, null, Path.GetFileName(archivePath), "application/vnd.sunder.package", 0, CancellationToken.None);

        var stage = await service.StagePackageStoreChangesAsync(new PackageStoreStageRequest([
            new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: upload.UploadId),
        ]));

        Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
        Assert.NotNull(stage.StageId);
        Assert.Empty(await store.ListAsync());
        Assert.False(Directory.Exists(paths.GetInstalledPackagePath("test.package", "1.0.0")));

        var discarded = await service.DiscardPackageStoreStageAsync(stage.StageId!);

        Assert.True(discarded);
        Assert.Empty(await store.ListAsync());
        Assert.False(Directory.Exists(paths.GetInstalledPackagePath("test.package", "1.0.0")));
    }

    [Fact]
    public async Task UpgradeFromPathAsync_WhenArchiveIsNewer_ReplacesInstalledPackage()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var originalArchivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var upgradedArchivePath = CreatePackageArchive(root, "test.package", "1.1.0");

        var installResult = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: originalArchivePath));
        Assert.True(installResult.Success, string.Join(Environment.NewLine, installResult.Errors));
        var originalInstallPath = Assert.Single(await store.ListAsync()).InstallPath;

        var upgradeResult = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Upgrade, "test.package", upgradedArchivePath));

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
        var installer = new SunderPackageArchiveInstaller(paths);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");

        var result = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Upgrade, "other.package", archivePath));

        Assert.False(result.Success);
        Assert.Contains("does not match", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeFromPathAsync_WhenArchiveIsSameVersionAndReinstallNotAllowed_ReturnsFailureAndKeepsPackage()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var originalArchivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var reinstallArchivePath = CreatePackageArchive(root, "test.package", "1.0.0");

        var installResult = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: originalArchivePath));
        Assert.True(installResult.Success, string.Join(Environment.NewLine, installResult.Errors));
        var originalInstallPath = Assert.Single(await store.ListAsync()).InstallPath;

        var result = await ExecuteAsync(paths, store, installer, new PackageStoreMutation(PackageStoreMutationKind.Upgrade, "test.package", reinstallArchivePath));

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
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ManifestVersion = 1,
            Id = packageId,
            Name = "Test Package",
            Version = version,
            EntryAssembly = "Test.Package.dll",
            SdkApiVersion = 1,
            SdkPackageVersion = "1.1.0",
            RequiredSdkCapabilities = ["sdk-baseline-1-1.v1", "core.v1"],
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

    private static async Task<PackageOperationResult> ExecuteAsync(
        RuntimePackagePaths paths,
        InstalledPackageStore store,
        SunderPackageArchiveInstaller installer,
        PackageStoreMutation mutation)
    {
        var coordinator = new PackageStoreCoordinator(paths, store, installer);
        await coordinator.InitializeAsync();
        return await coordinator.ExecuteAsync([mutation]);
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
