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
            CreateAndValidateRoots();
            foreach (var transactionDirectory in PackageStoreFileSystem.EnumerateDirectories(paths.TransactionRootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageStorePathGuard.EnsureSafe(transactionDirectory, paths.TransactionRootPath);
                var journalPath = Path.Combine(transactionDirectory, "journal.json");
                if (!File.Exists(journalPath))
                {
                    PackageStoreFileSystem.TryDeleteDirectory(transactionDirectory);
                    continue;
                }

                PackageStorePathGuard.EnsureSafe(journalPath, transactionDirectory);
                var journal = await ReadJournalAsync(journalPath, cancellationToken);
                ValidateJournal(journal, transactionDirectory);
                await RecoverTransactionAsync(journal, journalPath);
            }

            await CollectGarbageAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private void CreateAndValidateRoots()
    {
        foreach (var path in new[]
                 {
                     paths.RootPath,
                     paths.InstalledRootPath,
                     paths.StagingRootPath,
                     paths.TransactionRootPath,
                     paths.TombstoneRootPath,
                 })
        {
            Directory.CreateDirectory(path);
            PackageStorePathGuard.EnsureNotReparsePoint(path);
        }
    }

    private static async Task<PackageStoreTransactionJournal> ReadJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageStoreTransactionJournal>(
                       await File.ReadAllTextAsync(journalPath, cancellationToken),
                       InstalledPackageStore.JsonOptions)
                   ?? throw new InvalidDataException($"Package transaction journal '{journalPath}' is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Package transaction journal '{journalPath}' is invalid.", exception);
        }
    }

    private async Task RecoverTransactionAsync(PackageStoreTransactionJournal journal, string journalPath)
    {
        var catalog = await store.ListAsync(CancellationToken.None);
        var catalogCommitted = PackageStorePolicy.CatalogsEqual(catalog, journal.DesiredPackages);
        if (journal.Phase is PackageStoreTransactionPhase.CatalogCommitted
            or PackageStoreTransactionPhase.CleanupPending
            or PackageStoreTransactionPhase.Completed
            || (journal.Phase == PackageStoreTransactionPhase.CatalogCommitting && catalogCommitted))
        {
            if (!catalogCommitted)
            {
                throw new InvalidDataException(
                    $"Package transaction '{journal.TransactionId}' says its catalog committed, but the authoritative catalog differs.");
            }

            await transactions.CleanupCommittedAsync(journal, journalPath);
            PackageStoreFileSystem.TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
            return;
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
        if (journal.SchemaVersion != 2
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.Phase)
            || journal.PreviousPackages is null
            || journal.DesiredPackages is null
            || journal.Actions is null
            || !string.Equals(
                transactionDirectory,
                Path.Combine(paths.TransactionRootPath, journal.TransactionId),
                PathComparison))
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
    }

    private static InvalidDataException InvalidJournal(string transactionDirectory) =>
        new($"Package transaction journal in '{transactionDirectory}' is invalid.");

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
