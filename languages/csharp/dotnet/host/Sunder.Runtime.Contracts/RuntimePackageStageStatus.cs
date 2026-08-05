namespace Sunder.Runtime.Contracts;

public enum RuntimePackageStageKind
{
    PackageSession = 0,
    PackageStore = 1,
}

public enum RuntimePackageStageState
{
    Pending = 0,
    Committing = 1,
    Committed = 2,
    Discarded = 3,
    Failed = 4,
}

public sealed record RuntimePackageStageStatus(
    string StageId,
    RuntimePackageStageKind Kind,
    RuntimePackageStageState State,
    DateTimeOffset UpdatedAtUtc,
    RuntimePackageStamp? CommittedStamp,
    bool RuntimeSessionApplied,
    bool ReconciliationPending,
    string? Message)
{
    public DateTimeOffset CreatedAtUtc { get; init; } = UpdatedAtUtc;

    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
