using System.Reflection;
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
    public async Task LoadInstalledAsync_ExposesActivePackagesInDependencyLoadOrder()
    {
        var rootPath = CreateTempDirectory();
        var extensionPackage = CreatePackageLayout(
            rootPath,
            "installed",
            "a.extension",
            "1.0.0",
            PackageSourceKind.Installed,
            ["z.root"]);
        var rootPackage = CreatePackageLayout(rootPath, "installed", "z.root", "1.0.0", PackageSourceKind.Installed);
        var loadService = new PackageSessionLoadService(NullLogger.Instance);

        try
        {
            var result = await loadService.LoadInstalledAsync([extensionPackage, rootPackage], startBackgroundServices: false);

            Assert.Empty(result.Errors);
            Assert.NotNull(result.Session);
            var session = result.Session;
            Assert.Equal(["z.root", "a.extension"], session.GetActivePackages().Select(package => package.PackageId));
            Assert.Equal(["z.root", "a.extension"], session.GetActivePackageSources().Select(source => source.PackageId));
            await session.DisposeAsync();
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void RuntimeSharedAssemblyRegistry_WhenSameIdentityContractExistsAtDifferentPaths_DoesNotThrow()
    {
        using var registry = new RuntimeSharedAssemblyRegistry([]);
        var registerMethod = typeof(RuntimeSharedAssemblyRegistry).GetMethod("TryRegisterSharedAssemblyPath", BindingFlags.Instance | BindingFlags.NonPublic);
        var candidateType = typeof(RuntimeSharedAssemblyRegistry).GetNestedType("AssemblyCandidate", BindingFlags.NonPublic);
        Assert.NotNull(registerMethod);
        Assert.NotNull(candidateType);
        var assemblyName = new AssemblyName("Example.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var firstCandidate = Activator.CreateInstance(candidateType, typeof(PackageSessionOverlayTests).Assembly.Location, assemblyName);
        var secondCandidate = Activator.CreateInstance(candidateType, typeof(ISunderPackageModule).Assembly.Location, assemblyName);

        registerMethod.Invoke(registry, [firstCandidate, null]);
        registerMethod.Invoke(registry, [secondCandidate, null]);
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
            var startupResult = await service.LoadPackageLifecycleAsync(CreateStartupLifecycleRequest(startupDevFolder));

            Assert.Empty(startupResult.Errors);
            Assert.Contains(startupResult.ActivePackages, package => package.PackageId == "startup.package");

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

            var loadResult = await service.LoadPackageLifecycleAsync(CreateStartupLifecycleRequest(startupDevFolder));

            Assert.Empty(loadResult.Errors);
            Assert.Contains(loadResult.ActivePackages, package => package.PackageId == "installed.package");
            Assert.Contains(loadResult.ActivePackages, package => package.PackageId == "startup.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "installed.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task ReloadInstalledPackageSessionAsync_PreservesDevOverlays()
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
            var startupResult = await service.LoadPackageLifecycleAsync(CreateStartupLifecycleRequest(startupDevFolder));
            Assert.Empty(startupResult.Errors);

            var reloadResult = await service.ReloadInstalledPackageSessionAsync(new InstalledPackageSessionReloadRequest(["installed.package"]));

            Assert.True(reloadResult.Success, string.Join(Environment.NewLine, reloadResult.Errors));
            Assert.True(reloadResult.RuntimeSessionApplied);
            Assert.False(reloadResult.RequiresAppRestart);
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "installed.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
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
    public async Task ReloadInstalledPackageSessionAsync_WhenOnePackageCannotPrepare_AppliesPartialSession()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var goodPackage = CreatePackageLayout(paths.RootPath, "installed", "good.package", "1.0.0", PackageSourceKind.Installed);
        var badPackage = CreatePackageLayout(paths.RootPath, "installed", "bad.package", "1.0.0", PackageSourceKind.Installed);

        try
        {
            Assert.True((await store.InstallAsync(goodPackage)).Success);
            Assert.True((await store.InstallAsync(badPackage)).Success);
            File.Delete(badPackage.EntryAssemblyPath);

            var reloadResult = await service.ReloadInstalledPackageSessionAsync(new InstalledPackageSessionReloadRequest(["good.package", "bad.package"]));

            Assert.True(reloadResult.Success, string.Join(Environment.NewLine, reloadResult.Errors));
            Assert.True(reloadResult.RuntimeSessionApplied);
            Assert.False(reloadResult.RequiresAppRestart);
            Assert.Contains(reloadResult.Warnings, warning => warning.Contains("bad.package", StringComparison.OrdinalIgnoreCase));
            var activePackage = Assert.Single(service.GetActivePackages());
            Assert.Equal("good.package", activePackage.PackageId);
            var badSessionPackage = Assert.Single(service.GetSessionPackages(), package => package.PackageId == "bad.package");
            Assert.Equal(PackageReadinessState.Failed, badSessionPackage.Readiness);
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task CommitPackageLifecycleStageAsync_PreservesSdkDevOverlay()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadPackageLifecycleAsync(CreateStartupLifecycleRequest(startupDevFolder));
            Assert.Empty(startupResult.Errors);

            var builderLoadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, builderDevFolder, Watch: true));
            Assert.True(builderLoadResult.Success, string.Join(Environment.NewLine, builderLoadResult.Errors));

            WritePackageManifest(startupDevFolder, "startup.package", "1.1.0");

            var stageResult = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(startupDevFolder));

            Assert.Empty(stageResult.Errors);
            Assert.NotNull(stageResult.StageId);
            Assert.Contains(stageResult.ActivePackages, package => package.PackageId == "startup.package" && package.Version == "1.1.0");
            Assert.Contains(stageResult.ActivePackages, package => package.PackageId == "builder.package");
            Assert.Contains("startup.package", stageResult.ImpactedPackageIds);
            Assert.DoesNotContain("builder.package", stageResult.ImpactedPackageIds);

            var commitResult = await service.CommitPackageLifecycleStageAsync(stageResult.StageId!);

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
    public async Task CommitPackageLifecycleStageAsync_WhenSdkOverlayHasSamePackageId_KeepsSdkActiveUntilUnload()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "shared.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "shared.package", "2.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadPackageLifecycleAsync(CreateStartupLifecycleRequest(startupDevFolder));
            Assert.Empty(startupResult.Errors);

            var builderLoadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, builderDevFolder, Watch: true));
            Assert.True(builderLoadResult.Success, string.Join(Environment.NewLine, builderLoadResult.Errors));
            Assert.Equal("2.0.0", builderLoadResult.Status?.Version);

            WritePackageManifest(startupDevFolder, "shared.package", "1.1.0");

            var stageResult = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(startupDevFolder));
            Assert.Empty(stageResult.Errors);
            Assert.NotNull(stageResult.StageId);

            var commitResult = await service.CommitPackageLifecycleStageAsync(stageResult.StageId!);

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

    [Fact]
    public async Task CommitPackageLifecycleStageAsync_WhenActiveSessionChangedAfterStage_RejectsStaleStage()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths, store);
        var service = new RuntimePackageSessionService(NullLogger<RuntimePackageSessionService>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var stagedDevFolder = CreatePackageLayout(paths.RootPath, "staged-dev", "staged.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadPackageLifecycleAsync(CreateStartupLifecycleRequest(startupDevFolder));
            Assert.Empty(startupResult.Errors);

            var stageResult = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(stagedDevFolder));
            Assert.Empty(stageResult.Errors);
            Assert.NotNull(stageResult.StageId);
            Assert.Contains(stageResult.ActivePackages, package => package.PackageId == "staged.package");

            var builderLoadResult = await service.LoadPackageSessionAsync(new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, builderDevFolder, Watch: true));
            Assert.True(builderLoadResult.Success, string.Join(Environment.NewLine, builderLoadResult.Errors));

            var commitResult = await service.CommitPackageLifecycleStageAsync(stageResult.StageId!);

            Assert.False(commitResult.Success);
            Assert.Contains(commitResult.Errors, error => error.Contains("stale", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(service.GetActivePackages(), package => package.PackageId == "staged.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "builder.package");
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
        PackageSourceKind sourceKind,
        IReadOnlyList<string>? dependencies = null)
    {
        var packageFolder = Path.Combine(rootPath, folderName, packageId, version);
        var libraryFolder = Path.Combine(packageFolder, "lib");
        Directory.CreateDirectory(libraryFolder);
        var assemblyPath = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        WritePackageManifest(packageFolder, packageId, version, dependencies);

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
            DependsOn: (dependencies ?? [])
                .Select(dependencyId => new InstalledPackageDependencyRecord(dependencyId, "*"))
                .ToArray(),
            packageFolder,
            IsEnabled: true,
            DateTimeOffset.UtcNow);
    }

    private static void WritePackageManifest(
        string packageFolder,
        string packageId,
        string version,
        IReadOnlyList<string>? dependencies = null)
    {
        var entryAssemblyFileName = Path.GetFileName(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        var dependencyJson = dependencies is { Count: > 0 }
            ? ",\n  \"dependsOn\": [\n" + string.Join(",\n", dependencies.Select(dependencyId => $"    {{ \"packageId\": \"{dependencyId}\", \"versionRange\": \"*\" }}")) + "\n  ]"
            : string.Empty;
        File.WriteAllText(Path.Combine(packageFolder, "sunder-package.json"), $$"""
            {
              "manifestVersion": 1,
              "id": "{{packageId}}",
              "name": "{{packageId}}",
              "version": "{{version}}",
              "entryAssembly": "{{entryAssemblyFileName}}"{{dependencyJson}}
            }
            """);
    }

    private static PackageLifecycleLoadRequest CreateStartupLifecycleRequest(string folder)
        => new([
            new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, folder),
        ], PackageLifecycleOverlayOwner.Startup);

    private static PackageLifecycleStageRequest CreateHotReloadStageRequest(string folder)
        => new([
            new Sunder.Protocol.PackageSessionLoadRequest(PackageSourceKind.Dev, folder),
        ], PackageLifecycleOverlayOwner.HotReload);

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
