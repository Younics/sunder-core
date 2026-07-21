namespace Sunder.Host.Contracts;

public enum HostRuntimeDesiredState
{
    Stopped = 0,
    Running = 1,
    Maintenance = 2,
}

public enum HostRuntimeState
{
    Stopped = 0,
    Starting = 1,
    Ready = 2,
    Draining = 3,
    Stopping = 4,
    Restarting = 5,
    Updating = 6,
    RollingBack = 7,
    Failed = 8,
    CrashLoop = 9,
}

public enum HostOperationState
{
    Accepted = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    RolledBack = 4,
    Cancelled = 5,
}

public static class HostOperationKinds
{
    public const string RuntimeStart = "runtime.start";
    public const string RuntimeStop = "runtime.stop";
    public const string RuntimeRestart = "runtime.restart";
}

public sealed record HostRuntimeStatus(
    HostRuntimeDesiredState DesiredState,
    HostRuntimeState State,
    long DeploymentGeneration,
    string? ActiveVersion,
    string? PreviousVersion,
    Guid? RuntimeInstanceId,
    DateTimeOffset? RuntimeStartedAtUtc,
    string? FailureCode,
    string? FailureMessage,
    string? ActiveOperationId);

public sealed record HostLifecycleRequest(
    Guid MutationId,
    long ExpectedDeploymentGeneration);

public sealed record HostOperationDescriptor(
    string OperationId,
    Guid MutationId,
    string Kind,
    long ExpectedDeploymentGeneration,
    HostOperationState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Message,
    string? FailureCode);

public sealed record HostLifecycleSubmission(
    HostOperationDescriptor Operation,
    HostRuntimeStatus Status);
