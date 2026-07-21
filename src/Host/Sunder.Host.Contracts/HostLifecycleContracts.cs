namespace Sunder.Host.Contracts;

public enum HostRuntimeDesiredState
{
    Stopped = 0,
    Running = 1,
}

public enum HostRuntimeState
{
    Stopped = 0,
    Starting = 1,
    Ready = 2,
    Stopping = 3,
    Restarting = 4,
    Failed = 5,
    CrashLoop = 6,
}

public enum HostOperationState
{
    Accepted = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
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
    Guid? RuntimeInstanceId,
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

public sealed record HostLifecycleSubmission(HostOperationDescriptor Operation);
