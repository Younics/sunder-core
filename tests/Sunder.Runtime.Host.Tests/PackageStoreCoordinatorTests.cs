using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStoreCoordinatorTests
{
    [Fact]
    public async Task Batch_ValidatesOneFinalCatalogAndCommitsDependenciesAtomically()
    {
        var fixture = await CreateFixtureAsync();
        var dependent = CreatePackageArchive(
            fixture.Root,
            "test.dependent",
            "1.0.0",
            [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0 <2.0.0")]);
        var dependency = CreatePackageArchive(fixture.Root, "test.dependency", "1.0.0");

        var result = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: dependent),
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: dependency),
        ]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            ["test.dependency", "test.dependent"],
            (await fixture.Store.ListAsync()).Select(package => package.PackageId).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Batch_WhenFinalGraphContainsCycle_CommitsNothing()
    {
        var fixture = await CreateFixtureAsync();
        var first = CreatePackageArchive(
            fixture.Root,
            "test.first",
            "1.0.0",
            [new InstalledPackageDependencyRecord("test.second", ">=1.0.0")]);
        var second = CreatePackageArchive(
            fixture.Root,
            "test.second",
            "1.0.0",
            [new InstalledPackageDependencyRecord("test.first", ">=1.0.0")]);

        var result = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: first),
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: second),
        ]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.InstalledRootPath));
    }

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.JournalPrepared, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadsCommitting, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadMoved, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadsCommitted, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplacing, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplaced, true)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitted, true)]
    [InlineData((int)PackageStoreFaultPoint.Cleanup, true)]
    public async Task FaultAtTransactionPhase_StartupRecoveryProducesWholeCatalog(
        int faultPointValue,
        bool expectedCommitted)
    {
        var faultPoint = (PackageStoreFaultPoint)faultPointValue;
        var injector = new OneShotFaultInjector(faultPoint, simulatedCrash: true);
        var fixture = await CreateFixtureAsync(injector);
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        Assert.NotNull(preparation.Stage);

        if (faultPoint == PackageStoreFaultPoint.Cleanup)
        {
            var result = await fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId);
            Assert.True(result.Success);
        }
        else
        {
            await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
                () => fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId));
        }

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();

        Assert.Equal(expectedCommitted, (await fixture.Store.ListAsync()).Count == 1);
        Assert.Equal(expectedCommitted, Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.package", "1.0.0")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task Commit_WhenCancelledBeforeJournal_LeavesCatalogAndPayloadUntouched()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId, cancellation.Token));

        Assert.Empty(await fixture.Store.ListAsync());
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.package", "1.0.0")));
    }

    [Fact]
    public async Task Commit_WhenCatalogGenerationChanged_RejectsStaleStage()
    {
        var fixture = await CreateFixtureAsync();
        var firstArchive = CreatePackageArchive(fixture.Root, "test.first", "1.0.0");
        var secondArchive = CreatePackageArchive(fixture.Root, "test.second", "1.0.0");
        var stale = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: firstArchive),
        ]);
        var committed = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: secondArchive),
        ]);

        var staleResult = await fixture.Coordinator.CommitStageAsync(stale.Stage!.StageId);

        Assert.True(committed.Success);
        Assert.False(staleResult.Success);
        Assert.Contains("stale", staleResult.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("test.second", Assert.Single(await fixture.Store.ListAsync()).PackageId);
    }

    [Fact]
    public async Task CleanupFailure_LeavesTombstoneForRetryWithoutResurrectingPackage()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        Assert.True((await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ])).Success);
        var failingCoordinator = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new OneShotFaultInjector(PackageStoreFaultPoint.Cleanup, simulatedCrash: false));
        await failingCoordinator.InitializeAsync();

        var result = await failingCoordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Uninstall, "test.package"),
        ]);

        Assert.True(result.Success);
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Paths.TombstoneRootPath));

        await CreateCoordinator(fixture.Paths, fixture.Store).InitializeAsync();
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TombstoneRootPath));
        Assert.Empty(await fixture.Store.ListAsync());
    }

    [Fact]
    public async Task Recovery_WhenJournalContainsUnsafePath_RejectsWithoutDeletingOutsideDirectory()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDirectory = Path.Combine(paths.TransactionRootPath, transactionId);
        var outsidePath = Path.Combine(root, "outside");
        Directory.CreateDirectory(transactionDirectory);
        Directory.CreateDirectory(outsidePath);
        var journal = new PackageStoreTransactionJournal(
            1,
            transactionId,
            PackageStoreTransactionPhase.PayloadsCommitting,
            [],
            [],
            [new PackageStoreJournalAction(
                PackageStoreJournalActionKind.Remove,
                null,
                outsidePath,
                null,
                Path.Combine(paths.TombstoneRootPath, "unsafe"))]);
        await DurableJsonDocument.WriteAsync(
            Path.Combine(transactionDirectory, "journal.json"),
            journal,
            InstalledPackageStore.JsonOptions);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCoordinator(paths, store).InitializeAsync());

        Assert.True(Directory.Exists(outsidePath));
    }

    [Fact]
    public async Task ConcurrentFacadeMutations_AreSerializedByOneOperationGate()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var gate = new RuntimeOperationGate();
        var coordinator = new PackageStoreCoordinator(paths, store, installer);
        var service = new RuntimePackageSessionService(
            NullLogger<RuntimePackageSessionService>.Instance,
            store,
            installer,
            gate,
            coordinator);
        await service.InitializeAsync();
        var first = CreatePackageArchive(root, "test.first", "1.0.0");
        var second = CreatePackageArchive(root, "test.second", "1.0.0");

        var results = await Task.WhenAll(
            service.InstallPackageFromRuntimePathAsync(first),
            service.InstallPackageFromRuntimePathAsync(second));

        Assert.All(results, result => Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors)));
        Assert.Equal(2, (await store.ListAsync()).Count);
        await service.ShutdownAsync();
    }

    [Fact]
    public async Task OperationGate_ShutdownCancelsActiveLeaseDrainsAndRejectsNewWork()
    {
        var gate = new RuntimeOperationGate();
        await using var operation = await gate.EnterAsync();
        var cleaned = false;
        var shutdown = gate.ShutdownAsync(() =>
        {
            cleaned = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await gate.EnterAsync());
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);

        await operation.DisposeAsync();
        await shutdown;
        Assert.True(cleaned);
    }

    [Fact]
    public void RuntimeRootLease_WhenRootIsAlreadyOwned_ReturnsClearContentionError()
    {
        var paths = new RuntimePackagePaths(Path.Combine(CreateTempDirectory(), "store"));
        using var first = RuntimeRootLease.Acquire(paths);

        var error = Assert.Throws<InvalidOperationException>(() => RuntimeRootLease.Acquire(paths));

        Assert.Contains("Another Sunder Runtime", error.Message, StringComparison.Ordinal);
        Assert.Contains(paths.RootPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageFault_WhenGenerationIsStale_CannotFaultReplacementSession()
    {
        var state = new PackageSessionState(
            NullLogger.Instance,
            static () => { },
            static _ => { });
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = new SessionPackageDescriptor(
                    "test.package",
                    "Test Package",
                    "2.0.0",
                    Icon: null,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    Views: [],
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
            });
        state.PublishSession(ActivePackageSession.Empty);
        Assert.Equal(2, state.PublishSession(session));

        var stale = state.ReportPackageFault(
            "test.package",
            new ReportPackageFaultRequest(PackageFailureOrigin.AppHostedView, "old view failed", GenerationId: 1));

        Assert.False(stale);
        Assert.True(Assert.Single(state.GetSessionPackages()).IsEnabled);
    }

    private static async Task<Fixture> CreateFixtureAsync(IPackageStoreFaultInjector? faultInjector = null)
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var coordinator = CreateCoordinator(paths, store, faultInjector);
        await coordinator.InitializeAsync();
        return new Fixture(root, paths, store, coordinator);
    }

    private static PackageStoreCoordinator CreateCoordinator(
        RuntimePackagePaths paths,
        InstalledPackageStore store,
        IPackageStoreFaultInjector? faultInjector = null)
    {
        var installer = new SunderPackageArchiveInstaller(paths);
        return new PackageStoreCoordinator(paths, store, installer, faultInjector);
    }

    private static string CreatePackageArchive(
        string root,
        string packageId,
        string version,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null)
    {
        var sourceRoot = Path.Combine(root, "package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "lib"));
        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new RuntimePackageManifest
        {
            ManifestVersion = 1,
            Id = packageId,
            Name = packageId,
            Version = version,
            EntryAssembly = packageId + ".dll",
            SdkApiVersion = 1,
            SdkPackageVersion = "1.0.0",
            RequiredSdkCapabilities = ["core.v1"],
            DependsOn = (dependencies ?? []).Select(dependency => new RuntimePackageDependencyManifest
            {
                PackageId = dependency.PackageId,
                VersionRange = dependency.VersionRange,
            }).ToArray(),
        }));
        File.WriteAllText(Path.Combine(sourceRoot, "payload", "lib", packageId + ".dll"), "test");
        var entries = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => CreateIndexEntry(sourceRoot, path))
            .ToArray();
        File.WriteAllText(
            Path.Combine(sourceRoot, "manifest", "content-index.json"),
            JsonSerializer.Serialize(new SunderPackageContentIndex(1, entries)));
        var archivePath = Path.Combine(root, $"{packageId}.{version}.{Guid.NewGuid():N}.sunderpkg");
        ZipFile.CreateFromDirectory(sourceRoot, archivePath);
        return archivePath;
    }

    private static SunderPackageContentIndexEntry CreateIndexEntry(string sourceRoot, string path)
    {
        using var stream = File.OpenRead(path);
        return new SunderPackageContentIndexEntry(
            Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'),
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            new FileInfo(path).Length,
            "runtime");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record Fixture(
        string Root,
        RuntimePackagePaths Paths,
        InstalledPackageStore Store,
        PackageStoreCoordinator Coordinator);

    private sealed class OneShotFaultInjector(PackageStoreFaultPoint point, bool simulatedCrash)
        : IPackageStoreFaultInjector
    {
        private int _hit;

        public void Hit(PackageStoreFaultPoint candidate)
        {
            if (candidate != point || Interlocked.Exchange(ref _hit, 1) != 0)
            {
                return;
            }

            if (simulatedCrash)
            {
                throw new PackageStoreSimulatedCrashException(point);
            }

            throw new IOException($"Injected cleanup failure at {point}.");
        }
    }
}
