using System.Text.Json;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class InstalledPackageStoreTests
{
    [Fact]
    public async Task ListAsync_WhenUnrecordedPayloadExists_DoesNotResurrectPackage()
    {
        var paths = CreateRuntimePackagePaths();
        CreateInstallPath(paths, "orphan.package", "1.0.0");
        var store = new InstalledPackageStore(paths);

        var packages = await store.ListAsync();

        Assert.Empty(packages);
        Assert.False(File.Exists(paths.StateFilePath));
    }

    [Fact]
    public async Task ListAsync_WhenPersistedPathIsOutsideVersionRoot_RejectsCatalog()
    {
        var paths = CreateRuntimePackagePaths();
        var outsidePath = Path.Combine(Path.GetDirectoryName(paths.RootPath)!, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);
        Directory.CreateDirectory(paths.CatalogRootPath);
        await File.WriteAllTextAsync(paths.StateFilePath, JsonSerializer.Serialize(new InstalledPackageStateFile(
            1,
            [CreatePackage(paths, "unsafe.package") with { InstallPath = outsidePath }]), TestJsonOptions));
        var store = new InstalledPackageStore(paths);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(outsidePath));
    }

    [Fact]
    public async Task WriteAsync_RejectsDuplicateIdsWithoutReplacingCatalog()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var original = CreatePackage(paths, "test.package");
        await store.WriteAsync([original]);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([original, original]));

        var persisted = Assert.Single(await store.ListAsync());
        Assert.Equal(original.PackageId, persisted.PackageId);
        Assert.Equal(original.Version, persisted.Version);
        Assert.Equal(original.InstallPath, persisted.InstallPath);
    }

    [Fact]
    public async Task WriteAsync_RejectsInvalidVersionAndDependencyRange()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var invalidVersion = CreatePackage(paths, "test.package") with { Version = "latest" };
        var invalidRange = CreatePackage(paths, "test.package") with
        {
            DependsOn = [new InstalledPackageDependencyRecord("other.package", "banana")],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([invalidVersion]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([invalidRange]));
    }

    [Fact]
    public async Task WriteAsync_PersistsOneAuthoritativeSortedCatalog()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var second = CreatePackage(paths, "z.package");
        var first = CreatePackage(paths, "a.package");

        await store.WriteAsync([second, first]);

        Assert.Equal(["a.package", "z.package"], (await store.ListAsync()).Select(package => package.PackageId));
        Assert.DoesNotContain(".tmp-", await File.ReadAllTextAsync(paths.StateFilePath), StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions TestJsonOptions = new() { WriteIndented = true };

    private static RuntimePackagePaths CreateRuntimePackagePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new RuntimePackagePaths(root);
    }

    internal static InstalledPackageRecord CreatePackage(
        RuntimePackagePaths paths,
        string packageId,
        string version = "1.0.0",
        bool isEnabled = true,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null)
        => new(
            packageId,
            packageId,
            Summary: null,
            version,
            EntryAssembly: packageId + ".dll",
            Icon: null,
            DependsOn: dependencies ?? [],
            CreateInstallPath(paths, packageId, version),
            isEnabled,
            DateTimeOffset.UtcNow);

    private static string CreateInstallPath(RuntimePackagePaths paths, string packageId, string version)
    {
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        Directory.CreateDirectory(installPath);
        return installPath;
    }
}
