using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeObservationCaptureV1(
    int SchemaVersion,
    RuntimeWorkerExecutionReference Reference,
    ExecutionRuntimeProcessLeaseV1 Execution,
    DateTimeOffset CapturedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed record ExecutionRuntimeObservationCleanupV1(
    int SchemaVersion,
    RuntimeWorkerExecutionReference Reference,
    ExecutionRuntimeProcessLeaseV1 Execution,
    string ProofKind,
    DateTimeOffset ConfirmedAtUtc,
    Guid ConfirmingHostInstanceId)
{
    public const int CurrentSchemaVersion = 1;
    public const string ContainmentEmptyProof = "owned-containment-empty.v1";
}

// A narrow durable-IO dependency for the new observation documents. The owning registry
// still owns synchronization, identity validation, capacity and release decisions.
internal interface IExecutionRuntimeObservationDocuments
{
    Task WriteCaptureAsync(string path, ExecutionRuntimeObservationCaptureV1 capture, CancellationToken token);
    Task WriteCleanupAsync(string path, ExecutionRuntimeObservationCleanupV1 cleanup, CancellationToken token);
}

internal sealed class DurableExecutionRuntimeObservationDocuments : IExecutionRuntimeObservationDocuments
{
    public Task WriteCaptureAsync(string path, ExecutionRuntimeObservationCaptureV1 capture, CancellationToken token)
        => DurableJsonDocument.WriteAsync(path, capture, ExecutionRuntimeStore.JsonOptions, token);

    public Task WriteCleanupAsync(string path, ExecutionRuntimeObservationCleanupV1 cleanup, CancellationToken token)
        => DurableJsonDocument.WriteAsync(path, cleanup, ExecutionRuntimeStore.JsonOptions, token);
}

internal static class ExecutionRuntimeObservationReferences
{
    private const string Prefix = "we1_";
    private const int Length = 4 + 32 + 1 + 64;

    internal static RuntimeWorkerExecutionReference Create(ExecutionRuntimeProcessLeaseV1 execution)
        => new(execution.HostInstanceId,
            Prefix + execution.LeaseId + "_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    internal static bool TryGetLeaseId(RuntimeWorkerExecutionReference? reference, out string leaseId)
    {
        leaseId = string.Empty;
        if (reference is not { RuntimeInstanceId: var instance, Value: { } value }
            || instance == Guid.Empty || value.Length != Length
            || !value.StartsWith(Prefix, StringComparison.Ordinal) || value[36] != '_'
            || !value.AsSpan(4, 32).ToString().All(IsLowerHex)
            || !Guid.TryParseExact(value.AsSpan(4, 32), "N", out var id) || id == Guid.Empty
            || !value.AsSpan(37).ToString().All(IsLowerHex)) return false;
        leaseId = value.Substring(4, 32);
        return true;
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

internal sealed class ExecutionRuntimeObservationCorruptException(string message, Exception? inner = null)
    : Exception(message, inner);

internal sealed class ExecutionRuntimeObservationCapacityException()
    : Exception("Worker execution observation retention is full. Existing observations remain retained; no new execution reference was issued.");

internal sealed class ExecutionRuntimeObservationOwnerException()
    : Exception("The execution observation is not owned by this App package.");
