using System.IO.Compression;
using System.Runtime.InteropServices;
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
        Assert.True(File.Exists(GetEntryAssemblyPath(installedPackage)));
        Assert.Equal(InstalledPackageSourceKind.LocalArchive, installedPackage.Provenance?.SourceKind);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath))).ToLowerInvariant(),
            installedPackage.Provenance?.SourceIdentity);
    }

    [Fact]
    public async Task InstallFromPathAsync_WithTrustedRegistryProvenance_PersistsAndExposesUpdatePolicy()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var provenance = new InstalledPackageProvenanceRecord(
            InstalledPackageSourceKind.Registry,
            InstalledPackageVersionPolicy.FollowTag,
            "https://registry.example/",
            "test.package",
            RequestedTag: "beta",
            VersionRange: ">=1.0.0",
            SourceIdentity: new string('b', 64),
            IncludePrerelease: true);

        var result = await ExecuteAsync(
            paths,
            store,
            new SunderPackageArchiveInstaller(paths),
            new PackageStoreMutation(
                PackageStoreMutationKind.Install,
                ArchiveFilePath: archivePath,
                Provenance: provenance));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var installed = Assert.Single(await store.ListAsync());
        Assert.Equal(provenance, installed.Provenance);
        var descriptor = store.ToDescriptor(installed);
        Assert.Equal(InstalledPackageSourceKind.Registry, descriptor.Provenance.SourceKind);
        Assert.Equal("https://registry.example/", descriptor.Provenance.RegistryOrigin);
        Assert.Equal("beta", descriptor.Provenance.RequestedTag);
        Assert.True(descriptor.Provenance.IncludePrerelease);
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
    public async Task CommitPackageStoreStageAsync_PublishesExactStagedSessionWithoutReloading()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer, transferStore: transfers);

        try
        {
            var upload = await UploadPackageAsync(transfers, CreatePackageArchive(root, "test.package", "1.0.0"));
            var stage = await service.StagePackageStoreChangesAsync(new PackageStoreStageRequest([
                new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: upload.UploadId),
            ]));
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var stageId = Assert.IsType<string>(stage.StageId);
            var stagedSession = service.GetStagedStoreSession(stageId);

            var commit = await service.CommitPackageStoreStageAsync(stageId);

            Assert.True(commit.Success, string.Join(Environment.NewLine, commit.Errors));
            Assert.Same(stagedSession, service.ActiveSession);
            Assert.Equal(service.SessionGeneration, commit.CommittedStamp?.SessionGeneration);
            Assert.Equal("test.package", Assert.Single(await store.ListAsync()).PackageId);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CommitPackageStoreStageAsync_WhenPublicationFaultsAfterStoreCommit_ReturnsDegradedAndReconciles()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var faultInjector = new OneShotLifecycleFaultInjector();
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer,
            transferStore: transfers,
            lifecycleFaultInjector: faultInjector);

        try
        {
            var upload = await UploadPackageAsync(transfers, CreatePackageArchive(root, "test.package", "1.0.0"));
            var stage = await service.StagePackageStoreChangesAsync(new PackageStoreStageRequest([
                new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: upload.UploadId),
            ]));
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var stageId = Assert.IsType<string>(stage.StageId);
            var stagedSession = service.GetStagedStoreSession(stageId);
            var stagedFolder = stagedSession?.SessionFolder;

            var commit = await service.CommitPackageStoreStageAsync(stageId);

            Assert.True(commit.Success, string.Join(Environment.NewLine, commit.Errors));
            Assert.True(commit.StoreCommitted);
            Assert.False(commit.RuntimeSessionApplied);
            Assert.True(commit.RuntimeSessionReconciliationPending);
            Assert.NotSame(stagedSession, service.ActiveSession);
            Assert.Equal("test.package", Assert.Single(await store.ListAsync()).PackageId);
            if (stagedFolder is not null)
            {
                Assert.False(Directory.Exists(stagedFolder));
            }
            var pending = service.GetPackageStageStatus(stageId);
            Assert.Equal(RuntimePackageStageState.Committed, pending?.State);
            Assert.True(pending?.ReconciliationPending);

            var reconciliation = await service.LoadInstalledPackagesAsync();

            Assert.True(reconciliation.Success, string.Join(Environment.NewLine, reconciliation.Errors));
            Assert.Equal("test.package", Assert.Single(service.GetSessionPackages()).PackageId);
            var reconciled = service.GetPackageStageStatus(stageId);
            Assert.False(reconciled?.ReconciliationPending);
            Assert.True(reconciled?.RuntimeSessionApplied);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SetEnabledAsync_WhenDesiredStoreIsUnchanged_DoesNotReloadSession()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer);

        try
        {
            var install = await service.InstallPackageFromRuntimePathAsync(
                CreatePackageArchive(root, "test.package", "1.0.0"));
            Assert.True(install.Success, string.Join(Environment.NewLine, install.Errors));
            var generation = service.SessionGeneration;
            var session = service.ActiveSession;

            var noOp = await service.SetInstalledPackageEnabledAsync("test.package", isEnabled: true);

            Assert.True(noOp.Success, string.Join(Environment.NewLine, noOp.Errors));
            Assert.True(noOp.StoreCommitted);
            Assert.False(noOp.RuntimeSessionApplied);
            Assert.Equal(generation, service.SessionGeneration);
            Assert.Same(session, service.ActiveSession);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallFromRuntimePathAsync_PublishesCanonicalFinalSourcePaths()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer);

        try
        {
            var result = await service.InstallPackageFromRuntimePathAsync(
                CreatePackageArchive(root, "test.package", "1.0.0"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var installed = Assert.Single(await store.ListAsync());
            var source = Assert.Single(service.GetActiveRuntimePackageSources());
            Assert.True(paths.IsCanonicalInstalledPath(installed.PackageId, installed.Version, source.SourceFolder));
            Assert.Equal(Path.GetFullPath(installed.InstallPath), Path.GetFullPath(source.SourceFolder));
            Assert.Null(source.SnapshotFolder);
            var snapshot = Assert.Single(service.GetActivePackageUiSnapshots());
            using var lease = service.AcquireCurrentUiSnapshot(snapshot.SnapshotId);
            Assert.NotNull(lease);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CommitPackageStoreStageAsync_WhenStale_PreservesCurrentGenerationAndStore()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var service = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance, store, installer, transferStore: transfers);

        try
        {
            var staleUpload = await UploadPackageAsync(transfers, CreatePackageArchive(root, "stale.package", "1.0.0"));
            var staleStage = await service.StagePackageStoreChangesAsync(new PackageStoreStageRequest([
                new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: staleUpload.UploadId),
            ]));
            Assert.True(staleStage.Success, string.Join(Environment.NewLine, staleStage.Errors));
            var staleStageId = Assert.IsType<string>(staleStage.StageId);
            var currentUpload = await UploadPackageAsync(transfers, CreatePackageArchive(root, "current.package", "1.0.0"));
            var currentStage = await service.StagePackageStoreChangesAsync(new PackageStoreStageRequest([
                new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: currentUpload.UploadId),
            ]));
            Assert.True(currentStage.Success, string.Join(Environment.NewLine, currentStage.Errors));
            var currentStageId = Assert.IsType<string>(currentStage.StageId);
            var currentCommit = await service.CommitPackageStoreStageAsync(currentStageId);
            Assert.True(currentCommit.Success, string.Join(Environment.NewLine, currentCommit.Errors));
            var generation = service.SessionGeneration;

            var staleCommit = await service.CommitPackageStoreStageAsync(staleStageId);

            Assert.False(staleCommit.Success);
            Assert.Contains(staleCommit.Errors, error => error.Contains("stale", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(generation, service.SessionGeneration);
            Assert.Equal("current.package", Assert.Single(await store.ListAsync()).PackageId);
        }
        finally
        {
            await service.ShutdownAsync();
            TryDeleteDirectory(root);
        }
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
        Assert.True(File.Exists(GetEntryAssemblyPath(installedPackage)));
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
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "shared", "lib"));

        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ArchiveFormatVersion = 1,
            ManifestVersion = 1,
            Id = packageId,
            Name = "Test Package",
            Version = version,
            Targets =
            [
                CreateTarget(SunderPackageFormat.AppHostRole, SunderPackageFormat.AvaloniaTargetKind),
                CreateTarget(SunderPackageFormat.RuntimeHostRole, SunderPackageFormat.DotnetTargetKind),
            ],
        }));

        var entryAssemblyPath = Path.Combine(sourceRoot, "payload", "shared", "lib", "Test.Package.dll");
        File.Copy(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location, entryAssemblyPath);

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

    private static async Task<ContentUploadDescriptor> UploadPackageAsync(RuntimeContentTransferStore transfers, string archivePath)
    {
        await using var archive = File.OpenRead(archivePath);
        return await transfers.CreateUploadAsync(
            RuntimeUploadKind.Package,
            archive,
            archive.Length,
            null,
            Path.GetFileName(archivePath),
            "application/vnd.sunder.package",
            generation: 0,
            CancellationToken.None);
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
        var relativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (corruptHash)
        {
            hash = new string('0', hash.Length);
        }

        return new SunderPackageContentIndexEntry(
            relativePath,
            hash,
            new FileInfo(path).Length);
    }

    private static SunderPackageTargetManifest CreateTarget(string role, string kind)
        => new()
        {
            Role = role,
            Rid = RuntimeInformation.RuntimeIdentifier,
            Kind = kind,
            EntryPoint = "lib/Test.Package.dll",
            TargetFramework = "net10.0",
            SdkVersion = "1.1.0",
            RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "core.v1"],
        };

    private static string GetEntryAssemblyPath(InstalledPackageRecord package)
        => Path.Combine(
            package.InstallPath,
            package.ContentInventory.Single(entry => entry.Path.EndsWith(".dll", StringComparison.Ordinal)).Path
                .Replace('/', Path.DirectorySeparatorChar));

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
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class OneShotLifecycleFaultInjector : IInstalledPackageLifecycleFaultInjector
    {
        private int _remaining = 1;

        public void Hit(InstalledPackageLifecycleFaultPoint point)
        {
            if (point == InstalledPackageLifecycleFaultPoint.StoreCommittedBeforeSessionPublication
                && Interlocked.Exchange(ref _remaining, 0) == 1)
            {
                throw new IOException("Injected failure after the package store commit.");
            }
        }
    }
}
