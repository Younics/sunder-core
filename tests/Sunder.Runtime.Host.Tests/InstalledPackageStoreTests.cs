using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class InstalledPackageStoreTests
{
    [Fact]
    public async Task InstallAsync_WhenPackageIdAlreadyExists_ReturnsFailure()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var package = CreatePackage(paths, "test.package");

        Assert.True((await store.InstallAsync(package)).Success);

        var duplicateResult = await store.InstallAsync(package with { InstallPath = CreateInstallPath(paths, "test.package", "1.0.1") });

        Assert.False(duplicateResult.Success);
        Assert.Contains("already installed", duplicateResult.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_WhenPackageHasDependency_ReturnsTargetAndDependencyAsImpacted()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var dependency = CreatePackage(paths, "test.dependency");
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            dependencies: [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0")]);

        Assert.True((await store.InstallAsync(dependency)).Success);

        var result = await store.InstallAsync(dependent);

        Assert.True(result.Success);
        Assert.Equal(["test.dependent", "test.dependency"], result.ImpactedPackageIds);
    }

    [Fact]
    public async Task SetEnabledAsync_WhenEnabledDependentExists_BlocksDisable()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var dependency = CreatePackage(paths, "test.dependency");
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            dependencies: [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0")]);

        Assert.True((await store.InstallAsync(dependency)).Success);
        Assert.True((await store.InstallAsync(dependent)).Success);

        var disableResult = await store.SetEnabledAsync("test.dependency", isEnabled: false);

        Assert.False(disableResult.Success);
        Assert.Contains("cannot be disabled", disableResult.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallAsync_WhenDependentExists_BlocksUninstall()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var dependency = CreatePackage(paths, "test.dependency");
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            dependencies: [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0")]);

        Assert.True((await store.InstallAsync(dependency)).Success);
        Assert.True((await store.InstallAsync(dependent)).Success);

        var uninstallResult = await store.UninstallAsync("test.dependency");

        Assert.False(uninstallResult.Success);
        Assert.Contains("cannot be uninstalled", uninstallResult.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallAsync_WhenPackageHasDependency_ReturnsTargetAndDependencyAsImpacted()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var dependency = CreatePackage(paths, "test.dependency");
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            dependencies: [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0")]);

        Assert.True((await store.InstallAsync(dependency)).Success);
        Assert.True((await store.InstallAsync(dependent)).Success);

        var result = await store.UninstallAsync("test.dependent");

        Assert.True(result.Success);
        Assert.Equal(["test.dependent", "test.dependency"], result.ImpactedPackageIds);
    }

    [Fact]
    public async Task SetEnabledAsync_WhenDependencyIsDisabled_BlocksEnable()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var dependency = CreatePackage(paths, "test.dependency");
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            isEnabled: false,
            dependencies: [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0")]);

        Assert.True((await store.InstallAsync(dependency)).Success);
        Assert.True((await store.InstallAsync(dependent)).Success);
        Assert.True((await store.SetEnabledAsync("test.dependency", isEnabled: false)).Success);

        var enableResult = await store.SetEnabledAsync("test.dependent", isEnabled: true);

        Assert.False(enableResult.Success);
        Assert.Contains("disabled package", enableResult.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeAsync_WhenPackageIsDisabled_PreservesDisabledState()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var package = CreatePackage(paths, "test.package", isEnabled: false);
        var upgradedPackage = CreatePackage(paths, "test.package", version: "1.1.0", isEnabled: true);

        Assert.True((await store.InstallAsync(package)).Success);

        var result = await store.UpgradeAsync("test.package", upgradedPackage, allowDowngrade: false, reinstall: false);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var installedPackage = Assert.Single(await store.ListAsync());
        Assert.Equal("1.1.0", installedPackage.Version);
        Assert.False(installedPackage.IsEnabled);
    }

    [Fact]
    public async Task UpgradeAsync_WhenDependentRangeWouldBreak_ReturnsFailure()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var dependency = CreatePackage(paths, "test.dependency");
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            dependencies: [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0 <2.0.0")]);
        var incompatibleDependency = CreatePackage(paths, "test.dependency", version: "2.0.0");

        Assert.True((await store.InstallAsync(dependency)).Success);
        Assert.True((await store.InstallAsync(dependent)).Success);

        var result = await store.UpgradeAsync("test.dependency", incompatibleDependency, allowDowngrade: false, reinstall: false);

        Assert.False(result.Success);
        Assert.Contains("test.dependent", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeAsync_ReturnsTargetDependenciesAndDependentsAsImpacted()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var sharedDependency = CreatePackage(paths, "test.shared");
        var package = CreatePackage(
            paths,
            "test.package",
            dependencies: [new InstalledPackageDependencyRecord("test.shared", ">=1.0.0")]);
        var dependent = CreatePackage(
            paths,
            "test.dependent",
            dependencies: [new InstalledPackageDependencyRecord("test.package", ">=1.0.0")]);
        var upgradedPackage = CreatePackage(
            paths,
            "test.package",
            version: "1.1.0",
            dependencies: [new InstalledPackageDependencyRecord("test.shared", ">=1.0.0")]);

        Assert.True((await store.InstallAsync(sharedDependency)).Success);
        Assert.True((await store.InstallAsync(package)).Success);
        Assert.True((await store.InstallAsync(dependent)).Success);

        var result = await store.UpgradeAsync("test.package", upgradedPackage, allowDowngrade: false, reinstall: false);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["test.package", "test.shared", "test.dependent"], result.ImpactedPackageIds);
    }

    [Fact]
    public async Task ListAsync_WhenDevLayoutWasCopiedIntoInstalledRoot_RecoversStateForUninstall()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var copiedPackagePath = CreateCopiedDevLayout(paths, "copied.package", "1.0.0");

        var installedPackages = await store.ListAsync();

        var package = Assert.Single(installedPackages);
        Assert.Equal("copied.package", package.PackageId);
        Assert.Equal(copiedPackagePath, package.InstallPath);
        Assert.True(File.Exists(paths.StateFilePath));

        var uninstallResult = await store.UninstallAsync("copied.package");

        Assert.True(uninstallResult.Success, string.Join(Environment.NewLine, uninstallResult.Errors));
        Assert.False(Directory.Exists(copiedPackagePath));
        Assert.Empty(await store.ListAsync());
    }

    private static RuntimePackagePaths CreateRuntimePackagePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new RuntimePackagePaths(root);
    }

    private static InstalledPackageRecord CreatePackage(
        RuntimePackagePaths paths,
        string packageId,
        string version = "1.0.0",
        bool isEnabled = true,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null)
    {
        var installPath = CreateInstallPath(paths, packageId, version);
        return new InstalledPackageRecord(
            packageId,
            packageId,
            Summary: null,
            version,
            EntryAssembly: packageId + ".dll",
            Icon: null,
            DependsOn: dependencies ?? [],
            installPath,
            isEnabled,
            DateTimeOffset.UtcNow);
    }

    private static string CreateInstallPath(RuntimePackagePaths paths, string packageId, string version)
    {
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        Directory.CreateDirectory(installPath);
        return installPath;
    }

    private static string CreateCopiedDevLayout(RuntimePackagePaths paths, string packageId, string version)
    {
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        Directory.CreateDirectory(Path.Combine(installPath, "lib"));
        File.WriteAllText(Path.Combine(installPath, "sunder-package.json"), $$"""
            {
              "manifestVersion": 1,
              "id": "{{packageId}}",
              "name": "Copied Package",
              "version": "{{version}}",
              "entryAssembly": "Copied.Package.dll"
            }
            """);
        File.WriteAllText(Path.Combine(installPath, "lib", "Copied.Package.dll"), "not a real assembly");
        return installPath;
    }
}
