namespace Sunder.Runtime.Contracts;

/// <summary>An opaque observation reference to one exact owned Worker execution.</summary>
/// <remarks>The instance identifies the original owning Host, not necessarily the responding Host.</remarks>
public sealed record RuntimeWorkerExecutionReference(Guid RuntimeInstanceId, string Value);

public enum RuntimeWorkerExecutionState
{
    Unknown = 0,
    CleanupPending = 1,
    CleanupConfirmed = 2,
}

/// <summary>Reports owning containment cleanup only, never a package job outcome.</summary>
public sealed record RuntimeWorkerExecutionObservation(
    RuntimeWorkerExecutionReference Reference,
    RuntimeWorkerExecutionState State)
{
    public DateTimeOffset? ConfirmedAtUtc { get; init; }
}

public sealed record RuntimeRpcAppExecutionCaptureRequest(
    string SessionId,
    RuntimeRpcProviderDescriptor Provider,
    string? CallScopeId = null);

public sealed record RuntimeRpcAppExecutionObserveRequest(
    string SessionId,
    RuntimeWorkerExecutionReference Reference,
    string? CallScopeId = null);

public sealed record RuntimeRpcAppExecutionCaptureResponse(
    RuntimeWorkerExecutionReference? Reference,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppExecutionObserveResponse(
    RuntimeWorkerExecutionObservation? Observation,
    RuntimeRpcErrorDescriptor? Error);
