namespace Sunder.Runtime.Contracts;

public enum RuntimeEventKind
{
    Snapshot = 0,
    SessionGenerationChanged = 1,
    OperationPhaseChanged = 2,
    DevReloadCompleted = 3,
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
    long SequenceId,
    DateTimeOffset Timestamp,
    RuntimeEventKind Kind,
    long SessionGeneration,
    RuntimeOperationPhase OperationPhase,
    IReadOnlyList<string> PackageIds,
    bool? Success = null,
    string? Message = null);

public sealed record RuntimeEventSnapshot(
    long SequenceId,
    long SessionGeneration,
    RuntimeOperationPhase OperationPhase,
    IReadOnlyList<string> ActivePackageIds,
    IReadOnlyList<RuntimeEventDescriptor> Events,
    bool HistoryGap);

public sealed record DevPackageWatchIntentRequest(bool Enabled);

public sealed record DevPackageWatchStatus(
    bool Enabled,
    long SessionGeneration,
    IReadOnlyList<string> PackageIds);
