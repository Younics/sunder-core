using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreTransactionManager(
    RuntimePackagePaths paths,
    InstalledPackageStore store,
    IPackageStoreFaultInjector? faultInjector,
    ExecutionRuntimeLifecyclePolicyCache? lifecyclePolicies,
    PackageLifecyclePolicyCache? packagePolicies = null)
{
    internal const int JournalSchemaVersion = 4;
    private readonly PackageStoreJournalFileSystem _fileSystem = new(paths, faultInjector);

    internal async Task PrepareAsync(
        PendingStoreStage stage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transactionDirectory = GetTransactionDirectory(stage.StageId);
        var journalPath = GetJournalPath(transactionDirectory);
        var journal = CreateJournal(stage, transactionDirectory);
        ValidateCommitPaths(journal, transactionDirectory, journalPath);
        DurableJsonDocument.CreateDirectory(transactionDirectory);
        PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);
        if (File.Exists(journalPath))
        {
            throw new InvalidDataException(
                $"Package store transaction '{stage.StageId}' already has durable evidence.");
        }

        await WriteJournalAsync(journalPath, journal).ConfigureAwait(false);
        Hit(PackageStoreFaultPoint.CutoverPrepared);
    }

    internal async Task AdvanceCutoverPhaseAsync(
        string transactionId,
        PackageStoreCutoverPhase phase)
    {
        var transactionDirectory = GetTransactionDirectory(transactionId);
        var journalPath = GetJournalPath(transactionDirectory);
        ValidateTransactionPaths(transactionDirectory, journalPath);
        var journal = await ReadJournalAsync(journalPath).ConfigureAwait(false);
        ValidateJournalIdentity(journal, transactionId, transactionDirectory);
        if (journal.CutoverPhase >= phase)
        {
            return;
        }
        if ((int)phase != (int)journal.CutoverPhase + 1)
        {
            throw new InvalidOperationException(
                $"Package store transaction '{transactionId}' cannot advance from cutover phase '{journal.CutoverPhase}' to '{phase}'.");
        }

        await WriteJournalAsync(journalPath, journal with { CutoverPhase = phase })
            .ConfigureAwait(false);
    }

    internal async Task MarkRuntimePublicationRequiredAsync(string transactionId)
    {
        var transactionDirectory = GetTransactionDirectory(transactionId);
        var journalPath = GetJournalPath(transactionDirectory);
        ValidateTransactionPaths(transactionDirectory, journalPath);
        var journal = await ReadJournalAsync(journalPath).ConfigureAwait(false);
        ValidateJournalIdentity(journal, transactionId, transactionDirectory);
        if (journal.CutoverPhase != PackageStoreCutoverPhase.PreCommitPolicyConfirmed
            || journal.Phase != PackageStoreTransactionPhase.Prepared)
        {
            throw new InvalidOperationException(
                $"Package store transaction '{transactionId}' cannot record Runtime publication at cutover phase '{journal.CutoverPhase}'.");
        }
        if (!journal.RequiresRuntimePublication)
        {
            await WriteJournalAsync(journalPath, journal with { RequiresRuntimePublication = true })
                .ConfigureAwait(false);
        }
    }

    internal async Task PrepareStorageActivationAsync(
        string transactionId,
        IReadOnlyList<string> packageIds)
    {
        var transactionDirectory = GetTransactionDirectory(transactionId);
        var journalPath = GetJournalPath(transactionDirectory);
        ValidateTransactionPaths(transactionDirectory, journalPath);
        var journal = await ReadJournalAsync(journalPath).ConfigureAwait(false);
        ValidateJournalIdentity(journal, transactionId, transactionDirectory);
        if (!journal.RequiresRuntimePublication
            || journal.CutoverPhase < PackageStoreCutoverPhase.OldGenerationStopped
            || journal.CutoverPhase >= PackageStoreCutoverPhase.Completed)
        {
            throw new InvalidOperationException(
                $"Package store transaction '{transactionId}' cannot prepare package storage at cutover phase '{journal.CutoverPhase}'.");
        }
        if (journal.StorageSnapshots.Count > 0)
        {
            journal = await RestoreStorageActivationAsync(journal, journalPath).ConfigureAwait(false);
        }

        var storageRoot = Path.Combine(transactionDirectory, "storage");
        PackageStorePathGuard.EnsureSafe(storageRoot, transactionDirectory);
        if (Directory.Exists(storageRoot))
        {
            Directory.Delete(storageRoot, recursive: true);
            DurableJsonDocument.SyncDirectory(transactionDirectory);
        }
        Directory.CreateDirectory(storageRoot);
        var snapshots = new List<PackageStoreStorageSnapshot>();
        var index = 0;
        var desiredPackageIds = journal.DesiredPackages
            .Select(static package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in packageIds.Where(desiredPackageIds.Contains)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var packageRoot = LocalPackageStorageContext.GetPackageRootPath(
                packageId,
                paths.PackageDataRootPath);
            foreach (var (kind, directoryName) in new[]
                     {
                         (PackageStoreStorageSnapshotKind.Data, "data"),
                         (PackageStoreStorageSnapshotKind.Workspace, "workspace"),
                         (PackageStoreStorageSnapshotKind.Files, "files"),
                     })
            {
                var targetPath = Path.Combine(packageRoot, directoryName);
                PackageStorePathGuard.EnsureSafe(targetPath, paths.PackageDataRootPath);
                var snapshotPath = Path.Combine(storageRoot, (index++).ToString("D4"));
                PackageStorePathGuard.EnsureSafe(snapshotPath, storageRoot);
                var existed = Directory.Exists(targetPath);
                if (existed)
                {
                    PackageStoreFileSystem.CopyDirectoryDurable(targetPath, snapshotPath);
                }
                snapshots.Add(new PackageStoreStorageSnapshot(
                    packageId,
                    kind,
                    targetPath,
                    snapshotPath,
                    existed));
            }
        }

        journal = journal with { StorageSnapshots = Array.AsReadOnly(snapshots.ToArray()) };
        await WriteJournalAsync(journalPath, journal).ConfigureAwait(false);
    }

    internal async Task<PackageStoreTransactionJournal> RestoreStorageActivationAsync(
        PackageStoreTransactionJournal journal,
        string journalPath)
    {
        if (journal.StorageSnapshots.Count == 0)
        {
            return journal;
        }
        ValidateStorageSnapshots(journal, Path.GetDirectoryName(journalPath)!);
        foreach (var snapshot in journal.StorageSnapshots.Reverse())
        {
            PackageStoreFileSystem.RestoreDirectorySnapshot(snapshot);
        }

        journal = journal with { StorageSnapshots = [] };
        await WriteJournalAsync(journalPath, journal).ConfigureAwait(false);
        var storageRoot = Path.Combine(Path.GetDirectoryName(journalPath)!, "storage");
        if (Directory.Exists(storageRoot))
        {
            Directory.Delete(storageRoot, recursive: true);
            DurableJsonDocument.SyncDirectory(Path.GetDirectoryName(journalPath)!);
        }
        return journal;
    }

    internal async Task<PackageStoreCommitResult> CommitAsync(
        PendingStoreStage stage,
        CancellationToken cancellationToken,
        Action? durableCommitStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transactionDirectory = GetTransactionDirectory(stage.StageId);
        var journalPath = GetJournalPath(transactionDirectory);
        ValidateTransactionPaths(transactionDirectory, journalPath);
        var journal = await ReadJournalAsync(journalPath).ConfigureAwait(false);
        string? catalogCommitWarning = null;
        var runtimePolicyReconciled = false;
        try
        {
            ValidateCommitPaths(journal, transactionDirectory, journalPath);
            ValidateCommitJournal(stage, journal, transactionDirectory);
        }
        catch (InvalidDataException exception)
        {
            PackageStoreStageManager.Cleanup(stage.PreparedArchives);
            return new PackageStoreCommitResult(
                PackageOperationResults.Failure(
                    $"Failed to commit package store transaction '{stage.StageId}': {exception.Message}"),
                PackageStoreCommitDisposition.RolledBack);
        }

        PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);

        try
        {
            Hit(PackageStoreFaultPoint.JournalPrepared);

            journal = journal with { CutoverPhase = PackageStoreCutoverPhase.DurableCommitStarted };
            await WriteJournalAsync(journalPath, journal);
            durableCommitStarted?.Invoke();
            Hit(PackageStoreFaultPoint.DurableCommitStarted);

            journal = journal with { Phase = PackageStoreTransactionPhase.PayloadsCommitting };
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.PayloadsCommitting);
            journal = await _fileSystem.CommitPayloadsAsync(journal, journalPath);

            journal = journal with { Phase = PackageStoreTransactionPhase.PayloadsCommitted };
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.PayloadsCommitted);

            journal = journal with { Phase = PackageStoreTransactionPhase.CatalogCommitting };
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.CatalogReplacing);
            await store.WriteAsync(stage.DesiredPackages, CancellationToken.None);
            Hit(PackageStoreFaultPoint.CatalogReplaced);
            await ReconcileRuntimePolicyAsync(journal, hitFaultPoints: true).ConfigureAwait(false);
            runtimePolicyReconciled = true;
            var committedJournal = journal with
            {
                CutoverPhase = PackageStoreCutoverPhase.DurableCommitted,
                Phase = PackageStoreTransactionPhase.CatalogCommitted,
            };
            Hit(PackageStoreFaultPoint.CatalogCommitRecording);
            await WriteJournalAsync(journalPath, committedJournal);
            journal = committedJournal;
            Hit(PackageStoreFaultPoint.CatalogCommitted);
        }
        catch (PackageStoreSimulatedCrashException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (exception is DurableWriteIndeterminateException)
            {
                return Indeterminate(
                    stage,
                    $"A package transaction write became visible, but its durability could not be confirmed: {exception.Message}");
            }
            if (journal.Phase is PackageStoreTransactionPhase.CatalogCommitting
                    or PackageStoreTransactionPhase.CatalogCommitted
                || journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted)
            {
                var durableCommitRecorded = journal.Phase >= PackageStoreTransactionPhase.CatalogCommitted
                                            || journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted;
                IReadOnlyList<InstalledPackageRecord> authoritativeCatalog;
                try
                {
                    authoritativeCatalog = await store.ListAsync(CancellationToken.None);
                }
                catch (Exception readException)
                {
                    return Indeterminate(
                        stage,
                        $"The authoritative catalog could not be verified after an ambiguous durable write: {readException.Message}");
                }

                if (PackageStorePolicy.CatalogsEqual(authoritativeCatalog, journal.DesiredPackages))
                {
                    if (!runtimePolicyReconciled)
                    {
                        try
                        {
                            await ReconcileRuntimePolicyAsync(journal, hitFaultPoints: false)
                                .ConfigureAwait(false);
                            runtimePolicyReconciled = true;
                        }
                        catch (Exception policyException)
                        {
                            return Indeterminate(
                                stage,
                                $"The desired catalog became authoritative, but execution-runtime policy reconciliation failed: {policyException.Message}");
                        }
                    }
                    catalogCommitWarning =
                        $"The catalog write reported an error after the desired catalog became authoritative: {exception.Message}";
                    journal = journal with
                    {
                        CutoverPhase = PackageStoreCutoverPhase.DurableCommitted,
                        Phase = PackageStoreTransactionPhase.CatalogCommitted,
                    };
                    await WriteJournalAsync(journalPath, journal);
                }
                else if (!durableCommitRecorded
                         && PackageStorePolicy.CatalogsEqual(authoritativeCatalog, journal.PreviousPackages))
                {
                    return await RollBackOrReturnIndeterminateAsync(
                        stage,
                        journal,
                        journalPath,
                        exception).ConfigureAwait(false);
                }
                else
                {
                    return Indeterminate(
                        stage,
                        "The authoritative catalog is indeterminate after an ambiguous durable write.");
                }
            }
            else
            {
                return await RollBackOrReturnIndeterminateAsync(
                    stage,
                    journal,
                    journalPath,
                    exception).ConfigureAwait(false);
            }
        }

        try
        {
            Hit(PackageStoreFaultPoint.Cleanup);
            journal = await CleanupCommittedAsync(journal, journalPath);
            journal = journal with { Phase = PackageStoreTransactionPhase.Completed };
            await WriteJournalAsync(journalPath, journal);
            var result = catalogCommitWarning is null
                ? stage.Result
                : stage.Result with
                {
                    Warnings = stage.Result.Warnings.Append(catalogCommitWarning).ToArray(),
                };
            return new PackageStoreCommitResult(result, PackageStoreCommitDisposition.Committed);
        }
        catch (Exception exception)
        {
            journal = journal with { Phase = PackageStoreTransactionPhase.CleanupPending };
            await WriteJournalAsync(journalPath, journal);
            var warnings = catalogCommitWarning is null
                ? stage.Result.Warnings
                : stage.Result.Warnings.Append(catalogCommitWarning).ToArray();
            return new PackageStoreCommitResult(
                stage.Result with
                {
                    Warnings = warnings.Concat([
                        $"Package files were committed, but garbage collection will retry after cleanup failed: {exception.Message}",
                    ]).ToArray(),
                },
                PackageStoreCommitDisposition.Committed);
        }
    }

    internal async Task<IReadOnlyList<string>> CompleteCutoverAsync(string transactionId)
    {
        var transactionDirectory = GetTransactionDirectory(transactionId);
        var journalPath = GetJournalPath(transactionDirectory);
        ValidateTransactionPaths(transactionDirectory, journalPath);
        if (!File.Exists(journalPath))
        {
            return [];
        }

        var journal = await ReadJournalAsync(journalPath).ConfigureAwait(false);
        ValidateJournalIdentity(journal, transactionId, transactionDirectory);
        if (journal.CutoverPhase == PackageStoreCutoverPhase.Completed)
        {
            PackageStoreFileSystem.TryDeleteDirectory(transactionDirectory);
            return [];
        }
        if (journal.CutoverPhase != PackageStoreCutoverPhase.OldGenerationStopped)
        {
            throw new InvalidOperationException(
                $"Package store transaction '{transactionId}' cannot complete from cutover phase '{journal.CutoverPhase}'.");
        }

        journal = journal with
        {
            CutoverPhase = PackageStoreCutoverPhase.Completed,
            StorageSnapshots = [],
        };
        await WriteJournalAsync(journalPath, journal).ConfigureAwait(false);
        if (journal.Phase == PackageStoreTransactionPhase.Completed)
        {
            PackageStoreFileSystem.TryDeleteDirectory(transactionDirectory);
        }
        return [];
    }

    internal async Task DiscardPreparedAsync(string transactionId)
    {
        var transactionDirectory = GetTransactionDirectory(transactionId);
        var journalPath = GetJournalPath(transactionDirectory);
        ValidateTransactionPaths(transactionDirectory, journalPath);
        if (!File.Exists(journalPath))
        {
            PackageStoreFileSystem.TryDeleteDirectory(transactionDirectory);
            return;
        }

        var journal = await ReadJournalAsync(journalPath).ConfigureAwait(false);
        ValidateJournalIdentity(journal, transactionId, transactionDirectory);
        if (journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitStarted)
        {
            throw new InvalidOperationException(
                $"Package store transaction '{transactionId}' cannot be discarded after durable commit started.");
        }
        PackageStoreFileSystem.TryDeleteDirectory(transactionDirectory);
    }

    private async Task<PackageStoreCommitResult> RollBackOrReturnIndeterminateAsync(
        PendingStoreStage stage,
        PackageStoreTransactionJournal journal,
        string journalPath,
        Exception commitException)
    {
        try
        {
            await RollBackAsync(journal, journalPath).ConfigureAwait(false);
            return new PackageStoreCommitResult(
                PackageOperationResults.Failure(
                    $"Failed to commit package store transaction '{stage.StageId}': {commitException.Message}"),
                PackageStoreCommitDisposition.RolledBack);
        }
        catch (Exception rollbackException)
        {
            return Indeterminate(
                stage,
                $"Rollback could not be verified after '{commitException.Message}': {rollbackException.Message}");
        }
    }

    private static PackageStoreCommitResult Indeterminate(
        PendingStoreStage stage,
        string detail)
    {
        var message =
            $"Failed to commit package store transaction '{stage.StageId}'; {detail} Transaction evidence was preserved for startup recovery.";
        return new PackageStoreCommitResult(
            PackageOperationResults.Failure(message),
            PackageStoreCommitDisposition.Indeterminate);
    }

    internal async Task RollBackAsync(PackageStoreTransactionJournal journal, string journalPath)
    {
        journal = await _fileSystem.RollBackAsync(journal, journalPath);
        await store.WriteAsync(journal.PreviousPackages, CancellationToken.None);
        journal = journal with { Phase = PackageStoreTransactionPhase.RolledBack };
        await WriteJournalAsync(journalPath, journal);
        PackageStoreFileSystem.TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
    }

    internal async Task<PackageStoreTransactionJournal> CleanupCommittedAsync(
        PackageStoreTransactionJournal journal,
        string journalPath) =>
        await _fileSystem.CleanupCommittedAsync(journal, journalPath);

    internal Task ReconcileRuntimePolicyAsync(PackageStoreTransactionJournal journal)
        => ReconcileRuntimePolicyAsync(journal, hitFaultPoints: false);

    internal Task RestorePackagePolicyEvidenceAsync(PackageStoreTransactionJournal journal)
        => packagePolicies?.RestoreRetainedAsync(journal.PreviousPackages.Concat(journal.DesiredPackages)
            .SelectMany(package => package.Policy?.Snapshots ?? []), CancellationToken.None) ?? Task.CompletedTask;

    internal static Task WriteJournalAsync(string journalPath, PackageStoreTransactionJournal journal) =>
        DurableJsonDocument.WriteAsync(
            journalPath,
            journal,
            InstalledPackageStore.JsonOptions,
            CancellationToken.None);

    private static async Task<PackageStoreTransactionJournal> ReadJournalAsync(string journalPath)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageStoreTransactionJournal>(
                       await File.ReadAllTextAsync(journalPath, CancellationToken.None).ConfigureAwait(false),
                       InstalledPackageStore.JsonOptions)
                   ?? throw new InvalidDataException(
                       $"Package transaction journal '{journalPath}' is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Package transaction journal '{journalPath}' is invalid.",
                exception);
        }
    }

    private PackageStoreTransactionJournal CreateJournal(PendingStoreStage stage, string transactionDirectory)
    {
        var desiredById = stage.DesiredPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var actions = new List<PackageStoreJournalAction>();
        var index = 0;
        foreach (var archive in stage.PreparedArchives)
        {
            actions.Add(new PackageStoreJournalAction(
                PackageStoreJournalActionKind.AddOrReplace,
                archive.StagingPath,
                archive.InstalledPath,
                Path.Combine(transactionDirectory, "backups", (index++).ToString("D4")),
                null));
        }

        var replacements = stage.PreparedArchives.Select(archive => archive.InstalledPath).ToHashSet(PathComparer);
        foreach (var previousPackage in stage.PreviousPackages)
        {
            if (desiredById.TryGetValue(previousPackage.PackageId, out var desiredPackage)
                && string.Equals(previousPackage.InstallPath, desiredPackage.InstallPath, PathComparison)
                || replacements.Contains(previousPackage.InstallPath))
            {
                continue;
            }

            actions.Add(new PackageStoreJournalAction(
                PackageStoreJournalActionKind.Remove,
                null,
                previousPackage.InstallPath,
                null,
                Path.Combine(paths.TombstoneRootPath, $"{stage.StageId}-{index++:D4}")));
        }

        return new PackageStoreTransactionJournal(
            JournalSchemaVersion,
            stage.StageId,
            PackageStoreCutoverPhase.Prepared,
            RequiresRuntimePublication: false,
            PackageStoreTransactionPhase.Prepared,
            stage.PreviousPackages,
            stage.DesiredPackages,
            actions,
            StorageSnapshots: [],
            stage.RuntimePolicyUpdates);
    }

    private void ValidateCommitJournal(
        PendingStoreStage stage,
        PackageStoreTransactionJournal journal,
        string transactionDirectory)
    {
        ValidateJournalIdentity(journal, stage.StageId, transactionDirectory);
        var expected = CreateJournal(stage, transactionDirectory);
        if (journal.CutoverPhase != PackageStoreCutoverPhase.PreCommitPolicyConfirmed
            || journal.Phase != PackageStoreTransactionPhase.Prepared
            || !PackageStorePolicy.CatalogsEqual(journal.PreviousPackages, expected.PreviousPackages)
            || !PackageStorePolicy.CatalogsEqual(journal.DesiredPackages, expected.DesiredPackages)
            || !journal.Actions.SequenceEqual(expected.Actions)
            || !journal.RuntimePolicyUpdates.SequenceEqual(expected.RuntimePolicyUpdates))
        {
            throw new InvalidDataException(
                $"Package store transaction '{stage.StageId}' does not match its prepared durable evidence.");
        }
    }

    private void ValidateStorageSnapshots(
        PackageStoreTransactionJournal journal,
        string transactionDirectory)
    {
        var storageRoot = Path.Combine(transactionDirectory, "storage");
        var desiredPackageIds = journal.DesiredPackages
            .Select(static package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(PathComparer);
        foreach (var snapshot in journal.StorageSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.PackageId)
                || !desiredPackageIds.Contains(snapshot.PackageId)
                || !Enum.IsDefined(snapshot.Kind)
                || !targets.Add(snapshot.TargetPath))
            {
                throw new InvalidDataException(
                    $"Package store transaction '{journal.TransactionId}' has invalid package storage evidence.");
            }
            var packageRoot = LocalPackageStorageContext.GetPackageRootPath(
                snapshot.PackageId,
                paths.PackageDataRootPath);
            var directoryName = snapshot.Kind switch
            {
                PackageStoreStorageSnapshotKind.Data => "data",
                PackageStoreStorageSnapshotKind.Workspace => "workspace",
                PackageStoreStorageSnapshotKind.Files => "files",
                _ => throw new InvalidDataException(
                    $"Package store transaction '{journal.TransactionId}' has invalid package storage evidence."),
            };
            if (!string.Equals(
                    snapshot.TargetPath,
                    Path.Combine(packageRoot, directoryName),
                    PathComparison)
                || !string.Equals(Path.GetDirectoryName(snapshot.SnapshotPath), storageRoot, PathComparison)
                || snapshot.Existed && !Directory.Exists(snapshot.SnapshotPath))
            {
                throw new InvalidDataException(
                    $"Package store transaction '{journal.TransactionId}' has invalid package storage evidence.");
            }
            PackageStorePathGuard.EnsureSafe(snapshot.TargetPath, paths.PackageDataRootPath);
            PackageStorePathGuard.EnsureSafe(snapshot.SnapshotPath, storageRoot);
        }
    }

    private void ValidateJournalIdentity(
        PackageStoreTransactionJournal journal,
        string transactionId,
        string transactionDirectory)
    {
        if (journal.SchemaVersion != JournalSchemaVersion
            || !string.Equals(journal.TransactionId, transactionId, StringComparison.Ordinal)
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.CutoverPhase)
            || !Enum.IsDefined(journal.Phase)
            || journal.PreviousPackages is null
            || journal.DesiredPackages is null
            || journal.Actions is null
            || journal.StorageSnapshots is null
            || journal.RuntimePolicyUpdates is null
            || !string.Equals(
                transactionDirectory,
                Path.Combine(paths.TransactionRootPath, transactionId),
                PathComparison))
        {
            throw new InvalidDataException(
                $"Package transaction journal in '{transactionDirectory}' is invalid.");
        }
    }

    private string GetTransactionDirectory(string transactionId) =>
        Path.Combine(paths.TransactionRootPath, transactionId);

    private static string GetJournalPath(string transactionDirectory) =>
        Path.Combine(transactionDirectory, "journal.json");

    private void ValidateTransactionPaths(string transactionDirectory, string journalPath)
    {
        PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);
        PackageStorePathGuard.EnsureSafe(journalPath, transactionDirectory);
    }

    private void ValidateCommitPaths(
        PackageStoreTransactionJournal journal,
        string transactionDirectory,
        string journalPath)
    {
        PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);
        PackageStorePathGuard.EnsureSafe(journalPath, transactionDirectory);
        foreach (var action in journal.Actions)
        {
            PackageStorePathGuard.EnsureSafe(action.TargetPath, paths.InstalledRootPath);
            if (action.StagingPath is not null)
            {
                PackageStorePathGuard.EnsureSafe(action.StagingPath, paths.StagingRootPath);
            }

            if (action.BackupPath is not null)
            {
                PackageStorePathGuard.EnsureSafe(action.BackupPath, transactionDirectory);
            }

            if (action.TombstonePath is not null)
            {
                PackageStorePathGuard.EnsureSafe(action.TombstonePath, paths.TombstoneRootPath);
            }
        }
    }

    private void Hit(PackageStoreFaultPoint point) => faultInjector?.Hit(point);

    private async Task ReconcileRuntimePolicyAsync(
        PackageStoreTransactionJournal journal,
        bool hitFaultPoints)
    {
        var hasPolicy = journal.RuntimePolicyUpdates.Count > 0 || packagePolicies is not null && journal.DesiredPackages.Any(package => package.Policy?.Snapshots.Count > 0);
        if (hitFaultPoints && hasPolicy) Hit(PackageStoreFaultPoint.PolicyReconciling);
        if (packagePolicies is not null)
            await packagePolicies.RestoreRetainedAsync(journal.DesiredPackages.SelectMany(package => package.Policy?.Snapshots ?? []), CancellationToken.None);
        if (journal.RuntimePolicyUpdates.Count == 0)
        {
            if (hitFaultPoints && hasPolicy) Hit(PackageStoreFaultPoint.PolicyReconciled);
            return;
        }
        if (lifecyclePolicies is null)
        {
            throw new InvalidOperationException(
                "Execution-runtime lifecycle policy persistence is unavailable for this package transaction.");
        }
        await lifecyclePolicies.ApplyEntriesAsync(
                journal.RuntimePolicyUpdates,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (hitFaultPoints)
        {
            Hit(PackageStoreFaultPoint.PolicyReconciled);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
