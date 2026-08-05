using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreTransactionManager(
    RuntimePackagePaths paths,
    InstalledPackageStore store,
    IPackageStoreFaultInjector? faultInjector)
{
    private readonly PackageStoreJournalFileSystem _fileSystem = new(paths, faultInjector);

    internal async Task<PackageOperationResult> CommitAsync(
        PendingStoreStage stage,
        CancellationToken cancellationToken)
    {
        var transactionDirectory = Path.Combine(paths.TransactionRootPath, stage.StageId);
        var journalPath = Path.Combine(transactionDirectory, "journal.json");
        var journal = CreateJournal(stage, transactionDirectory);
        try
        {
            ValidateCommitPaths(journal, transactionDirectory, journalPath);
        }
        catch (InvalidDataException exception)
        {
            PackageStoreStageManager.Cleanup(stage.PreparedArchives);
            return PackageOperationResults.Failure(
                $"Failed to commit package store transaction '{stage.StageId}': {exception.Message}");
        }

        Directory.CreateDirectory(transactionDirectory);
        PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);

        try
        {
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.JournalPrepared);

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
            journal = journal with { Phase = PackageStoreTransactionPhase.CatalogCommitted };
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.CatalogCommitted);
        }
        catch (PackageStoreSimulatedCrashException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RollBackAsync(journal, journalPath);
            return PackageOperationResults.Failure(
                $"Failed to commit package store transaction '{stage.StageId}': {exception.Message}");
        }

        try
        {
            Hit(PackageStoreFaultPoint.Cleanup);
            journal = await CleanupCommittedAsync(journal, journalPath);
            journal = journal with { Phase = PackageStoreTransactionPhase.Completed };
            await WriteJournalAsync(journalPath, journal);
            PackageStoreFileSystem.TryDeleteDirectory(transactionDirectory);
            return stage.Result;
        }
        catch (Exception exception)
        {
            journal = journal with { Phase = PackageStoreTransactionPhase.CleanupPending };
            await WriteJournalAsync(journalPath, journal);
            return stage.Result with
            {
                Warnings = stage.Result.Warnings.Concat([
                    $"Package files were committed, but garbage collection will retry after cleanup failed: {exception.Message}",
                ]).ToArray(),
            };
        }
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

    internal static Task WriteJournalAsync(string journalPath, PackageStoreTransactionJournal journal) =>
        DurableJsonDocument.WriteAsync(
            journalPath,
            journal,
            InstalledPackageStore.JsonOptions,
            CancellationToken.None);

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
            2,
            stage.StageId,
            PackageStoreTransactionPhase.Prepared,
            stage.PreviousPackages,
            stage.DesiredPackages,
            actions);
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

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
