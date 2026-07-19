using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
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

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 2)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 2)]
    public async Task SameVersionReinstall_WhenCommitMoveCrashes_RecoveryPreservesOriginal(
        int faultPointValue,
        int occurrence)
    {
        var fixture = await CreateFixtureAsync();
        var original = CreatePackageArchive(fixture.Root, "test.package", "1.0.0", payloadContent: "original");
        Assert.True((await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: original),
        ])).Success);
        var crashing = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new NthFaultInjector((PackageStoreFaultPoint)faultPointValue, occurrence));
        await crashing.InitializeAsync();
        var replacement = CreatePackageArchive(fixture.Root, "test.package", "1.0.0", payloadContent: "replacement");
        var preparation = await crashing.PrepareStageAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Upgrade,
                PackageId: "test.package",
                ArchiveFilePath: replacement,
                Reinstall: true),
        ]);

        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
            () => crashing.CommitStageAsync(preparation.Stage!.StageId));

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();
        await recovery.InitializeAsync();

        Assert.Equal("original", ReadPackagePayload(fixture.Paths, "test.package", "1.0.0"));
        Assert.Single(await fixture.Store.ListAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 2)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 2)]
    public async Task SameVersionReinstall_WhenRecoveryMoveCrashes_RepeatedRecoveryPreservesOriginal(
        int faultPointValue,
        int occurrence)
    {
        var fixture = await CreateFixtureAsync();
        var original = CreatePackageArchive(fixture.Root, "test.package", "1.0.0", payloadContent: "original");
        Assert.True((await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: original),
        ])).Success);
        var replacement = CreatePackageArchive(fixture.Root, "test.package", "1.0.0", payloadContent: "replacement");
        var crashingCommit = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new NthFaultInjector(PackageStoreFaultPoint.DirectoryMoveAfter, 2));
        await crashingCommit.InitializeAsync();
        var preparation = await crashingCommit.PrepareStageAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Upgrade,
                PackageId: "test.package",
                ArchiveFilePath: replacement,
                Reinstall: true),
        ]);
        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
            () => crashingCommit.CommitStageAsync(preparation.Stage!.StageId));
        var crashingRecovery = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new NthFaultInjector((PackageStoreFaultPoint)faultPointValue, occurrence));

        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(() => crashingRecovery.InitializeAsync());

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();
        await recovery.InitializeAsync();
        Assert.Equal("original", ReadPackagePayload(fixture.Paths, "test.package", "1.0.0"));
        Assert.Single(await fixture.Store.ListAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 2)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 2)]
    public async Task MultiPackageCommit_WhenMoveCrashes_RepeatedRecoveryRollsBackWholeTransaction(
        int faultPointValue,
        int occurrence)
    {
        var fixture = await CreateFixtureAsync(
            new NthFaultInjector((PackageStoreFaultPoint)faultPointValue, occurrence));
        var first = CreatePackageArchive(fixture.Root, "test.first", "1.0.0");
        var second = CreatePackageArchive(fixture.Root, "test.second", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: first),
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: second),
        ]);

        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
            () => fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId));

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();
        await recovery.InitializeAsync();
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.first", "1.0.0")));
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.second", "1.0.0")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 1)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveBefore, 2)]
    [InlineData((int)PackageStoreFaultPoint.DirectoryMoveAfter, 2)]
    public async Task MultiPackageRecovery_WhenMoveCrashes_RepeatedRecoveryRollsBackWholeTransaction(
        int faultPointValue,
        int occurrence)
    {
        var fixture = await CreateFixtureAsync(
            new NthFaultInjector(PackageStoreFaultPoint.DirectoryMoveAfter, 2));
        var first = CreatePackageArchive(fixture.Root, "test.first", "1.0.0");
        var second = CreatePackageArchive(fixture.Root, "test.second", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: first),
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: second),
        ]);
        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
            () => fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId));
        var crashingRecovery = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new NthFaultInjector((PackageStoreFaultPoint)faultPointValue, occurrence));

        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(() => crashingRecovery.InitializeAsync());

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();
        await recovery.InitializeAsync();
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.first", "1.0.0")));
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.second", "1.0.0")));
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
            2,
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
    public async Task Recovery_WhenJournalMetadataIsMalformed_RejectsBeforeFilesystemMutation()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDirectory = Path.Combine(paths.TransactionRootPath, transactionId);
        Directory.CreateDirectory(transactionDirectory);
        await File.WriteAllTextAsync(Path.Combine(transactionDirectory, "journal.json"), $$"""
            {
              "schemaVersion": 2,
              "transactionId": "{{transactionId}}",
              "phase": 999,
              "previousPackages": [],
              "desiredPackages": [],
              "actions": []
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCoordinator(paths, new InstalledPackageStore(paths)).InitializeAsync());

        Assert.True(File.Exists(Path.Combine(transactionDirectory, "journal.json")));
    }

    [Fact]
    public async Task Recovery_IgnoresInterruptedJournalTempAndRemovesUnpreparedTransaction()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var transactionDirectory = Path.Combine(paths.TransactionRootPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionDirectory);
        await File.WriteAllTextAsync(Path.Combine(transactionDirectory, "journal.json.tmp-interrupted"), "{truncated");

        await CreateCoordinator(paths, new InstalledPackageStore(paths)).InitializeAsync();

        Assert.False(Directory.Exists(transactionDirectory));
    }

    [Fact]
    public async Task ConcurrentRecovery_OfInterruptedPayloadMoveIsSerializedAndIdempotent()
    {
        var fixture = await CreateFixtureAsync(
            new OneShotFaultInjector(PackageStoreFaultPoint.PayloadMoved, simulatedCrash: true));
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
            () => fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId));
        var first = CreateCoordinator(fixture.Paths, fixture.Store);
        var second = CreateCoordinator(fixture.Paths, fixture.Store);

        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());

        Assert.Empty(await fixture.Store.ListAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.package", "1.0.0")));
    }

    [Fact]
    public async Task Commit_WhenTransactionDirectoryIsSymbolicLink_RejectsWithoutWritingOutsideRoot()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        var outsidePath = Path.Combine(fixture.Root, "outside-transaction");
        Directory.CreateDirectory(outsidePath);
        var transactionPath = Path.Combine(fixture.Paths.TransactionRootPath, preparation.Stage!.StageId);
        try
        {
            Directory.CreateSymbolicLink(transactionPath, outsidePath);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            await fixture.Coordinator.DiscardStageAsync(preparation.Stage.StageId);
            return;
        }

        var result = await fixture.Coordinator.CommitStageAsync(preparation.Stage.StageId);

        Assert.False(result.Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsidePath));
        Assert.Empty(await fixture.Store.ListAsync());
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
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
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
    public async Task OperationGate_ShutdownCancellationReachesActiveOperation()
    {
        var gate = new RuntimeOperationGate();
        await using var operation = await gate.EnterAsync();
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = operation.CancellationToken.Register(() => cancellationObserved.TrySetResult(true));
        var cleaned = false;
        var shutdown = gate.ShutdownAsync(() =>
        {
            cleaned = true;
            return Task.CompletedTask;
        });

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await gate.EnterAsync());
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);

        await operation.DisposeAsync();
        await shutdown;
        Assert.True(cleaned);
    }

    [Fact]
    public async Task OperationGate_ShutdownDrainsAfterCanceledOperationReleasesLease()
    {
        var gate = new RuntimeOperationGate();
        var operationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeOperation = Task.Run(async () =>
        {
            await using var operation = await gate.EnterAsync();
            operationEntered.SetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, operation.CancellationToken);
            }
            catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
            {
                cancellationObserved.SetResult(true);
                await releaseOperation.Task;
            }
        });
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cleanupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var shutdown = gate.ShutdownAsync(() =>
        {
            cleanupStarted.SetResult(true);
            return Task.CompletedTask;
        });

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cleanupStarted.Task.IsCompleted);
        Assert.False(shutdown.IsCompleted);
        releaseOperation.SetResult(true);
        await activeOperation.WaitAsync(TimeSpan.FromSeconds(2));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cleanupStarted.Task.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await gate.EnterAsync());
    }

    [Fact]
    public async Task OperationGate_ShutdownPhaseIsNotOverwrittenByLeaseRelease()
    {
        var events = new RuntimeEventStreamService();
        var gate = new RuntimeOperationGate(events);
        await using var operation = await gate.EnterAsync();

        var shutdown = gate.ShutdownAsync(static () => Task.CompletedTask);
        await operation.DisposeAsync();
        await shutdown;

        var phases = events.GetSnapshot().Events
            .Where(item => item.Kind == RuntimeEventKind.OperationPhaseChanged)
            .Select(item => item.OperationPhase)
            .ToArray();
        Assert.Equal([RuntimeOperationPhase.PackageOperation, RuntimeOperationPhase.ShuttingDown], phases);
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
    public void RuntimeRootLease_AvailabilityTracksOwnershipRelease()
    {
        var paths = new RuntimePackagePaths(Path.Combine(CreateTempDirectory(), "store"));
        var lease = RuntimeRootLease.Acquire(paths);

        Assert.False(RuntimeLocalState.IsLeaseAvailable(paths.RootPath));

        lease.Dispose();

        Assert.True(RuntimeLocalState.IsLeaseAvailable(paths.RootPath));
    }

    [Fact]
    public async Task PackageFault_WhenGenerationIsStale_CannotFaultReplacementSession()
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
                    PackageHostRoles.Runtime,
                    Icon: null,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    Views: [],
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
            });
        await state.PublishSessionAsync(new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)));
        var publication = await state.PublishSessionAsync(session);
        Assert.Equal(2, publication.Generation);

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
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null,
        string payloadContent = "test")
    {
        var sourceRoot = Path.Combine(root, "package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "lib"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "assets"));
        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ManifestVersion = 1,
            Id = packageId,
            Name = packageId,
            Version = version,
            EntryAssembly = packageId + ".dll",
            HostRoles = [SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole],
            SdkApiVersion = 1,
            SdkPackageVersion = "1.1.0",
            RequiredSdkCapabilities = ["sdk-baseline-1-1.v1", "core.v1"],
            DependsOn = (dependencies ?? []).Select(dependency => new SunderPackageDependencyManifest
            {
                PackageId = dependency.PackageId,
                VersionRange = dependency.VersionRange,
            }).ToArray(),
        }));
        File.Copy(
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            Path.Combine(sourceRoot, "payload", "lib", packageId + ".dll"));
        File.WriteAllText(Path.Combine(sourceRoot, "payload", "assets", "test-content.txt"), payloadContent);
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

    private static string ReadPackagePayload(
        RuntimePackagePaths paths,
        string packageId,
        string version) =>
        File.ReadAllText(Path.Combine(
            paths.GetInstalledPackagePath(packageId, version),
            "payload",
            "assets",
            "test-content.txt"));

    private static SunderPackageContentIndexEntry CreateIndexEntry(string sourceRoot, string path)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        using var stream = File.OpenRead(path);
        return new SunderPackageContentIndexEntry(
            relativePath,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            new FileInfo(path).Length,
            SunderPackageFormat.GetContentRole(relativePath) ?? "file");
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

    private sealed class NthFaultInjector(PackageStoreFaultPoint point, int occurrence) : IPackageStoreFaultInjector
    {
        private int _hits;

        public void Hit(PackageStoreFaultPoint candidate)
        {
            if (candidate == point && Interlocked.Increment(ref _hits) == occurrence)
            {
                throw new PackageStoreSimulatedCrashException(point);
            }
        }
    }
}
