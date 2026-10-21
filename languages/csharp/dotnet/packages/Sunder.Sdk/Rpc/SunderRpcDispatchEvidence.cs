using System.Runtime.CompilerServices;

namespace Sunder.Sdk.Rpc;

// Genuine transport metadata remains meaningful for existing App and Worker
// clients. Only the App transport additionally issues the new observation proof.
// Neither marker can be supplied by a package's public client/scope implementation.
internal interface ISunderRpcTrustedDispatchMetadataScope { }
internal interface ISunderRpcTrustedTransportScope : ISunderRpcTrustedDispatchMetadataScope { }

internal interface ISunderRpcGeneratedOperationScope
{
    ValueTask EnsureCurrentAsync(SunderRpcProviderSnapshot provider, CancellationToken token);
}

internal sealed class SunderRpcDispatchOperation
{
    private int _epoch;
    private int _unary = 1;
    internal int DispatchEpoch => Volatile.Read(ref _epoch);
    internal bool IsUnary => Volatile.Read(ref _unary) != 0;
    internal int BeginInvoke() => Interlocked.Increment(ref _epoch);
    internal void BeginStream() => Volatile.Write(ref _unary, 0);
}

internal static class SunderRpcDispatchEvidence
{
    private sealed record Proof(SunderRpcDispatchOperation Operation, int Epoch, SunderRpcException Cause);
    private static readonly ConditionalWeakTable<Exception, Proof> Proofs = new();

    private static bool Eligible(SunderRpcException error)
        => Enum.IsDefined(error.Error.Kind) && error.Error.Kind != SunderRpcErrorKind.Domain
            && error.Error.DispatchState == SunderRpcDispatchState.NotDispatched
            && error.Error.RetrySafety == SunderRpcRetrySafety.Safe;

    internal static bool HasProof(Exception? error)
        => error is not null && Proofs.TryGetValue(error, out var proof) && Eligible(proof.Cause)
            && proof.Operation.IsUnary && proof.Operation.DispatchEpoch == proof.Epoch
            && (error is not SunderRpcException rpc || Eligible(rpc));

    internal static SunderRpcException? Cause(Exception error)
        => HasProof(error) && Proofs.TryGetValue(error, out var proof) ? proof.Cause : null;

    internal static bool IsFor(Exception error, SunderRpcDispatchOperation operation)
        => HasProof(error) && Proofs.TryGetValue(error, out var proof) && ReferenceEquals(proof.Operation, operation);

    internal static SunderRpcException Issue(SunderRpcException error, SunderRpcDispatchOperation operation, int expectedEpoch = 0)
    {
        // Copy, never elevate an exception object shared with another operation.
        var result = new SunderRpcException(error.Error, error.IsHostAuthenticated);
        if (Eligible(result)) Proofs.Add(result, new(operation, expectedEpoch, result));
        return result;
    }

    internal static void Copy(Exception source, Exception destination)
    {
        if (ReferenceEquals(source, destination) || !HasProof(source) || destination is SunderRpcException rpc && !Eligible(rpc)) return;
        if (Proofs.TryGetValue(source, out var proof)) Proofs.Add(destination,
            destination is SunderRpcException cause ? new(proof.Operation, proof.Epoch, cause) : proof);
    }

    internal static void ThrowIfCancelled(CancellationToken token, SunderRpcDispatchOperation operation)
    {
        if (!token.IsCancellationRequested) return;
        var cause = Issue(new(new(SunderRpcErrorKind.Cancelled, "rpc.call.cancelled",
            "The SDK rejected this operation before service dispatch.")), operation);
        var cancelled = new OperationCanceledException(cause.Message, cause, token);
        Copy(cause, cancelled);
        throw cancelled;
    }

    internal static Exception WithoutProof(Exception error)
    {
        if (!HasProof(error)) return error;
        if (error is SunderRpcException rpc) return new SunderRpcException(rpc.Error, rpc.IsHostAuthenticated);
        if (error is OperationCanceledException cancelled)
            return new OperationCanceledException(cancelled.Message, cancelled.InnerException, cancelled.CancellationToken);
        return error;
    }

    internal static Exception AfterDispatch(Exception error)
    {
        if (error is SunderRpcException rpc)
            return rpc.WithDispatchMetadata(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe);
        if (error is OperationCanceledException cancelled && Cause(error) is { } cause)
            return new OperationCanceledException(cancelled.Message,
                cause.WithDispatchMetadata(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe), cancelled.CancellationToken);
        return error;
    }
}
