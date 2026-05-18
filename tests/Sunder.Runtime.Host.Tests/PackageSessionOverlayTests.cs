using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Protocol;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSessionOverlayTests
{
    [Fact]
    public async Task LoadInstalledWithDevOverlays_WhenPackageIdsCollide_UsesDevPackageSource()
    {
        var rootPath = CreateTempDirectory();
        var installedPackage = CreatePackageLayout(rootPath, "installed", "test.package", "1.0.0", PackageSourceKind.Installed);
        var devFolder = CreatePackageLayout(rootPath, "dev", "test.package", "2.0.0", PackageSourceKind.Dev).InstallPath;
        var loadService = new PackageSessionLoadService(NullLogger.Instance);

        try
        {
            var result = await loadService.LoadInstalledWithDevOverlaysAsync([installedPackage], [devFolder], startBackgroundServices: false);

            Assert.Empty(result.Errors);
            Assert.NotNull(result.Session);
            var session = result.Session;
            var activePackage = Assert.Single(session.GetActivePackages());
            Assert.Equal("test.package", activePackage.PackageId);
            Assert.Equal("2.0.0", activePackage.Version);
            var source = Assert.Single(session.GetActivePackageSources());
            Assert.Equal(PackageSourceKind.Dev, source.Kind);
            Assert.Equal(devFolder, source.Folder);
            await session.DisposeAsync();
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task DevPackageOverlayUnload_RestoresInstalledPackageWithSameId()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var installedPackage = CreatePackageLayout(paths.RootPath, "installed", "test.package", "1.0.0", PackageSourceKind.Installed);
        var devFolder = CreatePackageLayout(paths.RootPath, "dev", "test.package", "2.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            Assert.True((await store.InstallAsync(installedPackage)).Success);
            await service.LoadInstalledPackagesAsync();

            var loadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, devFolder, Watch: true));

            Assert.True(loadResult.Success, string.Join(Environment.NewLine, loadResult.Errors));
            Assert.NotNull(loadResult.Status);
            Assert.Equal(PackageSourceKind.Dev, loadResult.Status.ActiveSourceKind);
            Assert.True(loadResult.Status.OverridesInstalledPackage);
            Assert.True(loadResult.Status.WatchEnabled);
            Assert.Equal("2.0.0", loadResult.Status.Version);

            var unloadResult = await service.UnloadPackageSessionAsync("test.package", PackageSourceKind.Dev);

            Assert.True(unloadResult.Success, string.Join(Environment.NewLine, unloadResult.Errors));
            Assert.NotNull(unloadResult.Status);
            Assert.Equal(PackageSourceKind.Installed, unloadResult.Status.ActiveSourceKind);
            Assert.False(unloadResult.Status.OverridesInstalledPackage);
            Assert.False(unloadResult.Status.WatchEnabled);
            Assert.Equal("1.0.0", unloadResult.Status.Version);
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task LoadPackageSessionAsync_WhenStartupDevPackageIsActive_MergesAdditionalDevPackage()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadAsync(new DevPackageLoadRequest([startupDevFolder]));

            Assert.Empty(startupResult.Errors);
            Assert.Contains(startupResult.LoadedPackages, package => package.PackageId == "startup.package");

            var loadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, builderDevFolder, Watch: true));

            Assert.True(loadResult.Success, string.Join(Environment.NewLine, loadResult.Errors));
            Assert.NotNull(loadResult.Status);
            Assert.Equal("builder.package", loadResult.Status.PackageId);
            Assert.Equal(PackageSourceKind.Dev, loadResult.Status.ActiveSourceKind);
            Assert.True(loadResult.Status.WatchEnabled);
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "builder.package");

            var startupStatus = await service.GetPackageSessionStatusAsync("startup.package");
            Assert.NotNull(startupStatus);
            Assert.Equal(PackageSourceKind.Dev, startupStatus.ActiveSourceKind);
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenInstalledPackagesExist_LoadsInstalledAndStartupDevPackages()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var installedPackage = CreatePackageLayout(paths.RootPath, "installed", "installed.package", "1.0.0", PackageSourceKind.Installed);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            Assert.True((await store.InstallAsync(installedPackage)).Success);
            await service.LoadInstalledPackagesAsync();

            var loadResult = await service.LoadAsync(new DevPackageLoadRequest([startupDevFolder]));

            Assert.Empty(loadResult.Errors);
            Assert.Contains(loadResult.LoadedPackages, package => package.PackageId == "installed.package");
            Assert.Contains(loadResult.LoadedPackages, package => package.PackageId == "startup.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "installed.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task CommitDevPackageStageAsync_PreservesSdkDevOverlay()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadAsync(new DevPackageLoadRequest([startupDevFolder]));
            Assert.Empty(startupResult.Errors);

            var builderLoadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, builderDevFolder, Watch: true));
            Assert.True(builderLoadResult.Success, string.Join(Environment.NewLine, builderLoadResult.Errors));

            WritePackageManifest(startupDevFolder, "startup.package", "1.1.0");

            var stageResult = await service.StageDevPackagesAsync(new DevPackageLoadRequest([startupDevFolder]));

            Assert.Empty(stageResult.Errors);
            Assert.NotNull(stageResult.StageId);
            Assert.Contains(stageResult.LoadedPackages, package => package.PackageId == "startup.package" && package.Version == "1.1.0");
            Assert.Contains(stageResult.LoadedPackages, package => package.PackageId == "builder.package");

            var commitResult = await service.CommitDevPackageStageAsync(stageResult.StageId!);

            Assert.Empty(commitResult.Errors);
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package" && package.Version == "1.1.0");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "builder.package");

            var builderStatus = await service.GetPackageSessionStatusAsync("builder.package");
            Assert.NotNull(builderStatus);
            Assert.Equal(PackageSourceKind.Dev, builderStatus.ActiveSourceKind);
            Assert.True(builderStatus.WatchEnabled);
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task CommitDevPackageStageAsync_WhenSdkOverlayHasSamePackageId_KeepsSdkActiveUntilUnload()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "shared.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "shared.package", "2.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadAsync(new DevPackageLoadRequest([startupDevFolder]));
            Assert.Empty(startupResult.Errors);

            var builderLoadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, builderDevFolder, Watch: true));
            Assert.True(builderLoadResult.Success, string.Join(Environment.NewLine, builderLoadResult.Errors));
            Assert.Equal("2.0.0", builderLoadResult.Status?.Version);

            WritePackageManifest(startupDevFolder, "shared.package", "1.1.0");

            var stageResult = await service.StageDevPackagesAsync(new DevPackageLoadRequest([startupDevFolder]));
            Assert.Empty(stageResult.Errors);
            Assert.NotNull(stageResult.StageId);

            var commitResult = await service.CommitDevPackageStageAsync(stageResult.StageId!);

            Assert.Empty(commitResult.Errors);
            var activePackage = Assert.Single(service.GetActivePackages());
            Assert.Equal("shared.package", activePackage.PackageId);
            Assert.Equal("2.0.0", activePackage.Version);

            var activeStatus = await service.GetPackageSessionStatusAsync("shared.package");
            Assert.NotNull(activeStatus);
            Assert.Equal("2.0.0", activeStatus.Version);
            Assert.True(activeStatus.WatchEnabled);

            var unloadResult = await service.UnloadPackageSessionAsync("shared.package", PackageSourceKind.Dev);

            Assert.True(unloadResult.Success, string.Join(Environment.NewLine, unloadResult.Errors));
            Assert.NotNull(unloadResult.Status);
            Assert.Equal("1.1.0", unloadResult.Status.Version);
            Assert.Equal(PackageSourceKind.Dev, unloadResult.Status.ActiveSourceKind);
            Assert.False(unloadResult.Status.WatchEnabled);
            var restoredPackage = Assert.Single(service.GetActivePackages());
            Assert.Equal("1.1.0", restoredPackage.Version);
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    private static InstalledPackageRecord CreatePackageLayout(
        string rootPath,
        string folderName,
        string packageId,
        string version,
        PackageSourceKind sourceKind)
    {
        var packageFolder = Path.Combine(rootPath, folderName, packageId, version);
        var libraryFolder = Path.Combine(packageFolder, "lib");
        Directory.CreateDirectory(libraryFolder);
        var assemblyPath = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        WritePackageManifest(packageFolder, packageId, version);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(libraryFolder, Path.GetFileName(file)), overwrite: true);
        }

        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(depsPath, Path.Combine(libraryFolder, Path.GetFileName(depsPath)), overwrite: true);
        }

        return new InstalledPackageRecord(
            packageId,
            packageId,
            Summary: null,
            version,
            entryAssemblyFileName,
            Icon: null,
            DependsOn: [],
            packageFolder,
            IsEnabled: true,
            DateTimeOffset.UtcNow);
    }

    private static void WritePackageManifest(string packageFolder, string packageId, string version)
    {
        var entryAssemblyFileName = Path.GetFileName(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        File.WriteAllText(Path.Combine(packageFolder, "sunder-package.json"), $$"""
            {
              "manifestVersion": 1,
              "id": "{{packageId}}",
              "name": "{{packageId}}",
              "version": "{{version}}",
              "entryAssembly": "{{entryAssemblyFileName}}"
            }
            """);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
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
            // Package load contexts can keep shadows alive until process exit.
        }
    }
}

public sealed class PackageSessionOverlayTestPackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
    }
}
