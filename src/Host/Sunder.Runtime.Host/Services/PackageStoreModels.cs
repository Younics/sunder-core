using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackageStoreMutation(
    PackageStoreMutationKind Kind,
    string? PackageId = null,
    string? ArchiveFilePath = null,
    bool AllowDowngrade = false,
    bool Reinstall = false);

internal enum PackageStoreTransactionPhase
{
    Prepared,
    PayloadsCommitting,
    PayloadsCommitted,
    CatalogCommitting,
    CatalogCommitted,
    CleanupPending,
    Completed,
    RolledBack,
}

internal enum PackageStoreFaultPoint
{
    JournalPrepared,
    PayloadsCommitting,
    DirectoryMoveBefore,
    DirectoryMoveAfter,
    PayloadMoved,
    PayloadsCommitted,
    CatalogReplacing,
    CatalogReplaced,
    CatalogCommitted,
    Cleanup,
}

internal interface IPackageStoreFaultInjector
{
    void Hit(PackageStoreFaultPoint point);
}

internal sealed class PackageStoreSimulatedCrashException(PackageStoreFaultPoint point)
    : Exception($"Simulated package store crash at {point}.")
{
    public PackageStoreFaultPoint Point { get; } = point;
}

internal sealed record PackageStorePreparedStage(
    string StageId,
    IReadOnlyList<InstalledPackageRecord> ProspectivePackages,
    IReadOnlyDictionary<string, string> PreparationSourcePaths,
    PackageOperationResult Result,
    long BaseCatalogGeneration);

internal sealed record PackageStoreStagePreparation(
    PackageStorePreparedStage? Stage,
    PackageOperationResult? Failure)
{
    public bool Success => Stage is not null && Failure is null;
}

internal enum PackageStoreJournalActionKind
{
    AddOrReplace,
    Remove,
}

internal enum PackageStoreJournalActionProgress
{
    Pending,
    BackupMoveStarted,
    BackupCreated,
    OriginalAbsent,
    TargetMoveStarted,
    TargetInstalled,
    RemovalMoveStarted,
    Removed,
    CleanupStarted,
    Cleaned,
}

internal enum PackageStoreJournalRollbackProgress
{
    None,
    NewTargetMoveStarted,
    NewTargetMoved,
    OriginalRestoreMoveStarted,
    OriginalRestored,
    Completed,
}

internal sealed record PackageStoreJournalAction(
    PackageStoreJournalActionKind Kind,
    string? StagingPath,
    string TargetPath,
    string? BackupPath,
    string? TombstonePath,
    PackageStoreJournalActionProgress Progress = PackageStoreJournalActionProgress.Pending,
    PackageStoreJournalRollbackProgress RollbackProgress = PackageStoreJournalRollbackProgress.None);

internal sealed record PackageStoreTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    PackageStoreTransactionPhase Phase,
    IReadOnlyList<InstalledPackageRecord> PreviousPackages,
    IReadOnlyList<InstalledPackageRecord> DesiredPackages,
    IReadOnlyList<PackageStoreJournalAction> Actions);

internal sealed record PendingStoreStage(
    string StageId,
    IReadOnlyList<InstalledPackageRecord> PreviousPackages,
    IReadOnlyList<InstalledPackageRecord> DesiredPackages,
    IReadOnlyList<InstalledPackageRecord> ProspectivePackages,
    IReadOnlyList<PreparedPackageArchiveMutation> PreparedArchives,
    PackageOperationResult Result,
    long BaseCatalogGeneration);
