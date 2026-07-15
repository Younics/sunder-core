namespace Sunder.Runtime.Contracts;

public enum RuntimeEventKind
{
    Snapshot = 0,
    SessionGenerationChanged = 1,
    OperationPhaseChanged = 2,
    DevReloadCompleted = 3,
    BootstrapStateChanged = 4,
    SnapshotDiagnosticsChanged = 5,
}

public enum RuntimeOperationPhase
{
    Idle = 0,
    DevReloadDebounce = 1,
    DevReloadStabilityCheck = 2,
    DevReloading = 3,
    PackageOperation = 4,
    ShuttingDown = 5,
}

public sealed record RuntimeEventDescriptor(
    Guid RuntimeInstanceId,
    long SequenceId,
    DateTimeOffset Timestamp,
    RuntimeEventKind Kind,
    long SessionGeneration,
    RuntimeOperationPhase OperationPhase,
    RuntimeBootstrapState BootstrapState,
    IReadOnlyList<string> PackageIds,
    bool? Success = null,
    string? Message = null)
{
    public IReadOnlyList<string> PackageIds { get; } = RuntimeContractCollections.Freeze(PackageIds);
}

public sealed record RuntimeEventSnapshot(
    Guid RuntimeInstanceId,
    long SequenceId,
    long SessionGeneration,
    RuntimeOperationPhase OperationPhase,
    RuntimeBootstrapState BootstrapState,
    IReadOnlyList<string> ActivePackageIds,
    IReadOnlyList<RuntimeEventDescriptor> Events,
    bool HistoryGap)
{
    public IReadOnlyList<string> ActivePackageIds { get; }
        = RuntimeContractCollections.Freeze(ActivePackageIds);

    public IReadOnlyList<RuntimeEventDescriptor> Events { get; }
        = RuntimeContractCollections.Freeze(Events);
}
