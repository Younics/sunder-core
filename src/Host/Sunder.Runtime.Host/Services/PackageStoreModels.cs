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
    CatalogCommitted,
    CleanupPending,
    Completed,
    RolledBack,
}

internal enum PackageStoreFaultPoint
{
    JournalPrepared,
    PayloadsCommitting,
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

internal sealed record PackageStoreJournalAction(
    PackageStoreJournalActionKind Kind,
    string? StagingPath,
    string TargetPath,
    string? BackupPath,
    string? TombstonePath);

internal sealed record PackageStoreTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    PackageStoreTransactionPhase Phase,
    IReadOnlyList<InstalledPackageRecord> PreviousPackages,
    IReadOnlyList<InstalledPackageRecord> DesiredPackages,
    IReadOnlyList<PackageStoreJournalAction> Actions);
