namespace Sunder.Runtime.Host.Services;

internal static class PackageStoreFileSystem
{
    internal static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path).ToArray() : [];
        }
        catch
        {
            return [];
        }
    }

    internal static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(path);
                    return;
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Durable journals and authoritative catalog state make cleanup retryable.
        }
    }

    internal static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Retried by later garbage collection.
        }
    }
}

internal sealed class PackageStoreJournalFileSystem(
    RuntimePackagePaths paths,
    IPackageStoreFaultInjector? faultInjector)
{
    internal async Task<PackageStoreTransactionJournal> CommitPayloadsAsync(
        PackageStoreTransactionJournal journal,
        string journalPath)
    {
        for (var index = 0; index < journal.Actions.Count; index++)
        {
            if (journal.Actions[index].Kind == PackageStoreJournalActionKind.AddOrReplace)
            {
                journal = await CommitAddOrReplaceAsync(journal, journalPath, index);
            }
        }

        for (var index = 0; index < journal.Actions.Count; index++)
        {
            if (journal.Actions[index].Kind == PackageStoreJournalActionKind.Remove)
            {
                journal = await CommitRemoveAsync(journal, journalPath, index);
            }
        }

        return journal;
    }

    internal async Task<PackageStoreTransactionJournal> RollBackAsync(
        PackageStoreTransactionJournal journal,
        string journalPath)
    {
        for (var index = journal.Actions.Count - 1; index >= 0; index--)
        {
            journal = journal.Actions[index].Kind == PackageStoreJournalActionKind.AddOrReplace
                ? await RollBackAddOrReplaceAsync(journal, journalPath, index)
                : await RollBackRemoveAsync(journal, journalPath, index);
        }

        return journal;
    }

    internal async Task<PackageStoreTransactionJournal> CleanupCommittedAsync(
        PackageStoreTransactionJournal journal,
        string journalPath)
    {
        for (var index = 0; index < journal.Actions.Count; index++)
        {
            var action = journal.Actions[index];
            if (action.Progress == PackageStoreJournalActionProgress.Cleaned)
            {
                continue;
            }

            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.CleanupStarted);
            PackageStoreFileSystem.TryDeleteDirectory(action.StagingPath);
            PackageStoreFileSystem.TryDeleteDirectory(action.BackupPath);
            PackageStoreFileSystem.TryDeleteDirectory(action.TombstonePath);
            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.Cleaned);
        }

        return journal;
    }

    private async Task<PackageStoreTransactionJournal> CommitAddOrReplaceAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index)
    {
        var action = journal.Actions[index];
        Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);

        if (action.Progress == PackageStoreJournalActionProgress.Pending)
        {
            if (Directory.Exists(action.TargetPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(action.BackupPath!)!);
                journal = await SetActionProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalActionProgress.BackupMoveStarted);
            }
            else
            {
                journal = await SetActionProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalActionProgress.OriginalAbsent);
            }

            action = journal.Actions[index];
        }

        if (action.Progress == PackageStoreJournalActionProgress.BackupMoveStarted)
        {
            var targetExists = Directory.Exists(action.TargetPath);
            var backupExists = Directory.Exists(action.BackupPath);
            if (targetExists && !backupExists)
            {
                MoveDirectory(action.TargetPath, action.BackupPath!);
                targetExists = false;
                backupExists = true;
            }

            EnsureMoveCompleted(journal, action, !targetExists && backupExists);
            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.BackupCreated);
            action = journal.Actions[index];
        }

        if (action.Progress is PackageStoreJournalActionProgress.BackupCreated
            or PackageStoreJournalActionProgress.OriginalAbsent)
        {
            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.TargetMoveStarted);
            action = journal.Actions[index];
        }

        if (action.Progress == PackageStoreJournalActionProgress.TargetMoveStarted)
        {
            var stagingExists = Directory.Exists(action.StagingPath);
            var targetExists = Directory.Exists(action.TargetPath);
            if (stagingExists && !targetExists)
            {
                MoveDirectory(action.StagingPath!, action.TargetPath);
                stagingExists = false;
                targetExists = true;
            }

            EnsureMoveCompleted(journal, action, !stagingExists && targetExists);
            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.TargetInstalled);
            Hit(PackageStoreFaultPoint.PayloadMoved);
        }

        return journal;
    }

    private async Task<PackageStoreTransactionJournal> CommitRemoveAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index)
    {
        var action = journal.Actions[index];
        if (action.Progress == PackageStoreJournalActionProgress.Pending)
        {
            if (!Directory.Exists(action.TargetPath))
            {
                return await SetActionProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalActionProgress.Removed);
            }

            Directory.CreateDirectory(paths.TombstoneRootPath);
            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.RemovalMoveStarted);
            action = journal.Actions[index];
        }

        if (action.Progress == PackageStoreJournalActionProgress.RemovalMoveStarted)
        {
            var targetExists = Directory.Exists(action.TargetPath);
            var tombstoneExists = Directory.Exists(action.TombstonePath);
            if (targetExists && !tombstoneExists)
            {
                MoveDirectory(action.TargetPath, action.TombstonePath!);
                targetExists = false;
                tombstoneExists = true;
            }

            EnsureMoveCompleted(journal, action, !targetExists && tombstoneExists);
            journal = await SetActionProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalActionProgress.Removed);
            Hit(PackageStoreFaultPoint.PayloadMoved);
        }

        return journal;
    }

    private async Task<PackageStoreTransactionJournal> RollBackAddOrReplaceAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index)
    {
        var action = journal.Actions[index];
        if (action.RollbackProgress == PackageStoreJournalRollbackProgress.Completed)
        {
            return journal;
        }

        if (action.Progress == PackageStoreJournalActionProgress.BackupMoveStarted)
        {
            var targetExists = Directory.Exists(action.TargetPath);
            var backupExists = Directory.Exists(action.BackupPath);
            if (!targetExists && backupExists)
            {
                journal = await SetActionProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalActionProgress.BackupCreated);
                action = journal.Actions[index];
            }
            else if (targetExists && !backupExists)
            {
                return await CompleteRollbackAsync(journal, journalPath, index);
            }
            else
            {
                throw InvalidMoveState(journal, action);
            }
        }

        if (action.Progress == PackageStoreJournalActionProgress.TargetMoveStarted)
        {
            var stagingExists = Directory.Exists(action.StagingPath);
            var targetExists = Directory.Exists(action.TargetPath);
            if (!stagingExists && targetExists)
            {
                journal = await SetActionProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalActionProgress.TargetInstalled);
                action = journal.Actions[index];
            }
            else if (!(stagingExists && !targetExists))
            {
                throw InvalidMoveState(journal, action);
            }
        }

        if (action.Progress == PackageStoreJournalActionProgress.TargetInstalled
            && action.RollbackProgress == PackageStoreJournalRollbackProgress.None)
        {
            journal = await SetRollbackProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalRollbackProgress.NewTargetMoveStarted);
            action = journal.Actions[index];
        }

        if (action.RollbackProgress == PackageStoreJournalRollbackProgress.NewTargetMoveStarted)
        {
            var targetExists = Directory.Exists(action.TargetPath);
            var stagingExists = Directory.Exists(action.StagingPath);
            if (targetExists && !stagingExists)
            {
                MoveDirectory(action.TargetPath, action.StagingPath!);
                targetExists = false;
                stagingExists = true;
            }

            EnsureMoveCompleted(journal, action, !targetExists && stagingExists);
            journal = await SetRollbackProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalRollbackProgress.NewTargetMoved);
            action = journal.Actions[index];
        }

        if (Directory.Exists(action.BackupPath)
            || action.RollbackProgress == PackageStoreJournalRollbackProgress.OriginalRestoreMoveStarted)
        {
            if (action.RollbackProgress != PackageStoreJournalRollbackProgress.OriginalRestoreMoveStarted)
            {
                journal = await SetRollbackProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalRollbackProgress.OriginalRestoreMoveStarted);
                action = journal.Actions[index];
            }

            var backupExists = Directory.Exists(action.BackupPath);
            var targetExists = Directory.Exists(action.TargetPath);
            if (backupExists && !targetExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);
                MoveDirectory(action.BackupPath!, action.TargetPath);
                backupExists = false;
                targetExists = true;
            }

            EnsureMoveCompleted(journal, action, !backupExists && targetExists);
            journal = await SetRollbackProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalRollbackProgress.OriginalRestored);
        }

        return await CompleteRollbackAsync(journal, journalPath, index);
    }

    private async Task<PackageStoreTransactionJournal> RollBackRemoveAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index)
    {
        var action = journal.Actions[index];
        if (action.RollbackProgress == PackageStoreJournalRollbackProgress.Completed)
        {
            return journal;
        }

        if (action.Progress == PackageStoreJournalActionProgress.RemovalMoveStarted)
        {
            var targetExists = Directory.Exists(action.TargetPath);
            var tombstoneExists = Directory.Exists(action.TombstonePath);
            if (!targetExists && tombstoneExists)
            {
                journal = await SetActionProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalActionProgress.Removed);
                action = journal.Actions[index];
            }
            else if (targetExists && !tombstoneExists)
            {
                return await CompleteRollbackAsync(journal, journalPath, index);
            }
            else
            {
                throw InvalidMoveState(journal, action);
            }
        }

        if ((action.Progress == PackageStoreJournalActionProgress.Removed
                && Directory.Exists(action.TombstonePath))
            || action.RollbackProgress == PackageStoreJournalRollbackProgress.OriginalRestoreMoveStarted)
        {
            if (action.RollbackProgress != PackageStoreJournalRollbackProgress.OriginalRestoreMoveStarted)
            {
                journal = await SetRollbackProgressAsync(
                    journal,
                    journalPath,
                    index,
                    PackageStoreJournalRollbackProgress.OriginalRestoreMoveStarted);
                action = journal.Actions[index];
            }

            var tombstoneExists = Directory.Exists(action.TombstonePath);
            var targetExists = Directory.Exists(action.TargetPath);
            if (tombstoneExists && !targetExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);
                MoveDirectory(action.TombstonePath!, action.TargetPath);
                tombstoneExists = false;
                targetExists = true;
            }

            EnsureMoveCompleted(journal, action, !tombstoneExists && targetExists);
            journal = await SetRollbackProgressAsync(
                journal,
                journalPath,
                index,
                PackageStoreJournalRollbackProgress.OriginalRestored);
        }

        return await CompleteRollbackAsync(journal, journalPath, index);
    }

    private async Task<PackageStoreTransactionJournal> CompleteRollbackAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index)
    {
        PackageStoreFileSystem.TryDeleteDirectory(journal.Actions[index].StagingPath);
        return await SetRollbackProgressAsync(
            journal,
            journalPath,
            index,
            PackageStoreJournalRollbackProgress.Completed);
    }

    private static async Task<PackageStoreTransactionJournal> SetActionProgressAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index,
        PackageStoreJournalActionProgress progress)
    {
        var actions = journal.Actions.ToArray();
        actions[index] = actions[index] with { Progress = progress };
        var updated = journal with { Actions = actions };
        await PackageStoreTransactionManager.WriteJournalAsync(journalPath, updated);
        return updated;
    }

    private static async Task<PackageStoreTransactionJournal> SetRollbackProgressAsync(
        PackageStoreTransactionJournal journal,
        string journalPath,
        int index,
        PackageStoreJournalRollbackProgress progress)
    {
        var actions = journal.Actions.ToArray();
        actions[index] = actions[index] with { RollbackProgress = progress };
        var updated = journal with { Actions = actions };
        await PackageStoreTransactionManager.WriteJournalAsync(journalPath, updated);
        return updated;
    }

    private void MoveDirectory(string sourcePath, string destinationPath)
    {
        PackageStorePathGuard.EnsureNotReparsePoint(sourcePath);
        PackageStorePathGuard.EnsureNotReparsePoint(destinationPath);
        Hit(PackageStoreFaultPoint.DirectoryMoveBefore);
        Directory.Move(sourcePath, destinationPath);
        Hit(PackageStoreFaultPoint.DirectoryMoveAfter);
    }

    private static void EnsureMoveCompleted(
        PackageStoreTransactionJournal journal,
        PackageStoreJournalAction action,
        bool completed)
    {
        if (!completed)
        {
            throw InvalidMoveState(journal, action);
        }
    }

    private static InvalidDataException InvalidMoveState(
        PackageStoreTransactionJournal journal,
        PackageStoreJournalAction action) =>
        new($"Package transaction '{journal.TransactionId}' has inconsistent move state for '{action.TargetPath}'.");

    private void Hit(PackageStoreFaultPoint point) => faultInjector?.Hit(point);
}
