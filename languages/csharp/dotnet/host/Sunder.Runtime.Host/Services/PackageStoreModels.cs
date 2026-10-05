using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal abstract record PackageStoreChange;

internal sealed record PackageStoreMutation(
    PackageStoreMutationKind Kind,
    string? PackageId = null,
    string? ArchiveFilePath = null,
    bool AllowDowngrade = false,
    bool Reinstall = false,
    InstalledPackageProvenanceRecord? Provenance = null,
    bool AllowCascade = false,
    string? ConfirmationToken = null,
    PackageCapabilityConsent? CapabilityConsent = null,
    string? ConsentManifestSha256 = null,
    IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>? ExecutionRuntimeBindings = null)
    : PackageStoreChange
{
    internal Sunder.Registry.Contracts.RegistryPackageResolutionPolicy? ResolutionPolicy { get; init; }
    internal IReadOnlyList<Sunder.Package.Format.SunderPackageTargetKey>? RegistryProjectionTargets { get; init; }
}

internal sealed record PackageExecutionRuntimeRebindMutation(
    string PackageId,
    InstalledPackageRecord ExpectedPackage,
    IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> DesiredBindings)
    : PackageStoreChange;

internal sealed record PackageExecutionRuntimePolicyUpdateMutation(
    string PackageId,
    InstalledPackageRecord ExpectedPackage,
    IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> DesiredBindings)
    : PackageStoreChange;

internal sealed record PackageLifecyclePolicyUpdateMutation(
    string PackageId,
    InstalledPackageRecord ExpectedPackage,
    InstalledPackagePolicyRecord? DesiredPolicy) : PackageStoreChange;

internal sealed record RegistryPackageUploadMetadata(
    InstalledPackageProvenanceRecord Provenance,
    string ConsentManifestSha256,
    IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> ExecutionRuntimeBindings)
{
    internal Sunder.Registry.Contracts.RegistryPackageResolutionPolicy? ResolutionPolicy { get; init; }
    internal IReadOnlyList<Sunder.Package.Format.SunderPackageTargetKey>? AcquiredTargets { get; init; }
}

internal sealed record LocalExecutionRuntimePolicyAuthority(
    long Generation,
    IReadOnlyList<ExecutionRuntimeIdentity> InstallationIdentities,
    IReadOnlyList<ExecutionRuntimeIdentity> ActivationIdentities);

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

internal enum PackageStoreCutoverPhase
{
    Prepared,
    PreLaunchPolicyConfirmed,
    CandidateReady,
    PreCommitPolicyConfirmed,
    DurableCommitStarted,
    DurableCommitted,
    GenerationCommitAcknowledged,
    OldGenerationStopped,
    Completed,
}

internal enum PackageStoreCommitDisposition
{
    Committed,
    RolledBack,
    Indeterminate,
}

internal sealed record PackageStoreCommitResult(
    PackageOperationResult OperationResult,
    PackageStoreCommitDisposition Disposition);

internal enum PackageStoreFaultPoint
{
    CutoverPrepared,
    JournalPrepared,
    DurableCommitStarted,
    PayloadsCommitting,
    DirectoryMoveBefore,
    DirectoryMoveAfter,
    PayloadMoved,
    PayloadsCommitted,
    CatalogReplacing,
    CatalogReplaced,
    PolicyReconciling,
    PolicyReconciled,
    CatalogCommitRecording,
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
    long BaseCatalogGeneration,
    LocalExecutionRuntimePolicyAuthority? LocalRuntimePolicyAuthority = null,
    ExecutionRuntimeLifecyclePolicySnapshot? RegistryRuntimePolicyAuthority = null);

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

internal enum PackageStoreStorageSnapshotKind
{
    Data,
    Workspace,
    Files,
}

internal sealed record PackageStoreStorageSnapshot(
    string PackageId,
    PackageStoreStorageSnapshotKind Kind,
    string TargetPath,
    string SnapshotPath,
    bool Existed);

internal sealed record PackageStoreTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    PackageStoreCutoverPhase CutoverPhase,
    bool RequiresRuntimePublication,
    PackageStoreTransactionPhase Phase,
    IReadOnlyList<InstalledPackageRecord> PreviousPackages,
    IReadOnlyList<InstalledPackageRecord> DesiredPackages,
    IReadOnlyList<PackageStoreJournalAction> Actions,
    IReadOnlyList<PackageStoreStorageSnapshot> StorageSnapshots,
    IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> RuntimePolicyUpdates = null!);

internal sealed record PendingStoreStage(
    string StageId,
    IReadOnlyList<InstalledPackageRecord> PreviousPackages,
    IReadOnlyList<InstalledPackageRecord> DesiredPackages,
    IReadOnlyList<InstalledPackageRecord> ProspectivePackages,
    IReadOnlyList<PreparedPackageArchiveMutation> PreparedArchives,
    PackageOperationResult Result,
    long BaseCatalogGeneration,
    IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> RuntimePolicyUpdates,
    ExecutionRuntimeLifecyclePolicySnapshot? RegistryRuntimePolicyAuthority = null,
    LocalExecutionRuntimeBindingSnapshot? LocalBindingAuthority = null,
    LocalExecutionRuntimePolicyAuthority? LocalRuntimePolicyAuthority = null,
    IReadOnlyList<InstalledPackagePolicyRecord>? PackagePolicyAuthority = null);
