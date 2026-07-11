using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreCoordinator
{
    private readonly RuntimePackagePaths _paths;
    private readonly InstalledPackageStore _store;
    private readonly SunderPackageArchiveInstaller _archiveInstaller;
    private readonly IPackageStoreFaultInjector? _faultInjector;
    private readonly Dictionary<string, PendingStoreStage> _stages = new(StringComparer.Ordinal);
    private long _catalogGeneration;

    public PackageStoreCoordinator(
        RuntimePackagePaths paths,
        InstalledPackageStore store,
        SunderPackageArchiveInstaller archiveInstaller,
        IPackageStoreFaultInjector? faultInjector = null)
    {
        _paths = paths;
        _store = store;
        _archiveInstaller = archiveInstaller;
        _faultInjector = faultInjector;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.RootPath);
        Directory.CreateDirectory(_paths.InstalledRootPath);
        Directory.CreateDirectory(_paths.StagingRootPath);
        Directory.CreateDirectory(_paths.TransactionRootPath);
        Directory.CreateDirectory(_paths.TombstoneRootPath);

        foreach (var transactionDirectory in Directory.EnumerateDirectories(_paths.TransactionRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journalPath = Path.Combine(transactionDirectory, "journal.json");
            if (!File.Exists(journalPath))
            {
                TryDeleteDirectory(transactionDirectory);
                continue;
            }

            var journal = JsonSerializer.Deserialize<PackageStoreTransactionJournal>(
                              await File.ReadAllTextAsync(journalPath, cancellationToken),
                              InstalledPackageStore.JsonOptions)
                          ?? throw new InvalidDataException($"Package transaction journal '{journalPath}' is invalid.");
            ValidateJournal(journal, transactionDirectory);
            await RecoverTransactionAsync(journal, journalPath);
        }

        await CollectGarbageAsync(cancellationToken);
        _catalogGeneration++;
    }

    public async Task<PackageStoreStagePreparation> PrepareStageAsync(
        IReadOnlyList<PackageStoreMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        if (mutations.Count == 0)
        {
            return new PackageStoreStagePreparation(
                null,
                PackageOperationResults.Failure("At least one package store mutation is required."));
        }

        var current = (await _store.ListAsync(cancellationToken)).ToArray();
        var desired = current.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var prospective = current.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var preparedByPackageId = new Dictionary<string, PreparedPackageArchiveMutation>(StringComparer.OrdinalIgnoreCase);
        var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var messages = new List<string>();

        try
        {
            foreach (var mutation in mutations.Where(mutation => mutation.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selectedPackage = string.IsNullOrWhiteSpace(mutation.PackageId)
                    ? null
                    : desired.GetValueOrDefault(mutation.PackageId);
                var isUpgrade = mutation.Kind == PackageStoreMutationKind.Upgrade;
                var preparation = await _archiveInstaller.PrepareAsync(
                    mutation.ArchiveFilePath ?? string.Empty,
                    selectedPackage?.IsEnabled ?? true,
                    cancellationToken);
                if (!preparation.Success || preparation.Mutation is null)
                {
                    return FailResult(preparation.Failure ?? PackageOperationResults.Failure("Package archive staging failed."));
                }

                var archive = preparation.Mutation;
                if (isUpgrade && !string.Equals(archive.InstalledRecord.PackageId, mutation.PackageId, StringComparison.OrdinalIgnoreCase))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package archive '{archive.InstalledRecord.PackageId}' does not match selected package '{mutation.PackageId}'.");
                }

                if (isUpgrade && selectedPackage is null)
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{mutation.PackageId}' is not installed.");
                }

                if (!isUpgrade && desired.ContainsKey(archive.InstalledRecord.PackageId))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{archive.InstalledRecord.PackageId}' is already installed.");
                }

                if (!preparedByPackageId.TryAdd(archive.InstalledRecord.PackageId, archive))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{archive.InstalledRecord.PackageId}' is mutated by more than one archive in the same transaction.");
                }

                if (isUpgrade)
                {
                    var versionError = PackageStorePolicy.ValidateReplacementVersion(selectedPackage!, archive.InstalledRecord, mutation.AllowDowngrade, mutation.Reinstall);
                    if (versionError is not null)
                    {
                        return Fail(versionError);
                    }

                    archive = archive with
                    {
                        StagedRecord = archive.StagedRecord with { IsEnabled = selectedPackage!.IsEnabled },
                        InstalledRecord = archive.InstalledRecord with { IsEnabled = selectedPackage.IsEnabled },
                    };
                    preparedByPackageId[archive.InstalledRecord.PackageId] = archive;
                    messages.Add($"Updated package '{archive.InstalledRecord.Name}' from {selectedPackage.Version} to {archive.InstalledRecord.Version}.");
                }
                else
                {
                    messages.Add($"Installed package '{archive.InstalledRecord.Name}' {archive.InstalledRecord.Version}.");
                }

                desired[archive.InstalledRecord.PackageId] = archive.InstalledRecord;
                prospective[archive.StagedRecord.PackageId] = archive.StagedRecord;
                impactedPackageIds.Add(archive.InstalledRecord.PackageId);
            }

            foreach (var mutation in mutations.Where(mutation => mutation.Kind is not (PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(mutation.PackageId))
                {
                    return Fail("Package id is required.");
                }

                switch (mutation.Kind)
                {
                    case PackageStoreMutationKind.Enable:
                    case PackageStoreMutationKind.Disable:
                    {
                        if (!desired.TryGetValue(mutation.PackageId, out var package))
                        {
                            return Fail($"Package '{mutation.PackageId}' is not installed.");
                        }

                        var enabled = mutation.Kind == PackageStoreMutationKind.Enable;
                        if (package.IsEnabled == enabled)
                        {
                            messages.Add($"Package '{package.Name}' is already {(enabled ? "enabled" : "disabled")}.");
                            break;
                        }

                        desired[package.PackageId] = package with { IsEnabled = enabled };
                        prospective[package.PackageId] = prospective[package.PackageId] with { IsEnabled = enabled };
                        impactedPackageIds.Add(package.PackageId);
                        messages.Add($"{(enabled ? "Enabled" : "Disabled")} package '{package.Name}'.");
                        break;
                    }
                    case PackageStoreMutationKind.Uninstall:
                    {
                        if (!desired.TryGetValue(mutation.PackageId, out var package))
                        {
                            return Fail($"Package '{mutation.PackageId}' is not installed.");
                        }

                        var removalIds = PackageStorePolicy.BuildRemovalSet(package.PackageId, desired.Values);
                        foreach (var packageId in removalIds)
                        {
                            desired.Remove(packageId);
                            prospective.Remove(packageId);
                            impactedPackageIds.Add(packageId);
                        }

                        messages.Add(removalIds.Count == 1
                            ? $"Uninstalled package '{package.Name}'."
                            : $"Uninstalled package '{package.Name}' and {removalIds.Count - 1} dependent package(s).");
                        break;
                    }
                    default:
                        return Fail($"Unsupported package store mutation kind '{mutation.Kind}'.");
                }
            }

            var desiredPackages = desired.Values.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray();
            var validationErrors = PackageStorePolicy.ValidateCatalog(_store, desiredPackages);
            if (validationErrors.Count > 0)
            {
                return FailResult(PackageOperationResults.Failure(validationErrors[0], validationErrors));
            }

            foreach (var replacedPackageId in preparedByPackageId.Keys)
            {
                if (!desired.ContainsKey(replacedPackageId))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(preparedByPackageId[replacedPackageId].StagingPath);
                }
            }

            PackageStorePolicy.AddImpactedDependents(impactedPackageIds, desiredPackages);
            var result = PackageOperationResults.Success(
                PackageStorePolicy.BuildMessage(messages, impactedPackageIds.Count),
                warnings: warnings,
                impactedPackageIds: impactedPackageIds.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray());
            var stageId = Guid.NewGuid().ToString("N");
            var pendingStage = new PendingStoreStage(
                stageId,
                current,
                desiredPackages,
                prospective.Values.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray(),
                preparedByPackageId.Values.Where(archive => desired.ContainsKey(archive.InstalledRecord.PackageId)).ToArray(),
                result,
                _catalogGeneration);
            _stages.Add(stageId, pendingStage);
            return new PackageStoreStagePreparation(
                new PackageStorePreparedStage(stageId, pendingStage.ProspectivePackages, result, _catalogGeneration),
                null);
        }
        catch
        {
            CleanupPreparedArchives(preparedByPackageId.Values);
            throw;
        }

        PackageStoreStagePreparation Fail(string message) => FailResult(PackageOperationResults.Failure(message));

        PackageStoreStagePreparation FailResult(PackageOperationResult failure)
        {
            CleanupPreparedArchives(preparedByPackageId.Values);
            return new PackageStoreStagePreparation(null, failure);
        }
    }

    public async Task<PackageOperationResult> CommitStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        if (!_stages.Remove(stageId, out var stage))
        {
            return PackageOperationResults.Failure($"Package store stage '{stageId}' was not found.");
        }

        if (stage.BaseCatalogGeneration != _catalogGeneration)
        {
            CleanupPreparedArchives(stage.PreparedArchives);
            return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the installed package catalog changed before commit.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CleanupPreparedArchives(stage.PreparedArchives);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var transactionDirectory = Path.Combine(_paths.TransactionRootPath, stage.StageId);
        var journalPath = Path.Combine(transactionDirectory, "journal.json");
        var journal = CreateJournal(stage, transactionDirectory);
        Directory.CreateDirectory(transactionDirectory);

        try
        {
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.JournalPrepared);

            journal = journal with { Phase = PackageStoreTransactionPhase.PayloadsCommitting };
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.PayloadsCommitting);
            CommitPayloads(journal);

            journal = journal with { Phase = PackageStoreTransactionPhase.PayloadsCommitted };
            await WriteJournalAsync(journalPath, journal);
            Hit(PackageStoreFaultPoint.PayloadsCommitted);

            Hit(PackageStoreFaultPoint.CatalogReplacing);
            await _store.WriteAsync(stage.DesiredPackages, CancellationToken.None);
            Hit(PackageStoreFaultPoint.CatalogReplaced);
            journal = journal with { Phase = PackageStoreTransactionPhase.CatalogCommitted };
            await WriteJournalAsync(journalPath, journal);
            _catalogGeneration++;
            Hit(PackageStoreFaultPoint.CatalogCommitted);
        }
        catch (PackageStoreSimulatedCrashException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RollBackAsync(journal, journalPath);
            return PackageOperationResults.Failure($"Failed to commit package store transaction '{stageId}': {ex.Message}");
        }

        try
        {
            Hit(PackageStoreFaultPoint.Cleanup);
            CleanupCommittedTransaction(journal);
            journal = journal with { Phase = PackageStoreTransactionPhase.Completed };
            await WriteJournalAsync(journalPath, journal);
            TryDeleteDirectory(transactionDirectory);
            return stage.Result;
        }
        catch (Exception ex)
        {
            journal = journal with { Phase = PackageStoreTransactionPhase.CleanupPending };
            await WriteJournalAsync(journalPath, journal);
            return stage.Result with
            {
                Warnings = stage.Result.Warnings.Concat([$"Package files were committed, but garbage collection will retry after cleanup failed: {ex.Message}"]).ToArray(),
            };
        }
    }

    public Task<bool> DiscardStageAsync(string stageId)
    {
        if (!_stages.Remove(stageId, out var stage))
        {
            return Task.FromResult(false);
        }

        CleanupPreparedArchives(stage.PreparedArchives);
        return Task.FromResult(true);
    }

    public async Task<PackageOperationResult> ExecuteAsync(
        IReadOnlyList<PackageStoreMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareStageAsync(mutations, cancellationToken);
        return preparation.Stage is null
            ? preparation.Failure ?? PackageOperationResults.Failure("Package store transaction could not be prepared.")
            : await CommitStageAsync(preparation.Stage.StageId, cancellationToken);
    }

    public async Task DiscardAllStagesAsync()
    {
        foreach (var stageId in _stages.Keys.ToArray())
        {
            await DiscardStageAsync(stageId);
        }
    }

    internal long CatalogGeneration => _catalogGeneration;

    internal RuntimePackagePaths Paths => _paths;

    private PackageStoreTransactionJournal CreateJournal(PendingStoreStage stage, string transactionDirectory)
    {
        var desiredById = stage.DesiredPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var preparedById = stage.PreparedArchives.ToDictionary(archive => archive.InstalledRecord.PackageId, StringComparer.OrdinalIgnoreCase);
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

        var replacementDestinations = stage.PreparedArchives
            .Select(archive => archive.InstalledPath)
            .ToHashSet(PathComparer);
        foreach (var previousPackage in stage.PreviousPackages)
        {
            if (desiredById.TryGetValue(previousPackage.PackageId, out var desiredPackage)
                && string.Equals(previousPackage.InstallPath, desiredPackage.InstallPath, PathComparison))
            {
                continue;
            }

            if (replacementDestinations.Contains(previousPackage.InstallPath))
            {
                continue;
            }

            actions.Add(new PackageStoreJournalAction(
                PackageStoreJournalActionKind.Remove,
                null,
                previousPackage.InstallPath,
                null,
                Path.Combine(_paths.TombstoneRootPath, $"{stage.StageId}-{index++:D4}")));
        }

        return new PackageStoreTransactionJournal(
            1,
            stage.StageId,
            PackageStoreTransactionPhase.Prepared,
            stage.PreviousPackages,
            stage.DesiredPackages,
            actions);
    }

    private void CommitPayloads(PackageStoreTransactionJournal journal)
    {
        foreach (var action in journal.Actions.Where(action => action.Kind == PackageStoreJournalActionKind.AddOrReplace))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);
            if (Directory.Exists(action.TargetPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(action.BackupPath!)!);
                Directory.Move(action.TargetPath, action.BackupPath!);
            }

            Directory.Move(action.StagingPath!, action.TargetPath);
            Hit(PackageStoreFaultPoint.PayloadMoved);
        }

        foreach (var action in journal.Actions.Where(action => action.Kind == PackageStoreJournalActionKind.Remove))
        {
            if (!Directory.Exists(action.TargetPath))
            {
                continue;
            }

            Directory.CreateDirectory(_paths.TombstoneRootPath);
            Directory.Move(action.TargetPath, action.TombstonePath!);
            Hit(PackageStoreFaultPoint.PayloadMoved);
        }
    }

    private async Task RecoverTransactionAsync(PackageStoreTransactionJournal journal, string journalPath)
    {
        var catalog = await _store.ListAsync(CancellationToken.None);
        var catalogCommitted = PackageStorePolicy.CatalogsEqual(catalog, journal.DesiredPackages);
        if (journal.Phase is PackageStoreTransactionPhase.CatalogCommitted
            or PackageStoreTransactionPhase.CleanupPending
            or PackageStoreTransactionPhase.Completed
            || catalogCommitted)
        {
            if (!catalogCommitted)
            {
                throw new InvalidDataException(
                    $"Package transaction '{journal.TransactionId}' says its catalog committed, but the authoritative catalog differs.");
            }

            CleanupCommittedTransaction(journal);
            TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
            return;
        }

        await RollBackAsync(journal, journalPath);
    }

    private async Task RollBackAsync(PackageStoreTransactionJournal journal, string journalPath)
    {
        foreach (var action in journal.Actions.Reverse())
        {
            if (action.Kind == PackageStoreJournalActionKind.AddOrReplace)
            {
                TryDeleteDirectory(action.TargetPath);
                if (Directory.Exists(action.BackupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);
                    Directory.Move(action.BackupPath!, action.TargetPath);
                }

                TryDeleteDirectory(action.StagingPath);
            }
            else if (Directory.Exists(action.TombstonePath) && !Directory.Exists(action.TargetPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(action.TargetPath)!);
                Directory.Move(action.TombstonePath!, action.TargetPath);
            }
        }

        await _store.WriteAsync(journal.PreviousPackages, CancellationToken.None);
        journal = journal with { Phase = PackageStoreTransactionPhase.RolledBack };
        await WriteJournalAsync(journalPath, journal);
        TryDeleteDirectory(Path.GetDirectoryName(journalPath)!);
    }

    private void CleanupCommittedTransaction(PackageStoreTransactionJournal journal)
    {
        foreach (var action in journal.Actions)
        {
            TryDeleteDirectory(action.StagingPath);
            TryDeleteDirectory(action.BackupPath);
            TryDeleteDirectory(action.TombstonePath);
        }
    }

    private async Task CollectGarbageAsync(CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateDirectories(_paths.TombstoneRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectory(path);
        }

        foreach (var path in EnumerateDirectories(_paths.StagingRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectory(path);
        }

        var installedPackages = await _store.ListAsync(cancellationToken);
        var authoritativePaths = installedPackages.Select(package => package.InstallPath).ToHashSet(PathComparer);
        foreach (var packageDirectory in EnumerateDirectories(_paths.InstalledRootPath))
        {
            foreach (var versionDirectory in EnumerateDirectories(packageDirectory))
            {
                var canonicalPath = Path.GetFullPath(versionDirectory);
                if (authoritativePaths.Contains(canonicalPath))
                {
                    continue;
                }

                var tombstonePath = Path.Combine(_paths.TombstoneRootPath, "orphan-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.Move(canonicalPath, tombstonePath);
                    TryDeleteDirectory(tombstonePath);
                }
                catch
                {
                    // The next startup retries orphan collection. Catalog records remain authoritative.
                }
            }

            TryDeleteEmptyDirectory(packageDirectory);
        }
    }

    private void ValidateJournal(PackageStoreTransactionJournal journal, string transactionDirectory)
    {
        if (journal.SchemaVersion != 1
            || !string.Equals(transactionDirectory, Path.Combine(_paths.TransactionRootPath, journal.TransactionId), PathComparison))
        {
            throw new InvalidDataException($"Package transaction journal in '{transactionDirectory}' is invalid.");
        }

        _store.ValidateCatalog(journal.PreviousPackages);
        _store.ValidateCatalog(journal.DesiredPackages);
        var previousPaths = journal.PreviousPackages.Select(package => package.InstallPath).ToHashSet(PathComparer);
        var desiredPaths = journal.DesiredPackages.Select(package => package.InstallPath).ToHashSet(PathComparer);
        foreach (var action in journal.Actions)
        {
            var expectedTarget = action.Kind == PackageStoreJournalActionKind.AddOrReplace
                ? desiredPaths.Contains(action.TargetPath)
                : previousPaths.Contains(action.TargetPath);
            if (!expectedTarget
                || action.StagingPath is not null && !IsWithin(action.StagingPath, _paths.StagingRootPath)
                || action.BackupPath is not null && !IsWithin(action.BackupPath, transactionDirectory)
                || action.TombstonePath is not null && !IsWithin(action.TombstonePath, _paths.TombstoneRootPath))
            {
                throw new InvalidDataException($"Package transaction '{journal.TransactionId}' contains an unsafe filesystem path.");
            }
        }
    }

    private static bool IsWithin(string path, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, PathComparison);
    }

    private async Task WriteJournalAsync(string journalPath, PackageStoreTransactionJournal journal)
        => await DurableJsonDocument.WriteAsync(journalPath, journal, InstalledPackageStore.JsonOptions, CancellationToken.None);

    private void Hit(PackageStoreFaultPoint point) => _faultInjector?.Hit(point);

    private static void CleanupPreparedArchives(IEnumerable<PreparedPackageArchiveMutation> archives)
    {
        foreach (var archive in archives)
        {
            SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
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

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Durable journals and authoritative catalog state make cleanup retryable.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
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

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record PendingStoreStage(
        string StageId,
        IReadOnlyList<InstalledPackageRecord> PreviousPackages,
        IReadOnlyList<InstalledPackageRecord> DesiredPackages,
        IReadOnlyList<InstalledPackageRecord> ProspectivePackages,
        IReadOnlyList<PreparedPackageArchiveMutation> PreparedArchives,
        PackageOperationResult Result,
        long BaseCatalogGeneration);
}
