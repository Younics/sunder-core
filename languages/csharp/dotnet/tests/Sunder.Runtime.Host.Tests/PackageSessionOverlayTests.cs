using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class PackageSessionOverlayTests
{
    [Fact]
    public async Task LoadInstalledWithDevOverlays_WhenPackageIdsCollide_UsesDevPackageSource()
    {
        var rootPath = CreateTempDirectory();
        var installedPackage = CreatePackageLayout(
            rootPath,
            "installed",
            "test.package",
            "1.0.0",
            PackageSourceKind.Installed,
            roles: [SunderPackageFormat.AppHostRole]);
        var devFolder = CreatePackageLayout(
            rootPath,
            "dev",
            "test.package",
            "2.0.0",
            PackageSourceKind.Dev,
            roles: [SunderPackageFormat.AppHostRole]).InstallPath;
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
            ">=1.0.0 <2.0.0",
            [SunderPackageFormat.AppHostRole]);
        var installedCore = CreatePackageLayout(
            rootPath,
            "installed",
            "core.package",
            "1.5.0",
            PackageSourceKind.Installed,
            roles: [SunderPackageFormat.AppHostRole]);
        var devCoreFolder = CreatePackageLayout(
            rootPath,
            "dev",
            "core.package",
            "2.0.0",
            PackageSourceKind.Dev,
            roles: [SunderPackageFormat.AppHostRole]).InstallPath;
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
            ["z.root"],
            roles: [SunderPackageFormat.AppHostRole]);
        var rootPackage = CreatePackageLayout(
            rootPath,
            "installed",
            "z.root",
            "1.0.0",
            PackageSourceKind.Installed,
            roles: [SunderPackageFormat.AppHostRole]);
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
    public async Task LoadDevAsync_WhenPackageDeclaresLegacyDotnetRuntimeTarget_RejectsBeforeActivation()
    {
        var rootPath = CreateTempDirectory();
        var folder = Path.Combine(rootPath, "dev", "legacy.package", "1.0.0");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            folder,
            "legacy.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeTargetKind: "dotnet");
        var loadService = new PackageSessionLoadService(NullLogger.Instance);

        try
        {
            var result = await loadService.LoadInstalledWithDevOverlaysAsync([], [folder]);

            Assert.Contains(result.Errors, error =>
                error.Contains("kind 'dotnet'", StringComparison.Ordinal)
                && error.Contains("invalid for role 'runtime'", StringComparison.Ordinal));
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
    public async Task LoadInstalledWithDevOverlays_CancellationAfterFirstPreparationReleasesRuntimeRetention()
    {
        var rootPath = CreateTempDirectory();
        var first = CreatePackageLayout(
            rootPath,
            "dev-a",
            "a.package",
            "1.0.0",
            PackageSourceKind.Dev,
            roles: [SunderPackageFormat.RuntimeHostRole]).InstallPath;
        var second = CreatePackageLayout(
            rootPath,
            "dev-b",
            "b.package",
            "1.0.0",
            PackageSourceKind.Dev,
            roles: [SunderPackageFormat.RuntimeHostRole]).InstallPath;
        using var cancellation = new CancellationTokenSource();
        var bindingResolver = new CancellingBindingResolver(cancellation);
        var loadService = new PackageSessionLoadService(
            NullLogger.Instance,
            executionRuntimeLaunchResolver: TestExecutionRuntimeLaunchResolver.Instance,
            ephemeralExecutionRuntimeBindingResolver: bindingResolver,
            concurrency: new PackageSessionConcurrencyOptions { MaximumPreparationConcurrency = 1 });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                loadService.LoadInstalledWithDevOverlaysAsync(
                    [],
                    [first, second],
                    cancellationToken: cancellation.Token));

            Assert.Equal(1, bindingResolver.CreatedRetentions);
            Assert.Equal(1, bindingResolver.DisposedRetentions);
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
            await service.ShutdownAsync();
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
            await service.ShutdownAsync();
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
            await service.ShutdownAsync();
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
                installedPackage.ContentInventory.First(entry => entry.Path.EndsWith(".dll", StringComparison.Ordinal)).Path.Replace('/', Path.DirectorySeparatorChar)));

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
            await service.ShutdownAsync();
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
            await service.ShutdownAsync();
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
            await service.ShutdownAsync();
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
        string dependencyVersionRange = ">=0.0.0-0",
        IReadOnlyList<string>? roles = null)
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
            roles: roles,
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

    private sealed class CancellingBindingResolver(CancellationTokenSource cancellation)
        : IEphemeralExecutionRuntimeBindingResolver
    {
        private int _createdRetentions;
        private int _disposedRetentions;

        public int CreatedRetentions => Volatile.Read(ref _createdRetentions);
        public int DisposedRetentions => Volatile.Read(ref _disposedRetentions);

        public async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(
            SunderPackageTargetKey targetKey,
            SunderPackageTargetManifest target,
            CancellationToken cancellationToken)
        {
            var synthesized = await TestExecutionRuntimeLaunchResolver.Instance.ResolveEphemeralBindingAsync(
                targetKey,
                target,
                cancellationToken);
            await synthesized.DisposeAsync();
            Interlocked.Increment(ref _createdRetentions);
            cancellation.Cancel();
            return new ResolvedEphemeralExecutionRuntimeBinding(
                synthesized.Binding,
                new CallbackAsyncDisposable(() => Interlocked.Increment(ref _disposedRetentions)));
        }
    }

    private sealed class CallbackAsyncDisposable(Action dispose) : IAsyncDisposable
    {
        private Action? _dispose = dispose;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}

public abstract class PackageSessionOverlayTestPackageModuleBase : ISunderAppPackageModule
{
    public abstract void ConfigureAppServices(IServiceCollection services, IPackageContext context);

    public abstract void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services);
}

public sealed class PackageSessionOverlayTestPackageModule : PackageSessionOverlayTestPackageModuleBase
{
    public override void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public override void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}
