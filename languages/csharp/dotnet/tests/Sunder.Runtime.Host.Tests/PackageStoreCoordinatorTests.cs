using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
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
    public async Task ArchiveMutation_OverlaysSuppliedExactRuntimeBindingsOnlyAtCommit()
    {
        var fixture = await CreateFixtureAsync();
        var binding = CreateRuntimeBinding();
        var archive = CreatePackageArchive(fixture.Root, "test.bound", "1.0.0");

        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Install,
                ArchiveFilePath: archive,
                ExecutionRuntimeBindings: [binding]),
        ]);

        Assert.True(preparation.Success, preparation.Failure?.Message);
        Assert.Equal(
            binding,
            Assert.Single(Assert.Single(preparation.Stage!.ProspectivePackages).ExecutionRuntimeBindings));
        Assert.Contains(
            binding,
            (await fixture.Coordinator.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(CancellationToken.None))
            .PackageBindings);
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.False(File.Exists(fixture.Paths.StateFilePath));
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.bound", "1.0.0")));

        var committed = await fixture.Coordinator.CommitStageAsync(preparation.Stage.StageId);

        Assert.True(committed.Success, string.Join(Environment.NewLine, committed.Errors));
        Assert.Equal(binding, Assert.Single(Assert.Single(await fixture.Store.ListAsync()).ExecutionRuntimeBindings));
        Assert.Contains(
            binding,
            (await fixture.Coordinator.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(CancellationToken.None))
            .PackageBindings);
        Assert.True(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.bound", "1.0.0")));
    }

    [Fact]
    public async Task ArchiveMutation_WhenSuppliedBindingMismatchesManifest_RejectsWholeBatch()
    {
        var fixture = await CreateFixtureAsync();
        var validArchive = CreatePackageArchive(fixture.Root, "test.valid", "1.0.0");
        var mismatchedArchive = CreatePackageArchive(fixture.Root, "test.mismatch", "1.0.0");
        var mismatchedBinding = CreateRuntimeBinding() with { RuntimeVersion = "11.0.0" };

        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Install,
                ArchiveFilePath: validArchive,
                ExecutionRuntimeBindings: [CreateRuntimeBinding()]),
            new PackageStoreMutation(
                PackageStoreMutationKind.Install,
                ArchiveFilePath: mismatchedArchive,
                ExecutionRuntimeBindings: [mismatchedBinding]),
        ]);

        Assert.False(preparation.Success);
        Assert.Null(preparation.Stage);
        Assert.Contains("strict manifest", preparation.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.valid", "1.0.0")));
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.mismatch", "1.0.0")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.StagingRootPath));
    }

    [Fact]
    public async Task RuntimeRebind_CommitsMetadataOnlyAndPreservesEveryOtherPackageFieldAndFile()
    {
        var faultRecorder = new RecordingFaultInjector();
        var fixture = await CreateInstalledFixtureAsync(CreateRuntimeBinding(), faultRecorder);
        var before = Assert.Single(await fixture.Store.ListAsync());
        var filesBefore = SnapshotPackageFiles(before.InstallPath);
        var desiredBinding = CreateRuntimeBinding(
            runtimeVersion: "10.0.2",
            packagingRevision: 2,
            manifestHash: 'c',
            artifactHash: 'd',
            runtimePolicyRevision: 2);

        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageExecutionRuntimeRebindMutation(
                before.PackageId,
                before,
                [desiredBinding]),
        ]);

        Assert.True(preparation.Success, preparation.Failure?.Message);
        Assert.Empty(preparation.Stage!.PreparationSourcePaths);
        Assert.Equal(desiredBinding, Assert.Single(Assert.Single(preparation.Stage.ProspectivePackages).ExecutionRuntimeBindings));
        var unchanged = Assert.Single(await fixture.Store.ListAsync());
        AssertPackageFieldsExceptBindingsEqual(before, unchanged);
        Assert.Equal(Assert.Single(before.ExecutionRuntimeBindings), Assert.Single(unchanged.ExecutionRuntimeBindings));

        var committed = await fixture.Coordinator.CommitStageAsync(preparation.Stage.StageId);

        Assert.True(committed.Success, string.Join(Environment.NewLine, committed.Errors));
        var after = Assert.Single(await fixture.Store.ListAsync());
        AssertPackageFieldsExceptBindingsEqual(before, after);
        Assert.Equal(desiredBinding, Assert.Single(after.ExecutionRuntimeBindings));
        AssertPackageFilesEqual(filesBefore, SnapshotPackageFiles(after.InstallPath));
        Assert.DoesNotContain(PackageStoreFaultPoint.DirectoryMoveBefore, faultRecorder.Points);
        Assert.DoesNotContain(PackageStoreFaultPoint.DirectoryMoveAfter, faultRecorder.Points);
        Assert.DoesNotContain(PackageStoreFaultPoint.PayloadMoved, faultRecorder.Points);
    }

    [Fact]
    public async Task RuntimeRebind_WhenExpectedPackageIsStale_RejectsWithoutMutation()
    {
        var fixture = await CreateInstalledFixtureAsync(CreateRuntimeBinding());
        var current = Assert.Single(await fixture.Store.ListAsync());
        var staleExpected = current with { InstalledAtUtc = current.InstalledAtUtc.AddTicks(1) };
        var desiredBinding = CreateRuntimeBinding(
            runtimeVersion: "10.0.2",
            packagingRevision: 2,
            manifestHash: 'c',
            artifactHash: 'd',
            runtimePolicyRevision: 2);

        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageExecutionRuntimeRebindMutation(
                current.PackageId,
                staleExpected,
                [desiredBinding]),
        ]);

        Assert.False(preparation.Success);
        Assert.Null(preparation.Stage);
        Assert.Contains("stale", preparation.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        var persisted = Assert.Single(await fixture.Store.ListAsync());
        AssertPackageFieldsExceptBindingsEqual(current, persisted);
        Assert.Equal(Assert.Single(current.ExecutionRuntimeBindings), Assert.Single(persisted.ExecutionRuntimeBindings));
    }

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplacing, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplaced, true)]
    [InlineData((int)PackageStoreFaultPoint.PolicyReconciling, true)]
    [InlineData((int)PackageStoreFaultPoint.PolicyReconciled, true)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitRecording, true)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitted, true)]
    [InlineData((int)PackageStoreFaultPoint.Cleanup, true)]
    public async Task RuntimePolicyUpdate_FaultRecoveryCouplesCatalogAndPolicyEvidence(
        int faultPointValue,
        bool expectedUpdated)
    {
        var faultPoint = (PackageStoreFaultPoint)faultPointValue;
        var initialBinding = CreateRuntimeBinding();
        var fixture = await CreateInstalledFixtureAsync(initialBinding);
        var initialPolicy = CreateRuntimePolicyEntry(
            initialBinding,
            RuntimeLifecycle(ExecutionRuntimeLifecycleStates.Supported));
        var policyCache = new ExecutionRuntimeLifecyclePolicyCache(
            new ExecutionRuntimeStoreLayout(fixture.Paths));
        await policyCache.ApplyEntriesAsync([initialPolicy]);
        var crashing = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new OneShotFaultInjector(
                (PackageStoreFaultPoint)faultPointValue,
                simulatedCrash: true),
            policyCache);
        await crashing.InitializeAsync();
        var installed = Assert.Single(await fixture.Store.ListAsync());
        var desiredBinding = initialBinding with
        {
            RuntimePolicyRevision = 2,
            RuntimeLifecycleState = ExecutionRuntimeLifecycleStates.Supported,
            PolicyObservedAtUtc = initialBinding.PolicyObservedAtUtc.AddMinutes(2),
            ResolvedAtUtc = initialBinding.ResolvedAtUtc.AddMinutes(2),
        };
        var desiredPolicy = CreateRuntimePolicyEntry(
            desiredBinding,
            RuntimeLifecycle(ExecutionRuntimeLifecycleStates.Supported),
            ExecutionRuntimeTrustDispositions.Denied);
        var preparation = await crashing.PrepareStageAsync(
            [new PackageExecutionRuntimePolicyUpdateMutation(
                installed.PackageId,
                installed,
                [desiredBinding])],
            localBindingAuthority: null,
            registryRuntimePolicyUpdates: [desiredPolicy],
            registryRuntimePolicyAuthority: new ExecutionRuntimeLifecyclePolicySnapshot(
                policyCache.CaptureSnapshot().Generation + 1,
                [desiredPolicy]),
            CancellationToken.None);
        Assert.True(preparation.Success, preparation.Failure?.Message);

        if (faultPoint == PackageStoreFaultPoint.Cleanup)
        {
            var result = await crashing.CommitStageAsync(preparation.Stage!.StageId);
            Assert.True(result.Success, result.Message);
        }
        else
        {
            await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
                () => crashing.CommitStageAsync(preparation.Stage!.StageId));
        }

        var recoveredPolicies = new ExecutionRuntimeLifecyclePolicyCache(
            new ExecutionRuntimeStoreLayout(fixture.Paths));
        var recovery = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            lifecyclePolicies: recoveredPolicies);
        await recovery.InitializeAsync();
        await recoveredPolicies.InitializeAsync();

        var recoveredBinding = Assert.Single(
            Assert.Single(await fixture.Store.ListAsync()).ExecutionRuntimeBindings);
        var recoveredPolicy = Assert.Single(recoveredPolicies.CaptureSnapshot().Entries);
        Assert.Equal(expectedUpdated ? desiredBinding : initialBinding, recoveredBinding);
        Assert.Equal(expectedUpdated ? desiredPolicy : initialPolicy, recoveredPolicy);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task UninstallPlan_RequiresExactCascadeConsentAndReportsRemovedPackages()
    {
        var fixture = await CreateFixtureAsync();
        var dependent = CreatePackageArchive(
            fixture.Root,
            "test.dependent",
            "1.0.0",
            [new InstalledPackageDependencyRecord("test.dependency", ">=1.0.0 <2.0.0")]);
        var dependency = CreatePackageArchive(fixture.Root, "test.dependency", "1.0.0");
        Assert.True((await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: dependent),
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: dependency),
        ])).Success);
        var plan = PackageStorePolicy.CreateUninstallPlan("test.dependency", await fixture.Store.ListAsync());
        var retainedDataPath = Path.Combine(fixture.Paths.PackageDataRootPath, "test.dependency", "data", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(retainedDataPath)!);
        await File.WriteAllTextAsync(retainedDataPath, "{}");

        var denied = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Uninstall,
                "test.dependency",
                ConfirmationToken: plan.ConfirmationToken),
        ]);

        Assert.False(denied.Success);
        Assert.Contains("cascade consent", denied.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["test.dependency"], plan.DirectRemovals.Select(package => package.PackageId));
        Assert.Equal(["test.dependent"], plan.CascadingRemovals.Select(package => package.PackageId));
        Assert.Equal(PackageUninstallDataBehavior.Retain, plan.DataBehavior);
        Assert.Equal(plan.ExpectedRemovalPackageIds, plan.ReloadImpact.RemovedPackageIds);

        Assert.True((await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Disable, "test.dependent"),
        ])).Success);
        var stale = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Uninstall,
                "test.dependency",
                AllowCascade: true,
                ConfirmationToken: plan.ConfirmationToken),
        ]);
        Assert.False(stale.Success);
        Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
        var refreshedPlan = PackageStorePolicy.CreateUninstallPlan(
            "test.dependency",
            await fixture.Store.ListAsync());

        var committed = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Uninstall,
                "test.dependency",
                AllowCascade: true,
                ConfirmationToken: refreshedPlan.ConfirmationToken),
        ]);

        Assert.True(committed.Success, string.Join(Environment.NewLine, committed.Errors));
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.Equal(["test.dependency", "test.dependent"], committed.ChangeSet.RemovedPackageIds);
        Assert.True(File.Exists(retainedDataPath));
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
    [InlineData((int)PackageStoreFaultPoint.DurableCommitStarted, false)]
    [InlineData((int)PackageStoreFaultPoint.JournalPrepared, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadsCommitting, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadMoved, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadsCommitted, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplacing, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplaced, true)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitRecording, true)]
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
    public async Task PrepareStage_PersistsPreparedCutoverBeforeReturningCandidate()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");

        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);

        var stage = Assert.IsType<PackageStorePreparedStage>(preparation.Stage);
        var journal = ReadJournal(fixture.Paths, stage.StageId);
        Assert.Equal(4, journal.SchemaVersion);
        Assert.Equal(PackageStoreCutoverPhase.Prepared, journal.CutoverPhase);
        Assert.Equal(PackageStoreTransactionPhase.Prepared, journal.Phase);
        Assert.False(journal.RequiresRuntimePublication);
        Assert.Empty(await fixture.Store.ListAsync());

        Assert.True(await fixture.Coordinator.DiscardStageAsync(stage.StageId));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task PreparedCutoverCrash_StartupRecoveryRestoresPreviousCatalog()
    {
        var fixture = await CreateFixtureAsync(
            new OneShotFaultInjector(PackageStoreFaultPoint.CutoverPrepared, simulatedCrash: true));
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");

        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(() =>
            fixture.Coordinator.PrepareStageAsync([
                new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
            ]));

        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();

        Assert.Empty(await fixture.Store.ListAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.StagingRootPath));
    }

    [Fact]
    public async Task DurableLifecycleCutover_RemainsUntilRecoveredGenerationCompletes()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        var stageId = Assert.IsType<string>(preparation.Stage?.StageId);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.PreLaunchPolicyConfirmed);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.CandidateReady);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.PreCommitPolicyConfirmed);

        var commit = await fixture.Coordinator.CommitStageWithOutcomeAsync(
            stageId,
            CancellationToken.None,
            retainCutoverForPublication: true);

        Assert.Equal(PackageStoreCommitDisposition.Committed, commit.Disposition);
        var durable = ReadJournal(fixture.Paths, stageId);
        Assert.True(durable.RequiresRuntimePublication);
        Assert.Equal(PackageStoreCutoverPhase.DurableCommitted, durable.CutoverPhase);
        Assert.Equal(PackageStoreTransactionPhase.Completed, durable.Phase);

        var blockedArchive = CreatePackageArchive(fixture.Root, "test.second", "1.0.0");
        var blocked = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: blockedArchive),
        ]);
        Assert.False(blocked.Success);
        Assert.Contains("reconciliation", blocked.Failure?.Message, StringComparison.OrdinalIgnoreCase);

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.TransactionRootPath, stageId)));

        Assert.Empty(await recovery.CompleteRecoveredCutoversAsync(
            CancellationToken.None,
            acknowledgeEmptyGeneration: true));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
        Assert.Equal("test.package", Assert.Single(await fixture.Store.ListAsync()).PackageId);
    }

    [Fact]
    public async Task CompletedCutover_DisarmsSnapshotsAndAllowsCleanupAfterNewerCatalogCommit()
    {
        var fixture = await CreateFixtureAsync();
        var originalArchive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        Assert.True((await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: originalArchive),
        ])).Success);
        var packageDataPath = Path.Combine(fixture.Paths.PackageDataRootPath, "test.package", "data");
        Directory.CreateDirectory(packageDataPath);
        var statePath = Path.Combine(packageDataPath, "state.json");
        await File.WriteAllTextAsync(statePath, "before");

        var coordinator = CreateCoordinator(
            fixture.Paths,
            fixture.Store,
            new OneShotFaultInjector(PackageStoreFaultPoint.Cleanup, simulatedCrash: false));
        await coordinator.InitializeAsync();
        var replacementArchive = CreatePackageArchive(fixture.Root, "test.package", "2.0.0");
        var preparation = await coordinator.PrepareStageAsync([
            new PackageStoreMutation(
                PackageStoreMutationKind.Upgrade,
                "test.package",
                replacementArchive),
        ]);
        var stageId = Assert.IsType<string>(preparation.Stage?.StageId);
        await coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.PreLaunchPolicyConfirmed);
        await coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.CandidateReady);
        await coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.PreCommitPolicyConfirmed);
        var commit = await coordinator.CommitStageWithOutcomeAsync(
            stageId,
            CancellationToken.None,
            retainCutoverForPublication: true);
        Assert.Equal(PackageStoreCommitDisposition.Committed, commit.Disposition);
        Assert.Equal(PackageStoreTransactionPhase.CleanupPending, ReadJournal(fixture.Paths, stageId).Phase);
        await coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.GenerationCommitAcknowledged);
        await coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.OldGenerationStopped);
        await coordinator.PrepareStorageActivationAsync(stageId, ["test.package"]);
        await File.WriteAllTextAsync(statePath, "published");

        Assert.Empty(await coordinator.CompleteCutoverAsync(stageId));

        var completed = ReadJournal(fixture.Paths, stageId);
        Assert.Equal(PackageStoreCutoverPhase.Completed, completed.CutoverPhase);
        Assert.Equal(PackageStoreTransactionPhase.CleanupPending, completed.Phase);
        Assert.Empty(completed.StorageSnapshots);
        Assert.Equal("published", await File.ReadAllTextAsync(statePath));

        var laterArchive = CreatePackageArchive(fixture.Root, "test.later", "1.0.0");
        var later = await coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: laterArchive),
        ]);
        Assert.True(later.Success, string.Join(Environment.NewLine, later.Errors));

        await CreateCoordinator(fixture.Paths, fixture.Store).InitializeAsync();

        Assert.Equal("published", await File.ReadAllTextAsync(statePath));
        Assert.Equal(
            ["test.later", "test.package"],
            (await fixture.Store.ListAsync()).Select(static package => package.PackageId).Order().ToArray());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task PreparedStageRecovery_DoesNotOverwriteAConsistentNewerCatalog()
    {
        var fixture = await CreateFixtureAsync();
        var staleArchive = CreatePackageArchive(fixture.Root, "test.stale", "1.0.0");
        var committedArchive = CreatePackageArchive(fixture.Root, "test.committed", "1.0.0");
        var stale = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: staleArchive),
        ]);
        Assert.True(stale.Success, stale.Failure?.Message);

        var committed = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: committedArchive),
        ]);
        Assert.True(committed.Success, string.Join(Environment.NewLine, committed.Errors));

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();

        Assert.Equal("test.committed", Assert.Single(await fixture.Store.ListAsync()).PackageId);
        Assert.False(Directory.Exists(fixture.Paths.GetInstalledPackagePath("test.stale", "1.0.0")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task DurableStorageSnapshots_RestorePackageDataBeforeRecoveredActivation()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        var stageId = Assert.IsType<string>(preparation.Stage?.StageId);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.PreLaunchPolicyConfirmed);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(stageId, PackageStoreCutoverPhase.CandidateReady);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.PreCommitPolicyConfirmed);
        var commit = await fixture.Coordinator.CommitStageWithOutcomeAsync(
            stageId,
            CancellationToken.None,
            retainCutoverForPublication: true);
        Assert.Equal(PackageStoreCommitDisposition.Committed, commit.Disposition);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.GenerationCommitAcknowledged);
        await fixture.Coordinator.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.OldGenerationStopped);

        var packageDataRoot = Path.Combine(fixture.Paths.PackageDataRootPath, "test.package");
        var dataPath = Path.Combine(packageDataRoot, "data");
        var workspacePath = Path.Combine(packageDataRoot, "workspace");
        var filesPath = Path.Combine(packageDataRoot, "files");
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(filesPath);
        await File.WriteAllTextAsync(Path.Combine(dataPath, "state.json"), "before-state");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "workspace.txt"), "before-workspace");
        await File.WriteAllTextAsync(Path.Combine(filesPath, "package-file.txt"), "before-file");
        await fixture.Coordinator.PrepareStorageActivationAsync(stageId, ["test.package", "dev.overlay"]);
        var storageSnapshots = ReadJournal(fixture.Paths, stageId).StorageSnapshots;
        Assert.Equal(3, storageSnapshots.Count);
        Assert.All(storageSnapshots, snapshot => Assert.Equal("test.package", snapshot.PackageId));

        await File.WriteAllTextAsync(Path.Combine(dataPath, "state.json"), "after-state");
        await File.WriteAllTextAsync(Path.Combine(dataPath, "created.json"), "created");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "workspace.txt"), "after-workspace");
        await File.WriteAllTextAsync(Path.Combine(filesPath, "package-file.txt"), "after-file");

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();

        Assert.Equal("before-state", await File.ReadAllTextAsync(Path.Combine(dataPath, "state.json")));
        Assert.False(File.Exists(Path.Combine(dataPath, "created.json")));
        Assert.Equal(
            "before-workspace",
            await File.ReadAllTextAsync(Path.Combine(workspacePath, "workspace.txt")));
        Assert.Equal("before-file", await File.ReadAllTextAsync(Path.Combine(filesPath, "package-file.txt")));
        Assert.Empty(ReadJournal(fixture.Paths, stageId).StorageSnapshots);
        Assert.Empty(await recovery.CompleteRecoveredCutoversAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task Recovery_RejectsStorageSnapshotTargetNotBoundToDesiredPackageDirectory()
    {
        var fixture = await CreateFixtureAsync();
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);
        var stageId = Assert.IsType<string>(preparation.Stage?.StageId);
        var transactionDirectory = Path.Combine(fixture.Paths.TransactionRootPath, stageId);
        var journalPath = Path.Combine(transactionDirectory, "journal.json");
        var journal = ReadJournal(fixture.Paths, stageId) with
        {
            StorageSnapshots =
            [
                new PackageStoreStorageSnapshot(
                    "test.package",
                    PackageStoreStorageSnapshotKind.Data,
                    fixture.Paths.PackageDataRootPath,
                    Path.Combine(transactionDirectory, "storage", "0000"),
                    Existed: false),
            ],
        };
        Directory.CreateDirectory(fixture.Paths.PackageDataRootPath);
        var sentinel = Path.Combine(fixture.Paths.PackageDataRootPath, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCoordinator(fixture.Paths, fixture.Store).InitializeAsync());

        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public void StorageSnapshot_RejectsFifoWithoutOpeningIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateTempDirectory();
        var fifoPath = Path.Combine(root, "package.fifo");
        try
        {
            Assert.Equal(0, MkFifo(fifoPath, 0x180));
            var exception = Assert.Throws<InvalidDataException>(
                () => PackageStoreFileSystem.EnsureRegularFile(fifoPath));
            Assert.Contains("not a regular file", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fifoPath);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task GarbageCollectionAuthority_IncludesBothCatalogsFromPendingJournal()
    {
        var previousBinding = CreateRuntimeBinding();
        var desiredBinding = CreateRuntimeBinding(
            runtimeVersion: "10.0.2",
            packagingRevision: 2,
            manifestHash: 'c',
            artifactHash: 'd',
            runtimePolicyRevision: 2);
        var fixture = await CreateInstalledFixtureAsync(
            previousBinding,
            new OneShotFaultInjector(PackageStoreFaultPoint.JournalPrepared, simulatedCrash: true));
        var installed = Assert.Single(await fixture.Store.ListAsync());
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageExecutionRuntimeRebindMutation(
                installed.PackageId,
                installed,
                [desiredBinding]),
        ]);
        Assert.True(preparation.Success, preparation.Failure?.Message);

        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
            () => fixture.Coordinator.CommitStageAsync(preparation.Stage!.StageId));
        var authority = await fixture.Coordinator.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(
            CancellationToken.None);

        Assert.Contains(previousBinding, authority.PackageBindings);
        Assert.Contains(desiredBinding, authority.PackageBindings);
    }

    [Fact]
    public async Task GarbageCollectionAuthority_HoldsOperationGateAcrossSnapshotAndSweep()
    {
        var fixture = await CreateFixtureAsync();
        await using var operationGate = new RuntimeOperationGate();
        var authority = new PackageExecutionRuntimeGarbageCollectionAuthority(
            operationGate,
            fixture.Coordinator,
            new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(fixture.Paths)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = authority.ExecuteAsync(
            async (snapshot, cancellationToken) =>
            {
                Assert.Empty(snapshot.PackageBindings);
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return 1;
            },
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var concurrentOperation = operationGate.EnterAsync().AsTask();
        Assert.False(concurrentOperation.IsCompleted);
        release.SetResult();

        Assert.Equal(1, await collection.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var concurrentLease = await concurrentOperation.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GarbageCollectionAuthority_MissingJournalFailsClosedWithoutDeletingEvidence()
    {
        var fixture = await CreateFixtureAsync();
        var transactionPath = Path.Combine(
            fixture.Paths.TransactionRootPath,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionPath);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Coordinator.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(
                CancellationToken.None));

        Assert.True(Directory.Exists(transactionPath));
    }

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.JournalPrepared, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadsCommitting, false)]
    [InlineData((int)PackageStoreFaultPoint.PayloadsCommitted, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplacing, false)]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplaced, true)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitRecording, true)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitted, true)]
    [InlineData((int)PackageStoreFaultPoint.Cleanup, true)]
    public async Task RuntimeRebind_FaultRecoveryUsesCatalogReplacementAsCommitPoint(
        int faultPointValue,
        bool expectedRebound)
    {
        var faultPoint = (PackageStoreFaultPoint)faultPointValue;
        var initialBinding = CreateRuntimeBinding();
        var desiredBinding = CreateRuntimeBinding(
            runtimeVersion: "10.0.2",
            packagingRevision: 2,
            manifestHash: 'c',
            artifactHash: 'd',
            runtimePolicyRevision: 2);
        var fixture = await CreateInstalledFixtureAsync(
            initialBinding,
            new OneShotFaultInjector(faultPoint, simulatedCrash: true));
        var before = Assert.Single(await fixture.Store.ListAsync());
        var filesBefore = SnapshotPackageFiles(before.InstallPath);
        var preparation = await fixture.Coordinator.PrepareStageAsync([
            new PackageExecutionRuntimeRebindMutation(
                before.PackageId,
                before,
                [desiredBinding]),
        ]);
        Assert.True(preparation.Success, preparation.Failure?.Message);
        Assert.Empty(preparation.Stage!.PreparationSourcePaths);

        if (faultPoint == PackageStoreFaultPoint.Cleanup)
        {
            var result = await fixture.Coordinator.CommitStageAsync(preparation.Stage.StageId);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        }
        else
        {
            await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(
                () => fixture.Coordinator.CommitStageAsync(preparation.Stage.StageId));
        }

        var recovery = CreateCoordinator(fixture.Paths, fixture.Store);
        await recovery.InitializeAsync();

        var recovered = Assert.Single(await fixture.Store.ListAsync());
        AssertPackageFieldsExceptBindingsEqual(before, recovered);
        Assert.Equal(
            expectedRebound ? desiredBinding : initialBinding,
            Assert.Single(recovered.ExecutionRuntimeBindings));
        AssertPackageFilesEqual(filesBefore, SnapshotPackageFiles(recovered.InstallPath));
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

    [Theory]
    [InlineData((int)PackageStoreFaultPoint.CatalogReplaced)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitRecording)]
    [InlineData((int)PackageStoreFaultPoint.CatalogCommitted)]
    public async Task CatalogWriteReportedFailure_WhenDesiredCatalogIsAuthoritative_RollsForward(
        int faultPointValue)
    {
        var fixture = await CreateFixtureAsync(
            new OneShotFaultInjector((PackageStoreFaultPoint)faultPointValue, simulatedCrash: false));
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");

        var result = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("desired catalog became authoritative", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("test.package", Assert.Single(await fixture.Store.ListAsync()).PackageId);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task CatalogWriteFailure_WhenAuthoritativeCatalogCannotBeRead_IsIndeterminate()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var faultInjector = new CallbackFaultInjector(point =>
        {
            if (point != PackageStoreFaultPoint.CatalogReplacing)
            {
                return;
            }

            Directory.CreateDirectory(paths.CatalogRootPath);
            File.WriteAllText(paths.StateFilePath, "{invalid");
            throw new IOException("Injected ambiguous catalog write failure.");
        });
        var coordinator = CreateCoordinator(paths, store, faultInjector);
        await coordinator.InitializeAsync();
        var generation = coordinator.CatalogGeneration;
        var archive = CreatePackageArchive(root, "test.package", "1.0.0");
        var preparation = await coordinator.PrepareStageAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);

        var commit = await coordinator.CommitStageWithOutcomeAsync(preparation.Stage!.StageId);

        Assert.True(
            commit.Disposition == PackageStoreCommitDisposition.Indeterminate,
            $"Expected an indeterminate outcome, got {commit.Disposition}: {commit.OperationResult.Message}");
        Assert.False(commit.OperationResult.Success);
        Assert.Contains("could not be verified", commit.OperationResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startup recovery", commit.OperationResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(generation, coordinator.CatalogGeneration);
        Assert.NotEmpty(Directory.EnumerateDirectories(paths.TransactionRootPath));
    }

    [Fact]
    public async Task CatalogDurabilityUnconfirmed_RemainsIndeterminateUntilStartupRecovery()
    {
        var fixture = await CreateFixtureAsync(new CallbackFaultInjector(point =>
        {
            if (point == PackageStoreFaultPoint.CatalogReplaced)
            {
                throw new DurableWriteIndeterminateException(
                    "installed-packages.json",
                    new IOException("Injected directory synchronization failure."));
            }
        }));
        var archive = CreatePackageArchive(fixture.Root, "test.package", "1.0.0");

        var result = await fixture.Coordinator.ExecuteAsync([
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archive),
        ]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains("durability could not be confirmed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("test.package", Assert.Single(await fixture.Store.ListAsync()).PackageId);
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));

        await CreateCoordinator(fixture.Paths, fixture.Store).InitializeAsync();

        Assert.Equal("test.package", Assert.Single(await fixture.Store.ListAsync()).PackageId);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task Recovery_WhenJournalUsesSchema3_RejectsBeforeMutationAndPreservesEvidence()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDirectory = Path.Combine(paths.TransactionRootPath, transactionId);
        Directory.CreateDirectory(transactionDirectory);
        var journalPath = Path.Combine(transactionDirectory, "journal.json");
        var journalJson = $$"""
            {
              "SchemaVersion": 3,
              "TransactionId": "{{transactionId}}",
              "CutoverPhase": 0,
              "RequiresRuntimePublication": false,
              "Phase": 0,
              "PreviousPackages": [],
              "DesiredPackages": [],
              "Actions": [],
              "StorageSnapshots": []
            }
            """;
        await File.WriteAllTextAsync(journalPath, journalJson);
        var transactionEvidencePath = Path.Combine(transactionDirectory, "evidence.txt");
        await File.WriteAllTextAsync(transactionEvidencePath, "preserve-transaction");
        var stagingEvidencePath = Path.Combine(paths.StagingRootPath, "evidence", "staged.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(stagingEvidencePath)!);
        await File.WriteAllTextAsync(stagingEvidencePath, "preserve-staging");
        Assert.False(Directory.Exists(paths.InstalledRootPath));
        Assert.False(Directory.Exists(paths.TombstoneRootPath));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCoordinator(paths, new InstalledPackageStore(paths)).InitializeAsync());

        Assert.Contains("unsupported pre-release schema version 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("No migration or reinterpretation is supported", failure.Message, StringComparison.Ordinal);
        Assert.Contains("sunder runtime reset --yes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("data-safe reset preserves package data", failure.Message, StringComparison.Ordinal);
        Assert.Equal(journalJson, await File.ReadAllTextAsync(journalPath));
        Assert.Equal("preserve-transaction", await File.ReadAllTextAsync(transactionEvidencePath));
        Assert.Equal("preserve-staging", await File.ReadAllTextAsync(stagingEvidencePath));
        Assert.False(Directory.Exists(paths.InstalledRootPath));
        Assert.False(Directory.Exists(paths.TombstoneRootPath));
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
            PackageStoreTransactionManager.JournalSchemaVersion,
            transactionId,
            PackageStoreCutoverPhase.DurableCommitStarted,
            RequiresRuntimePublication: false,
            PackageStoreTransactionPhase.PayloadsCommitting,
            [],
            [],
            [new PackageStoreJournalAction(
                PackageStoreJournalActionKind.Remove,
                null,
                outsidePath,
                null,
                Path.Combine(paths.TombstoneRootPath, "unsafe"))],
            StorageSnapshots: [],
            RuntimePolicyUpdates: []);
        await DurableJsonDocument.WriteAsync(
            Path.Combine(transactionDirectory, "journal.json"),
            journal,
            InstalledPackageStore.JsonOptions);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCoordinator(paths, store).InitializeAsync());

        Assert.True(Directory.Exists(outsidePath));
    }

    [Fact]
    public async Task Recovery_WhenUnjournaledActivationBackupsExist_FailsClosedAndPreservesEvidence()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var packageData = Path.Combine(paths.PackageDataRootPath, "test.package");
        var workspaceBackup = Path.Combine(packageData, ".workspace.sunder-activation-" + Guid.NewGuid().ToString("N"));
        var filesBackup = Path.Combine(packageData, ".files.sunder-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceBackup);
        Directory.CreateDirectory(filesBackup);
        await File.WriteAllTextAsync(Path.Combine(workspaceBackup, "workspace.txt"), "preserve-workspace");
        await File.WriteAllTextAsync(Path.Combine(filesBackup, "files.txt"), "preserve-files");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCoordinator(paths, new InstalledPackageStore(paths)).InitializeAsync());

        Assert.Contains("unjournaled storage activation backup", failure.Message, StringComparison.Ordinal);
        Assert.Equal("preserve-workspace", await File.ReadAllTextAsync(Path.Combine(workspaceBackup, "workspace.txt")));
        Assert.Equal("preserve-files", await File.ReadAllTextAsync(Path.Combine(filesBackup, "files.txt")));
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
              "SchemaVersion": 4,
              "TransactionId": "{{transactionId}}",
              "CutoverPhase": 0,
              "RequiresRuntimePublication": false,
              "Phase": 999,
              "PreviousPackages": [],
              "DesiredPackages": [],
              "Actions": [],
              "StorageSnapshots": [],
              "RuntimePolicyUpdates": []
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
        Directory.Delete(transactionPath, recursive: true);
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
        await Assert.ThrowsAsync<RuntimeOperationRejectedException>(async () => await gate.EnterAsync());
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
        await Assert.ThrowsAsync<RuntimeOperationRejectedException>(async () => await gate.EnterAsync());
    }

    [Fact]
    public async Task OperationGate_ShutdownIsBoundedWhenActiveOperationIgnoresCancellation()
    {
        var gate = new RuntimeOperationGate(
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(50),
            });
        var operation = await gate.EnterAsync();
        var cleanupCalled = false;

        var shutdown = gate.ShutdownAsync(() =>
        {
            cleanupCalled = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<TimeoutException>(
            () => shutdown.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(cleanupCalled);

        await operation.DisposeAsync();
        await gate.DisposeAsync();
    }

    [Fact]
    public async Task OperationGate_ShutdownIsBoundedWhenCleanupIgnoresCancellation()
    {
        var gate = new RuntimeOperationGate(
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(50),
            });
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var shutdown = gate.ShutdownAsync(async _ =>
        {
            cleanupStarted.TrySetResult();
            await releaseCleanup.Task;
        });

        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(
            () => shutdown.WaitAsync(TimeSpan.FromSeconds(2)));

        releaseCleanup.TrySetResult();
        await gate.DisposeAsync();
    }

    [Fact]
    public async Task OperationGate_CanceledShutdownDeadlineDoesNotRestartBudgetDuringDispose()
    {
        var gate = new RuntimeOperationGate(
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            });
        var operation = await gate.EnterAsync();
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.ShutdownAsync(static _ => Task.CompletedTask, deadline.Token));
        await gate.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(200));

        await operation.DisposeAsync();
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

    private static async Task<Fixture> CreateFixtureAsync(IPackageStoreFaultInjector? faultInjector = null)
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        var store = new InstalledPackageStore(paths);
        var coordinator = CreateCoordinator(paths, store, faultInjector);
        await coordinator.InitializeAsync();
        return new Fixture(root, paths, store, coordinator);
    }

    private static async Task<Fixture> CreateInstalledFixtureAsync(
        InstalledPackageExecutionRuntimeBindingRecord binding,
        IPackageStoreFaultInjector? faultInjector = null)
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(Path.Combine(root, "store"));
        const string packageId = "test.bound";
        const string version = "1.0.0";
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            installPath,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        var package = CanonicalPackageTestBuilder.CreateInstalledRecord(
            installPath,
            packageId,
            version,
            isEnabled: false,
            executionRuntimeBindings: [binding]) with
        {
            InstalledAtUtc = DateTimeOffset.Parse("2026-07-11T09:00:00Z"),
            Provenance = InstalledPackageProvenanceRecord.LocalArchive(new string('f', 64)),
        };
        var store = new InstalledPackageStore(paths);
        await store.WriteAsync([package]);
        var coordinator = CreateCoordinator(paths, store, faultInjector);
        await coordinator.InitializeAsync();
        return new Fixture(root, paths, store, coordinator);
    }

    private static PackageStoreCoordinator CreateCoordinator(
        RuntimePackagePaths paths,
        InstalledPackageStore store,
        IPackageStoreFaultInjector? faultInjector = null,
        ExecutionRuntimeLifecyclePolicyCache? lifecyclePolicies = null)
    {
        var installer = new SunderPackageArchiveInstaller(paths);
        return new PackageStoreCoordinator(paths, store, installer, faultInjector, lifecyclePolicies);
    }

    private static PackageStoreTransactionJournal ReadJournal(RuntimePackagePaths paths, string transactionId)
        => JsonSerializer.Deserialize<PackageStoreTransactionJournal>(
               File.ReadAllText(Path.Combine(paths.TransactionRootPath, transactionId, "journal.json")),
               InstalledPackageStore.JsonOptions)
           ?? throw new InvalidDataException("The package transaction journal is invalid.");

    private static string CreatePackageArchive(
        string root,
        string packageId,
        string version,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null,
        string payloadContent = "test")
    {
        var sourceRoot = Path.Combine(root, "package-source-" + Guid.NewGuid().ToString("N"));
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            sourceRoot,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            dependencies: dependencies);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "shared", "assets"));
        File.WriteAllText(Path.Combine(sourceRoot, "payload", "shared", "assets", "test-content.txt"), payloadContent);
        CanonicalPackageTestBuilder.WriteContentIndex(sourceRoot);
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
            "shared",
            "assets",
            "test-content.txt"));

    private static InstalledPackageExecutionRuntimeBindingRecord CreateRuntimeBinding(
        string runtimeVersion = "10.0.1",
        int packagingRevision = 1,
        char manifestHash = 'a',
        char artifactHash = 'b',
        long runtimePolicyRevision = 1)
        => new(
            SunderPackageFormat.RuntimeHostRole,
            PackageTargetSelection.GetCurrentRuntimeIdentifier(),
            SunderExecutionRuntimeFormat.DotNetRuntimeId,
            runtimeVersion,
            packagingRevision,
            SunderExecutionRuntimeFormat.DotNetModuleAbi,
            new string(manifestHash, 64),
            new string(artifactHash, 64),
            4096 + packagingRevision,
            "https://registry.example/",
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "sunder-runtime-release-2026-01",
            packagingRevision,
            runtimePolicyRevision,
            "supported",
            DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddMinutes(runtimePolicyRevision),
            DateTimeOffset.Parse("2026-07-11T10:01:00Z").AddMinutes(runtimePolicyRevision));

    private static ExecutionRuntimeLifecyclePolicyEntryV1 CreateRuntimePolicyEntry(
        InstalledPackageExecutionRuntimeBindingRecord binding,
        RegistryExecutionRuntimeLifecycle lifecycle,
        string trustDisposition = ExecutionRuntimeTrustDispositions.Trusted)
        => new(
            new ExecutionRuntimeIdentity(
                binding.RuntimeId,
                binding.RuntimeVersion,
                binding.RuntimePackagingRevision,
                binding.Rid,
                binding.ModuleAbi),
            binding.RegistryOrigin,
            SunderExecutionRuntimeFormat.WorkerProtocol,
            binding.RuntimeManifestSha256,
            binding.RuntimeArtifactSha256,
            binding.RuntimeArtifactSize,
            new string('e', 64),
            binding.SigningKeyId,
            binding.TrustStoreGeneration,
            new string('f', 64),
            "sunder-native-test-2026-01",
            lifecycle,
            binding.RuntimePolicyRevision,
            binding.PolicyObservedAtUtc,
            trustDisposition);

    private static RegistryExecutionRuntimeLifecycle RuntimeLifecycle(string state)
    {
        var staged = DateTimeOffset.Parse("2026-07-10T10:00:00Z");
        var supported = staged.AddHours(1);
        return new RegistryExecutionRuntimeLifecycle(
            state,
            staged,
            supported,
            state == ExecutionRuntimeLifecycleStates.Supported ? null : supported.AddHours(1),
            InstallBlockedAtUtc: null,
            ActivationBlockedAtUtc: null,
            RevokedAtUtc: null,
            Message: null);
    }

    private static IReadOnlyDictionary<string, string> SnapshotPackageFiles(string packagePath)
        => Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(packagePath, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);

    private static void AssertPackageFilesEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, hash) in expected)
        {
            Assert.Equal(hash, actual[path]);
        }
    }

    private static void AssertPackageFieldsExceptBindingsEqual(
        InstalledPackageRecord expected,
        InstalledPackageRecord actual)
    {
        Assert.Equal(expected.PackageId, actual.PackageId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Icon, actual.Icon);
        Assert.Equal(expected.InstallPath, actual.InstallPath);
        Assert.Equal(expected.ManifestPath, actual.ManifestPath);
        Assert.Equal(expected.ContentIdentity, actual.ContentIdentity);
        Assert.Equal(expected.ContentInventory, actual.ContentInventory);
        Assert.Equal(expected.DependsOn, actual.DependsOn);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.InstalledAtUtc, actual.InstalledAtUtc);
        Assert.Equal(expected.Provenance, actual.Provenance);
        Assert.Equal(expected.CapabilityGrants, actual.CapabilityGrants);
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

    private sealed class RecordingFaultInjector : IPackageStoreFaultInjector
    {
        public List<PackageStoreFaultPoint> Points { get; } = [];

        public void Hit(PackageStoreFaultPoint point) => Points.Add(point);
    }

    private sealed class CallbackFaultInjector(Action<PackageStoreFaultPoint> callback)
        : IPackageStoreFaultInjector
    {
        public void Hit(PackageStoreFaultPoint point) => callback(point);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);
}
