using System.Collections.Concurrent;
using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreRecovery(
    RuntimePackagePaths paths,
    InstalledPackageStore store,
    PackageStoreTransactionManager transactions)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates = new(PathComparer);

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var gate = RootGates.GetOrAdd(paths.RootPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateExistingRoots();
            var pendingTransactions = new List<(
                string TransactionDirectory,
                string JournalPath,
                PackageStoreTransactionJournal? Journal)>();
            foreach (var transactionDirectory in PackageStoreFileSystem.EnumerateDirectories(paths.TransactionRootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);
                var journalPath = Path.Combine(transactionDirectory, "journal.json");
                if (!File.Exists(journalPath))
                {
                    pendingTransactions.Add((transactionDirectory, journalPath, null));
                    continue;
                }

                PackageStorePathGuard.EnsureSafe(journalPath, transactionDirectory);
                var journal = await ReadJournalAsync(journalPath, cancellationToken);
                ValidateJournal(journal, transactionDirectory);
                pendingTransactions.Add((transactionDirectory, journalPath, journal));
            }

            CreateAndValidateRoots();
            foreach (var pending in pendingTransactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pending.Journal is null)
                {
                    PackageStoreFileSystem.TryDeleteDirectory(pending.TransactionDirectory);
                    continue;
                }
                // Package rollback cannot compensate an already observed policy denial.
                await transactions.RestorePackagePolicyEvidenceAsync(pending.Journal);
                await RecoverTransactionAsync(pending.Journal, pending.JournalPath);
            }

            await CollectGarbageAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private void ValidateExistingRoots()
    {
        foreach (var path in EnumerateStoreRoots())
        {
            PackageStorePathGuard.EnsureNotReparsePoint(path);
        }
    }

    private void CreateAndValidateRoots()
    {
        foreach (var path in EnumerateStoreRoots())
        {
            DurableJsonDocument.CreateDirectory(path);
            PackageStorePathGuard.EnsureNotReparsePoint(path);
        }
    }

    private IEnumerable<string> EnumerateStoreRoots()
    {
        yield return paths.RootPath;
        yield return paths.InstalledRootPath;
        yield return paths.StagingRootPath;
        yield return paths.TransactionRootPath;
        yield return paths.TombstoneRootPath;
    }

    internal async Task<IReadOnlyList<PackageStoreTransactionJournal>> ReadPendingJournalsAsync(
        CancellationToken cancellationToken)
    {
        var journals = new List<PackageStoreTransactionJournal>();
        foreach (var transactionDirectory in PackageStoreFileSystem.EnumerateDirectories(paths.TransactionRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);
            var journalPath = Path.Combine(transactionDirectory, "journal.json");
            if (!File.Exists(journalPath))
            {
                if (!Directory.Exists(transactionDirectory))
                {
                    continue;
                }
                throw new InvalidDataException(
                    $"Package transaction directory '{transactionDirectory}' has no durable journal.");
            }

            PackageStorePathGuard.EnsureSafe(journalPath, transactionDirectory);
            var journal = await ReadJournalAsync(journalPath, cancellationToken).ConfigureAwait(false);
            ValidateJournal(journal, transactionDirectory);
            journals.Add(journal);
        }
        return journals;
    }

    private static async Task<PackageStoreTransactionJournal> ReadJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(journalPath, cancellationToken);
        try
        {
            var schemaVersion = ReadSchemaVersion(json);
            if (schemaVersion != PackageStoreTransactionManager.JournalSchemaVersion)
            {
                throw UnsupportedSchema(journalPath, schemaVersion);
            }
            var journal = JsonSerializer.Deserialize<PackageStoreTransactionJournal>(
                       json,
                       InstalledPackageStore.JsonOptions)
                   ?? throw new InvalidDataException($"Package transaction journal '{journalPath}' is invalid.");
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Package transaction journal '{journalPath}' is invalid.", exception);
        }
    }

    private async Task RecoverTransactionAsync(PackageStoreTransactionJournal journal, string journalPath)
    {
        if (journal.CutoverPhase == PackageStoreCutoverPhase.Completed)
        {
            await transactions.ReconcileRuntimePolicyAsync(journal).ConfigureAwait(false);
            if (journal.Phase != PackageStoreTransactionPhase.Completed)
            {
                journal = await transactions.CleanupCommittedAsync(journal, journalPath);
                journal = journal with { Phase = PackageStoreTransactionPhase.Completed };
                await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);
            }
            PackageStoreFileSystem.TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
            return;
        }
        var catalog = await store.ListAsync(CancellationToken.None);
        var catalogCommitted = PackageStorePolicy.CatalogsEqual(catalog, journal.DesiredPackages);
        var previousCatalogConfirmed = PackageStorePolicy.CatalogsEqual(catalog, journal.PreviousPackages);
        if (journal.Phase is PackageStoreTransactionPhase.Prepared or PackageStoreTransactionPhase.RolledBack)
        {
            PackageStoreFileSystem.TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
            return;
        }
        var durableCommitConfirmed = journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted
                                     || journal.Phase is PackageStoreTransactionPhase.CatalogCommitted
                                         or PackageStoreTransactionPhase.CleanupPending
                                         or PackageStoreTransactionPhase.Completed
                                     || journal.Phase == PackageStoreTransactionPhase.CatalogCommitting
                                     && catalogCommitted;
        if (durableCommitConfirmed)
        {
            if (!catalogCommitted)
            {
                throw new InvalidDataException(
                    $"Package transaction '{journal.TransactionId}' says its catalog committed, but the authoritative catalog differs.");
            }

            if (journal.CutoverPhase < PackageStoreCutoverPhase.DurableCommitted
                || journal.Phase < PackageStoreTransactionPhase.CatalogCommitted)
            {
                journal = journal with
                {
                    CutoverPhase = PackageStoreCutoverPhase.DurableCommitted,
                    Phase = PackageStoreTransactionPhase.CatalogCommitted,
                };
                await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);
            }
            await transactions.ReconcileRuntimePolicyAsync(journal).ConfigureAwait(false);
            if (journal.CutoverPhase < PackageStoreCutoverPhase.Completed)
            {
                journal = await transactions.RestoreStorageActivationAsync(journal, journalPath);
            }
            if (journal.Phase != PackageStoreTransactionPhase.Completed)
            {
                journal = await transactions.CleanupCommittedAsync(journal, journalPath);
                journal = journal with { Phase = PackageStoreTransactionPhase.Completed };
                await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);
            }
            if (!journal.RequiresRuntimePublication
                && journal.CutoverPhase < PackageStoreCutoverPhase.Completed)
            {
                if (journal.CutoverPhase < PackageStoreCutoverPhase.GenerationCommitAcknowledged)
                {
                    journal = journal with
                    {
                        CutoverPhase = PackageStoreCutoverPhase.GenerationCommitAcknowledged,
                    };
                    await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);
                }
                if (journal.CutoverPhase < PackageStoreCutoverPhase.OldGenerationStopped)
                {
                    journal = journal with { CutoverPhase = PackageStoreCutoverPhase.OldGenerationStopped };
                    await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);
                }
                journal = journal with { CutoverPhase = PackageStoreCutoverPhase.Completed };
                await PackageStoreTransactionManager.WriteJournalAsync(journalPath, journal);
            }
            if (journal.CutoverPhase == PackageStoreCutoverPhase.Completed)
            {
                PackageStoreFileSystem.TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
            }
            return;
        }

        if (!previousCatalogConfirmed)
        {
            throw new InvalidDataException(
                $"Package transaction '{journal.TransactionId}' has an indeterminate authoritative catalog. Transaction evidence was preserved.");
        }

        await transactions.RollBackAsync(journal, journalPath);
    }

    private async Task CollectGarbageAsync(CancellationToken cancellationToken)
    {
        foreach (var path in PackageStoreFileSystem.EnumerateDirectories(paths.TombstoneRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageStorePathGuard.EnsureSafe(path, paths.TombstoneRootPath);
            PackageStoreFileSystem.TryDeleteDirectory(path);
        }

        foreach (var path in PackageStoreFileSystem.EnumerateDirectories(paths.StagingRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageStorePathGuard.EnsureSafe(path, paths.StagingRootPath);
            PackageStoreFileSystem.TryDeleteDirectory(path);
        }

        foreach (var packageDataDirectory in PackageStoreFileSystem.EnumerateDirectories(paths.PackageDataRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageStorePathGuard.EnsureSafe(packageDataDirectory, paths.PackageDataRootPath);
            var activationBackups = new[]
            {
                ".workspace.sunder-activation-*",
                ".files.sunder-activation-*",
            }.SelectMany(pattern => Directory.EnumerateDirectories(
                packageDataDirectory,
                pattern,
                SearchOption.TopDirectoryOnly)).ToArray();
            if (activationBackups.Length > 0)
            {
                foreach (var backupPath in activationBackups)
                {
                    PackageStorePathGuard.EnsureSafe(backupPath, paths.PackageDataRootPath);
                }
                throw new InvalidDataException(
                    $"Package data directory '{packageDataDirectory}' contains an unjournaled storage activation backup. " +
                    "The backup was preserved because startup cannot determine whether activation committed or requires rollback.");
            }
        }

        var installedPackages = await store.ListAsync(cancellationToken);
        var authoritativePaths = installedPackages.Select(package => package.InstallPath).ToHashSet(PathComparer);
        foreach (var packageDirectory in PackageStoreFileSystem.EnumerateDirectories(paths.InstalledRootPath))
        {
            PackageStorePathGuard.EnsureSafe(packageDirectory, paths.InstalledRootPath);
            foreach (var versionDirectory in PackageStoreFileSystem.EnumerateDirectories(packageDirectory))
            {
                PackageStorePathGuard.EnsureSafe(versionDirectory, paths.InstalledRootPath);
                var canonicalPath = Path.GetFullPath(versionDirectory);
                if (authoritativePaths.Contains(canonicalPath))
                {
                    continue;
                }

                var tombstonePath = Path.Combine(paths.TombstoneRootPath, "orphan-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.Move(canonicalPath, tombstonePath);
                    PackageStoreFileSystem.TryDeleteDirectory(tombstonePath);
                }
                catch
                {
                    // The next startup retries orphan collection. Catalog records remain authoritative.
                }
            }

            PackageStoreFileSystem.TryDeleteEmptyDirectory(packageDirectory);
        }
    }

    private void ValidateJournal(PackageStoreTransactionJournal journal, string transactionDirectory)
    {
        if (journal.SchemaVersion != PackageStoreTransactionManager.JournalSchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.CutoverPhase)
            || !Enum.IsDefined(journal.Phase)
            || journal.PreviousPackages is null
            || journal.DesiredPackages is null
            || journal.Actions is null
            || journal.StorageSnapshots is null
            || !string.Equals(
                transactionDirectory,
                Path.Combine(paths.TransactionRootPath, journal.TransactionId),
                PathComparison))
        {
            throw InvalidJournal(transactionDirectory);
        }

        if ((journal.CutoverPhase < PackageStoreCutoverPhase.DurableCommitStarted
                && journal.Phase != PackageStoreTransactionPhase.Prepared
            || journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted
                && journal.Phase < PackageStoreTransactionPhase.CatalogCommitted
            || journal.CutoverPhase == PackageStoreCutoverPhase.Completed
                && journal.StorageSnapshots.Count != 0)
            || journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted
                && journal.Phase == PackageStoreTransactionPhase.RolledBack)
        {
            throw InvalidJournal(transactionDirectory);
        }

        store.ValidateCatalog(journal.PreviousPackages);
        store.ValidateCatalog(journal.DesiredPackages);
        var previousPaths = journal.PreviousPackages.Select(package => package.InstallPath).ToHashSet(PathComparer);
        var desiredPaths = journal.DesiredPackages.Select(package => package.InstallPath).ToHashSet(PathComparer);
        foreach (var action in journal.Actions)
        {
            if (!Enum.IsDefined(action.Kind)
                || !Enum.IsDefined(action.Progress)
                || !Enum.IsDefined(action.RollbackProgress)
                || string.IsNullOrWhiteSpace(action.TargetPath)
                || action.Kind == PackageStoreJournalActionKind.AddOrReplace
                    && (action.StagingPath is null || action.BackupPath is null || action.TombstonePath is not null)
                || action.Kind == PackageStoreJournalActionKind.Remove
                    && (action.StagingPath is not null || action.BackupPath is not null || action.TombstonePath is null))
            {
                throw InvalidJournal(transactionDirectory);
            }

            var expectedTarget = action.Kind == PackageStoreJournalActionKind.AddOrReplace
                ? desiredPaths.Contains(action.TargetPath)
                : previousPaths.Contains(action.TargetPath);
            if (!expectedTarget)
            {
                throw new InvalidDataException(
                    $"Package transaction '{journal.TransactionId}' contains an unsafe filesystem path.");
            }

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

        var storageRoot = Path.Combine(transactionDirectory, "storage");
        var storageTargets = new HashSet<string>(PathComparer);
        var desiredPackageIds = journal.DesiredPackages
            .Select(static package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in journal.StorageSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.TargetPath)
                || string.IsNullOrWhiteSpace(snapshot.SnapshotPath)
                || string.IsNullOrWhiteSpace(snapshot.PackageId)
                || !Enum.IsDefined(snapshot.Kind)
                || !desiredPackageIds.Contains(snapshot.PackageId)
                || !storageTargets.Add(snapshot.TargetPath))
            {
                throw InvalidJournal(transactionDirectory);
            }
            var packageRoot = Sunder.Runtime.Host.Infrastructure.Storage.LocalPackageStorageContext.GetPackageRootPath(
                snapshot.PackageId,
                paths.PackageDataRootPath);
            var expectedDirectoryName = snapshot.Kind switch
            {
                PackageStoreStorageSnapshotKind.Data => "data",
                PackageStoreStorageSnapshotKind.Workspace => "workspace",
                PackageStoreStorageSnapshotKind.Files => "files",
                _ => throw InvalidJournal(transactionDirectory),
            };
            if (!string.Equals(
                    snapshot.TargetPath,
                    Path.Combine(packageRoot, expectedDirectoryName),
                    PathComparison)
                || !string.Equals(Path.GetDirectoryName(snapshot.SnapshotPath), storageRoot, PathComparison))
            {
                throw InvalidJournal(transactionDirectory);
            }
            PackageStorePathGuard.EnsureSafe(snapshot.TargetPath, paths.PackageDataRootPath);
            PackageStorePathGuard.EnsureSafe(snapshot.SnapshotPath, storageRoot);
            if (snapshot.Existed && !Directory.Exists(snapshot.SnapshotPath))
            {
                throw new InvalidDataException(
                    $"Package transaction '{journal.TransactionId}' is missing a durable package storage snapshot.");
            }
        }
    }

    private static InvalidDataException InvalidJournal(string transactionDirectory) =>
        new($"Package transaction journal in '{transactionDirectory}' is invalid.");

    private static int ReadSchemaVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Package transaction journal root must be an object.");
        }

        var schemaProperties = document.RootElement
            .EnumerateObject()
            .Where(static property => property.NameEquals(nameof(PackageStoreTransactionJournal.SchemaVersion)))
            .ToArray();
        if (schemaProperties.Length != 1
            || schemaProperties[0].Value.ValueKind != JsonValueKind.Number
            || !schemaProperties[0].Value.TryGetInt32(out var schemaVersion))
        {
            throw new JsonException("Package transaction journal must contain one integer schemaVersion.");
        }

        return schemaVersion;
    }

    private static InvalidDataException UnsupportedSchema(string journalPath, int schemaVersion)
        => new(
            $"Package transaction journal '{journalPath}' uses unsupported pre-release schema version {schemaVersion}. "
            + "No migration or reinterpretation is supported. Stop the Runtime and run 'sunder runtime reset --yes'; "
            + "the data-safe reset preserves package data, settings, secrets, credentials, workspaces, and user files.");

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal static class PackageStorePathGuard
{
    internal static void EnsureSafe(string path, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        if (!string.Equals(candidate, root, PathComparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException($"Package store path '{path}' is outside its trusted root.");
        }

        EnsureNotReparsePoint(root);
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    internal static void EnsureNotReparsePoint(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Package store path '{path}' must not be a symbolic link or reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
