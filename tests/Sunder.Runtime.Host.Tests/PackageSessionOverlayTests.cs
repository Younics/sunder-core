using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSessionOverlayTests
{
    [Fact]
    public void RuntimeSharedAssemblyRegistry_ResolvesHostStackSdkAssembly()
    {
        using var registry = new RuntimeSharedAssemblyRegistry([]);
        var stackSdkAssembly = typeof(IPackageStackExporter).Assembly;

        Assert.Same(stackSdkAssembly, registry.ResolveSharedAssembly(stackSdkAssembly.GetName()));
    }

    [Fact]
    public async Task LoadInstalledWithDevOverlays_WhenPackageIdsCollide_UsesDevPackageSource()
    {
        var rootPath = CreateTempDirectory();
        var installedPackage = CreatePackageLayout(rootPath, "installed", "test.package", "1.0.0", PackageSourceKind.Installed);
        var devFolder = CreatePackageLayout(rootPath, "dev", "test.package", "2.0.0", PackageSourceKind.Dev).InstallPath;
        var loadService = new PackageSessionLoadService(NullLogger.Instance);

        try
        {
            var result = await loadService.LoadInstalledWithDevOverlaysAsync([installedPackage], [devFolder]);

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
    public async Task LoadInstalledWithDevOverlays_EnforcesDependencyRangeAgainstEffectiveDevVersion()
    {
        var rootPath = CreateTempDirectory();
        var dependent = CreatePackageLayout(
            rootPath,
            "installed",
            "dependent.package",
            "1.0.0",
            PackageSourceKind.Installed,
            ["core.package"],
            ">=1.0.0 <2.0.0");
        var installedCore = CreatePackageLayout(
            rootPath,
            "installed",
            "core.package",
            "1.5.0",
            PackageSourceKind.Installed);
        var devCoreFolder = CreatePackageLayout(
            rootPath,
            "dev",
            "core.package",
            "2.0.0",
            PackageSourceKind.Dev).InstallPath;
        var loadService = new PackageSessionLoadService(NullLogger.Instance);

        try
        {
            var result = await loadService.LoadInstalledWithDevOverlaysAsync(
                [dependent, installedCore],
                [devCoreFolder]);

            Assert.Contains(result.Errors, error =>
                error.Contains("dependent.package", StringComparison.Ordinal)
                && error.Contains(">=1.0.0 <2.0.0", StringComparison.Ordinal)
                && error.Contains("2.0.0", StringComparison.Ordinal));
            Assert.NotNull(result.Session);
            var active = Assert.Single(result.Session.GetActivePackages());
            Assert.Equal("core.package", active.PackageId);
            Assert.Equal("2.0.0", active.Version);
            await result.Session.DisposeAsync();
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
            var result = await loadService.LoadInstalledAsync([extensionPackage, rootPackage]);

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
    public async Task LoadInstalledAsync_WhenPackageRegistersReservedHostCapability_RejectsItClearly()
    {
        var rootPath = CreateTempDirectory();
        var package = CreatePackageLayout(
            rootPath,
            "installed",
            "reserved.package",
            "1.0.0",
            PackageSourceKind.Installed);
        var loadService = new PackageSessionLoadService(NullLogger.Instance);

        try
        {
            var result = await loadService.LoadInstalledAsync([package]);

            Assert.Contains(result.Errors, error =>
                error.Contains("reserved host capability", StringComparison.OrdinalIgnoreCase)
                && error.Contains(typeof(IPackageContext).FullName!, StringComparison.Ordinal));
            if (result.Session is not null)
            {
                await result.Session.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void RuntimeSharedAssemblyRegistry_WhenRequestedVersionIsOlderThanLoadedVersion_AllowsBinding()
    {
        var requested = new AssemblyName("Sunder.Package.Agent.Contracts, Version=1.0.2.0, Culture=neutral, PublicKeyToken=null");
        var loaded = new AssemblyName("Sunder.Package.Agent.Contracts, Version=1.0.3.0, Culture=neutral, PublicKeyToken=null");

        Assert.True(RuntimeSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(requested, loaded));
        Assert.False(RuntimeSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(loaded, requested));
    }

    [Fact]
    public void RuntimeSharedAssemblyRegistry_RejectsHigherMajorAndDifferentContractIdentity()
    {
        var requested = new AssemblyName("Example.Contracts, Version=1.2.0.0, Culture=neutral, PublicKeyToken=0011223344556677");
        var higherMajor = new AssemblyName("Example.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=0011223344556677");
        var unrelated = new AssemblyName("Example.Contracts, Version=1.3.0.0, Culture=neutral, PublicKeyToken=8899aabbccddeeff");

        Assert.False(RuntimeSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(requested, higherMajor));
        Assert.False(RuntimeSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(requested, unrelated));
    }

    [Fact]
    public async Task DevPackageOverlayUnload_RestoresInstalledPackageWithSameId()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var installedPackage = CreatePackageLayout(paths.RootPath, "installed", "test.package", "1.0.0", PackageSourceKind.Installed);
        var devFolder = CreatePackageLayout(paths.RootPath, "dev", "test.package", "2.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            await AddInstalledPackageAsync(store, installedPackage);
            await service.LoadInstalledPackagesAsync();

            var loadResult = await service.LoadDevPackageFromRuntimeInputAsync(devFolder);

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
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);

            Assert.Empty(startupResult.Errors);
            Assert.Contains(startupResult.ActivePackages, package => package.PackageId == "startup.package");

            var loadResult = await service.LoadDevPackageFromRuntimeInputAsync(builderDevFolder);

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
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var installedPackage = CreatePackageLayout(paths.RootPath, "installed", "installed.package", "1.0.0", PackageSourceKind.Installed);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            await AddInstalledPackageAsync(store, installedPackage);
            await service.LoadInstalledPackagesAsync();

            var loadResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);

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
    public async Task LoadPackageLifecycleAsync_WhenStartupDevPackageOverridesBrokenInstalledPackage_LoadsDevWithoutInstalledErrors()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var installedPackage = CreatePackageLayout(paths.RootPath, "installed", "test.package", "1.0.0", PackageSourceKind.Installed);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "test.package", "2.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            await AddInstalledPackageAsync(store, installedPackage);
            File.Delete(Path.Combine(
                installedPackage.InstallPath,
                installedPackage.ContentInventory.Single(entry => entry.Path.EndsWith(".dll", StringComparison.Ordinal)).Path.Replace('/', Path.DirectorySeparatorChar)));

            var loadResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);

            Assert.Empty(loadResult.Errors);
            var activePackage = Assert.Single(loadResult.ActivePackages);
            Assert.Equal("test.package", activePackage.PackageId);
            Assert.Equal("2.0.0", activePackage.Version);
            var source = Assert.Single(service.GetActiveRuntimePackageSources());
            Assert.Equal(PackageSourceKind.Dev, source.Kind);
            var status = await service.GetPackageSessionStatusAsync("test.package");
            Assert.NotNull(status);
            Assert.Equal(PackageSourceKind.Dev, status.ActiveSourceKind);
            Assert.True(status.OverridesInstalledPackage);
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task LoadStartupDevPackagesAsync_ActivatesEffectiveDevPackageExactlyOnce()
    {
        const string packageId = "startup.counted.package";
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var installedPackage = CreatePackageLayout(paths.RootPath, "installed", packageId, "1.0.0", PackageSourceKind.Installed);
        var devFolder = CreatePackageLayout(paths.RootPath, "startup-dev", packageId, "2.0.0", PackageSourceKind.Dev).InstallPath;
        var counterPath = Path.Combine(paths.RootPath, "activation-counters.txt");
        var previousCounterPath = Environment.GetEnvironmentVariable(RuntimeActivationCounter.EnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(RuntimeActivationCounter.EnvironmentVariable, counterPath);
            await AddInstalledPackageAsync(store, installedPackage);

            var result = await service.LoadStartupDevPackagesAsync([devFolder]);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var active = Assert.Single(service.GetActivePackages());
            Assert.Equal(packageId, active.PackageId);
            Assert.Equal("2.0.0", active.Version);
            var source = Assert.Single(service.GetActiveRuntimePackageSources());
            Assert.Equal(PackageSourceKind.Dev, source.Kind);
            Assert.Equal(devFolder, source.Folder);

            var counters = File.ReadAllLines(counterPath);
            Assert.Equal(1, counters.Count(value => value == "2.0.0:configure"));
            Assert.Equal(1, counters.Count(value => value == "2.0.0:register"));
            Assert.Equal(1, counters.Count(value => value == "2.0.0:start"));
            Assert.DoesNotContain(counters, value => value.StartsWith("1.0.0:", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeActivationCounter.EnvironmentVariable, previousCounterPath);
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task CommitPackageLifecycleStageAsync_PreservesSdkDevOverlay()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.Empty(startupResult.Errors);

            var builderLoadResult = await service.LoadDevPackageFromRuntimeInputAsync(builderDevFolder);
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
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "shared.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "shared.package", "2.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.Empty(startupResult.Errors);

            var builderLoadResult = await service.LoadDevPackageFromRuntimeInputAsync(builderDevFolder);
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
    public async Task StagePackageLifecycleAsync_WhenAskedToAddUnknownPackage_RejectsPathlessRequest()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.Empty(startupResult.Errors);

            var stageResult = await service.StagePackageLifecycleAsync(
                new PackageLifecycleStageRequest(["builder.package"]));

            Assert.False(stageResult.Success);
            Assert.Null(stageResult.StageId);
            Assert.Contains(stageResult.Errors, error => error.Contains("not an active dev package", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
            Assert.DoesNotContain(service.GetActivePackages(), package => package.PackageId == "builder.package");
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
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;
        var builderDevFolder = CreatePackageLayout(paths.RootPath, "builder-dev", "builder.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.Empty(startupResult.Errors);

            WritePackageManifest(startupDevFolder, "startup.package", "1.1.0");
            var stageResult = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(startupDevFolder));
            Assert.Empty(stageResult.Errors);
            Assert.NotNull(stageResult.StageId);
            Assert.Contains(stageResult.ActivePackages, package => package.PackageId == "startup.package" && package.Version == "1.1.0");

            var builderLoadResult = await service.LoadDevPackageFromRuntimeInputAsync(builderDevFolder);
            Assert.True(builderLoadResult.Success, string.Join(Environment.NewLine, builderLoadResult.Errors));

            var commitResult = await service.CommitPackageLifecycleStageAsync(stageResult.StageId!);

            Assert.False(commitResult.Success);
            Assert.Contains(commitResult.Errors, error => error.Contains("stale", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package" && package.Version == "1.1.0");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "startup.package");
            Assert.Contains(service.GetActivePackages(), package => package.PackageId == "builder.package");
        }
        finally
        {
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task CommitPackageLifecycleStageAsync_WhenStageIsStale_DoesNotStartRejectedBackgroundServices()
    {
        const string packageId = "startup.counted.package";
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(
            paths.RootPath,
            "startup-dev",
            packageId,
            "1.0.0",
            PackageSourceKind.Dev).InstallPath;
        var counterPath = Path.Combine(paths.RootPath, "stale-stage-counters.txt");
        var previousCounterPath = Environment.GetEnvironmentVariable(RuntimeActivationCounter.EnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(RuntimeActivationCounter.EnvironmentVariable, counterPath);
            var initial = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.True(initial.Success, string.Join(Environment.NewLine, initial.Errors));
            WritePackageManifest(startupDevFolder, packageId, "1.1.0");
            var stage = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(startupDevFolder));
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            Assert.DoesNotContain(File.ReadAllLines(counterPath), value => value == "1.1.0:start");

            var superseding = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.True(superseding.Success, string.Join(Environment.NewLine, superseding.Errors));
            var startsBeforeReject = File.ReadAllLines(counterPath).Count(value => value == "1.1.0:start");

            var rejected = await service.CommitPackageLifecycleStageAsync(stage.StageId!);

            Assert.False(rejected.Success);
            Assert.Equal(
                startsBeforeReject,
                File.ReadAllLines(counterPath).Count(value => value == "1.1.0:start"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeActivationCounter.EnvironmentVariable, previousCounterPath);
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task CommitPackageLifecycleStageAsync_PublishesExactStagedSessionWithLazyTargetSnapshots()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer);
        var startupDevFolder = CreatePackageLayout(paths.RootPath, "startup-dev", "startup.package", "1.0.0", PackageSourceKind.Dev).InstallPath;

        try
        {
            var startupResult = await service.LoadStartupDevPackagesAsync([startupDevFolder]);
            Assert.True(startupResult.Success, string.Join(Environment.NewLine, startupResult.Errors));
            WritePackageManifest(startupDevFolder, "startup.package", "1.1.0");
            var stage = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(startupDevFolder));
            var stagedSession = service.GetStagedLifecycleSession(stage.StageId!);
            var stagedSnapshot = Assert.Single(service.GetStagedPackageUiSnapshots(stage.StageId!));

            var commit = await service.CommitPackageLifecycleStageAsync(stage.StageId!);

            Assert.True(commit.Success, string.Join(Environment.NewLine, commit.Errors));
            Assert.Same(stagedSession, service.ActiveSession);
            Assert.Equal(service.SessionGeneration, commit.CommittedStamp?.SessionGeneration);
            var committedSnapshot = Assert.Single(service.GetActivePackageUiSnapshots());
            Assert.NotEqual(stagedSnapshot.SnapshotId, committedSnapshot.SnapshotId);
            Assert.Equal(stagedSnapshot.ContentHash, committedSnapshot.ContentHash);
            Assert.DoesNotContain("/stage/", committedSnapshot.SnapshotUri, StringComparison.Ordinal);
            Assert.Null(service.AcquireStageUiSnapshot(stage.StageId!, stagedSnapshot.SnapshotId));
            using var committedLease = service.AcquireCurrentUiSnapshot(committedSnapshot.SnapshotId);
            Assert.NotNull(committedLease);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task ActiveAppSnapshots_WhenRequestedRidIsMissing_FailOnlyThatClientRequest()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer);
        var folder = CreatePackageLayout(
            paths.RootPath,
            "startup-dev",
            "test.package",
            "1.0.0",
            PackageSourceKind.Dev).InstallPath;
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var missingRid = SunderPackageFormat.SupportedRuntimeIdentifiers
            .First(rid => !string.Equals(rid, currentRid, StringComparison.Ordinal));

        try
        {
            var load = await service.LoadStartupDevPackagesAsync([folder]);
            Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors));
            Assert.Single(service.GetActivePackageUiSnapshots(currentRid));

            var exception = Assert.Throws<InvalidDataException>(
                () => service.GetActivePackageUiSnapshots(missingRid));

            Assert.Contains($"app/{missingRid}", exception.Message, StringComparison.Ordinal);
            Assert.Single(service.GetActivePackages());
            Assert.Single(service.GetActivePackageUiSnapshots(currentRid));
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task ActiveAppSnapshots_WhenExactTargetKindIsWeb_ReturnsWebProjection()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer);
        var folder = Path.Combine(paths.RootPath, "startup-dev", "test.package", "1.0.0");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            folder,
            "test.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            appTargetKind: SunderPackageFormat.WebTargetKind,
            appEntryPoint: "index.html",
            appViews:
            [
                new SunderPackageWebViewManifest
                {
                    ViewId = "test.package.main",
                    DisplayName = "Test",
                    Route = "/",
                    DefaultPlacement = "middle",
                    ShowInHotbar = true,
                },
            ]);
        File.WriteAllText(Path.Combine(folder, "payload", "shared", "index.html"), "<html></html>");
        CanonicalPackageTestBuilder.WriteContentIndex(folder);

        try
        {
            var load = await service.LoadStartupDevPackagesAsync([folder]);
            Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors));

            var snapshot = Assert.Single(service.GetActivePackageUiSnapshots(RuntimeInformation.RuntimeIdentifier));
            Assert.Equal(SunderPackageFormat.WebTargetKind, snapshot.Target.Kind);
            Assert.Equal("test.package.main", Assert.Single(snapshot.Target.Views).ViewId);
            Assert.Single(service.GetActivePackages());
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task PendingLifecycleStage_ExpiresAndCleansCandidateAndUiSnapshot()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var policy = new RuntimeLifecyclePolicyOptions { PendingStageLifetime = TimeSpan.FromMinutes(1) };
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer,
            lifecyclePolicy: policy,
            timeProvider: clock);
        var folder = CreatePackageLayout(
            paths.RootPath,
            "startup-dev",
            "expiring.package",
            "1.0.0",
            PackageSourceKind.Dev).InstallPath;

        try
        {
            Assert.True((await service.LoadStartupDevPackagesAsync([folder])).Success);
            WritePackageManifest(folder, "expiring.package", "1.1.0");
            var stage = await service.StagePackageLifecycleAsync(CreateHotReloadStageRequest(folder));
            var snapshot = Assert.Single(service.GetStagedPackageUiSnapshots(stage.StageId!));
            var pending = service.GetPackageStageStatus(stage.StageId!);

            Assert.NotNull(pending);
            Assert.Equal(clock.GetUtcNow(), pending.CreatedAtUtc);
            Assert.Equal(clock.GetUtcNow() + policy.PendingStageLifetime, pending.ExpiresAtUtc);

            clock.Advance(TimeSpan.FromMinutes(2));
            await service.SweepLifecycleStagesAsync(clock.GetUtcNow());

            Assert.Null(service.GetStagedLifecycleSession(stage.StageId!));
            Assert.Null(service.AcquireStageUiSnapshot(stage.StageId!, snapshot.SnapshotId));
            var expired = service.GetPackageStageStatus(stage.StageId!);
            Assert.Equal(RuntimePackageStageState.Failed, expired?.State);
            Assert.Contains("expired", expired?.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False((await service.CommitPackageLifecycleStageAsync(stage.StageId!)).Success);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    [Fact]
    public async Task PendingStoreStage_ExpiresAndCleansDurableCandidateAndUiSnapshot()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var policy = new RuntimeLifecyclePolicyOptions { PendingStageLifetime = TimeSpan.FromMinutes(1) };
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer,
            lifecyclePolicy: policy,
            timeProvider: clock);
        var installed = CreatePackageLayout(
            paths.RootPath,
            "installed",
            "expiring.store.package",
            "1.0.0",
            PackageSourceKind.Installed) with
        { IsEnabled = false };

        try
        {
            await AddInstalledPackageAsync(store, installed);
            Assert.True((await service.LoadInstalledPackagesAsync()).Success);
            var stage = await service.StagePackageStoreChangesAsync(new PackageStoreStageRequest([
                new PackageStoreMutationRequest(PackageStoreMutationKind.Enable, installed.PackageId),
            ]));
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var snapshot = Assert.Single(service.GetStagedPackageUiSnapshots(stage.StageId!));

            clock.Advance(TimeSpan.FromMinutes(2));
            await service.SweepStoreStagesAsync(clock.GetUtcNow());

            Assert.Null(service.GetStagedStoreSession(stage.StageId!));
            Assert.Null(service.AcquireStageUiSnapshot(stage.StageId!, snapshot.SnapshotId));
            var expired = service.GetPackageStageStatus(stage.StageId!);
            Assert.Equal(RuntimePackageStageState.Failed, expired?.State);
            Assert.Contains("expired", expired?.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False((await service.CommitPackageStoreStageAsync(stage.StageId!)).Success);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(paths.RootPath);
        }
    }

    private static InstalledPackageRecord CreatePackageLayout(
        string rootPath,
        string folderName,
        string packageId,
        string version,
        PackageSourceKind sourceKind,
        IReadOnlyList<string>? dependencies = null,
        string dependencyVersionRange = ">=0.0.0-0")
    {
        var packageFolder = Path.Combine(rootPath, folderName, packageId, version);
        var assemblyPath = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        var dependencyRecords = (dependencies ?? [])
            .Select(dependencyId => new InstalledPackageDependencyRecord(dependencyId, dependencyVersionRange))
            .ToArray();
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            packageFolder,
            packageId,
            version,
            assemblyPath,
            dependencies: dependencyRecords);
        return CanonicalPackageTestBuilder.CreateInstalledRecord(
            packageFolder,
            packageId,
            version,
            dependencies: dependencyRecords);
    }

    private static void WritePackageManifest(
        string packageFolder,
        string packageId,
        string version,
        IReadOnlyList<string>? dependencies = null,
        string dependencyVersionRange = ">=0.0.0-0")
    {
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            packageFolder,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            dependencies: (dependencies ?? [])
                .Select(dependencyId => new InstalledPackageDependencyRecord(dependencyId, dependencyVersionRange))
                .ToArray());
    }

    private static async Task AddInstalledPackageAsync(InstalledPackageStore store, InstalledPackageRecord package)
    {
        var packages = (await store.ListAsync()).Append(package).ToArray();
        await store.WriteAsync(packages);
    }

    private static PackageLifecycleStageRequest CreateHotReloadStageRequest(string folder)
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest", "sunder-package.json")));
        var packageId = manifest.RootElement.GetProperty("id").GetString()!;
        return new PackageLifecycleStageRequest([packageId]);
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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}

public abstract class PackageSessionOverlayTestPackageModuleBase : ISunderRuntimePackageModule, ISunderAppPackageModule
{
    public abstract void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context);

    public abstract void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);

    public abstract void ConfigureAppServices(IServiceCollection services, IPackageContext context);

    public abstract void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services);
}

public sealed class PackageSessionOverlayTestPackageModule : PackageSessionOverlayTestPackageModuleBase
{
    public static bool RegisterStackContributor { get; set; }

    public static bool StackContributorContainsSecrets { get; set; }

    public override void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        if (string.Equals(context.PackageId, "reserved.package", StringComparison.Ordinal))
        {
            services.AddSingleton<IPackageContext>(context);
        }
        if (RuntimeActivationCounter.TryRecord(context, "configure"))
        {
            services.AddSingleton<RuntimeActivationCounterBackgroundService>();
        }
        if (string.Equals(context.PackageId, "generation.failing.package", StringComparison.Ordinal))
        {
            services.AddSingleton<FailingGenerationActivationBackgroundService>();
        }
        if (string.Equals(context.PackageId, "generation.cancelling.package", StringComparison.Ordinal))
        {
            services.AddSingleton<CancellableGenerationActivationBackgroundService>();
        }
    }

    public override void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        var context = services.GetRequiredService<IPackageContext>();
        if (RuntimeActivationCounter.TryRecord(context, "register"))
        {
            registry.RegisterBackgroundService<RuntimeActivationCounterBackgroundService>();
        }
        if (string.Equals(context.PackageId, "generation.failing.package", StringComparison.Ordinal))
        {
            registry.RegisterBackgroundService<FailingGenerationActivationBackgroundService>();
        }
        if (string.Equals(context.PackageId, "generation.cancelling.package", StringComparison.Ordinal))
        {
            registry.RegisterBackgroundService<CancellableGenerationActivationBackgroundService>();
        }
        if (!RegisterStackContributor)
        {
            return;
        }

        var stackContributor = new PackageSessionOverlayTestStackContributor(
            context.PackageId,
            context.Version.ToString(),
            StackContributorContainsSecrets);
        registry.RegisterStackContributor("test.stack.provider", stackContributor, services);
    }

    public override void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public override void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}

public sealed class RuntimeActivationCounterBackgroundService(IPackageContext context) : IPackageBackgroundService
{
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeActivationCounter.TryRecord(context, "start");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class FailingGenerationActivationBackgroundService : IPackageRuntimeGenerationParticipant
{
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Injected Runtime generation activation failure.");
}

public sealed class CancellableGenerationActivationBackgroundService(IPackageContext context)
    : IPackageRuntimeGenerationParticipant
{
    public const string CommitStartedPathEnvironmentVariable =
        "SUNDER_RUNTIME_TEST_CANCELLING_GENERATION_PATH";
    public const string TriggerVersionEnvironmentVariable =
        "SUNDER_RUNTIME_TEST_CANCELLING_GENERATION_VERSION";

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
    {
        var triggerVersion = Environment.GetEnvironmentVariable(TriggerVersionEnvironmentVariable);
        if (!string.Equals(context.Version.ToString(), triggerVersion, StringComparison.Ordinal))
        {
            return;
        }
        var path = Environment.GetEnvironmentVariable(CommitStartedPathEnvironmentVariable)
            ?? throw new InvalidOperationException("The generation cancellation test marker path is unavailable.");
        File.WriteAllText(path, generation.SessionGeneration.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

internal static class RuntimeActivationCounter
{
    public const string EnvironmentVariable = "SUNDER_RUNTIME_TEST_ACTIVATION_COUNTER_PATH";
    private const string CountedPackageId = "startup.counted.package";

    public static bool TryRecord(IPackageContext context, string stage)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.Equals(context.PackageId, CountedPackageId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        File.AppendAllLines(path, [$"{context.Version}:{stage}"]);
        return true;
    }
}

internal sealed class PackageSessionOverlayTestStackContributor(
    string packageId,
    string version,
    bool containsSecrets) : IPackageStackExporter, IPackageStackImporter
{
    public string ContributorId => "test.stack.contributor";

    public string DisplayName => "Test Stack Contributor";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(
            [new StackExportItemDescriptor(
                "test.profile",
                "Test Profile",
                "test-profile",
                "A test profile export.",
                Details:
                [
                    new StackExportItemDetail(
                        "Profile data",
                        "Test Profile",
                        containsSecrets ? StackValueSensitivity.Secret : StackValueSensitivity.Public),
                ])]);

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsItemSelected("test.profile"))
        {
            return ValueTask.FromResult(new StackExportContribution([], [], []));
        }

        var packageRequirement = new StackPackageRequirement(
            packageId,
            CreatedWithVersion: version,
            MinimumVersion: "1.0.0");
        var fragment = new StackFragmentExport(
            "test.profile",
            "test/profile",
            1,
            "Test Profile",
            "{\"name\":\"Test Profile\"}",
            "A test profile export.");
        return ValueTask.FromResult(new StackExportContribution([fragment], [packageRequirement], []));
    }

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportPreview([], [], [], []));

    public ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []));
}
