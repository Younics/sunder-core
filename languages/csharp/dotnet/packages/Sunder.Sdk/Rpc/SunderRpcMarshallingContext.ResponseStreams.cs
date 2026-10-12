using System.Text.Json;

namespace Sunder.Sdk.Rpc;

public sealed partial class SunderRpcMarshallingContext
{
    private object? _responseStreamAttempt;

    /// <summary>Serializes an opted-in provider stream item, returning no item when this invocation's response-registration authority retires.</summary>
    /// <remarks>For generated provider adapters only. A null result means stream completion, not a JSON null value. Only authenticated, safe, not-dispatched retirement of the current registration attempt qualifies; unrelated failures and cancellation propagate.</remarks>
    public ValueTask<JsonElement?> SerializeResponseStreamItemAsync<T>(T value,
        Func<SunderRpcMarshallingContext, T, CancellationToken, ValueTask>? register = null,
        CancellationToken cancellationToken = default)
    {
        if (_invocation is null) throw new InvalidOperationException("Response stream completion requires invocation authority.");
        return SerializeCoreAsync(value, register, cancellationToken, completeOnRetirement: true);
    }

    private async ValueTask<SunderRpcContentReference> RegisterResponseContentAsync(Stream source,
        SunderRpcContentRegistrationOptions options, CancellationToken token)
    {
        var attempt = _responseStreamAttempt;
        if (attempt is null) return await _invocation!.RegisterContentAsync(source, options, token).ConfigureAwait(false);
        // A source stream can throw a previously obtained authenticated exception.
        // It is still a source-read failure, not a response-authority decision.
        using var observed = new RegistrationReadStream(source);
        try { return await _invocation!.RegisterContentAsync(observed, options, token).ConfigureAwait(false); }
        catch (SunderRpcException error) when (!observed.Failed && IsResponseRetirement(error))
        {
            token.ThrowIfCancellationRequested();
            throw new ResponseRegistrationRetired(attempt, error);
        }
    }

    private static bool IsResponseRetirement(SunderRpcException error)
        => error.IsHostAuthenticated
           && error.Error.DispatchState == SunderRpcDispatchState.NotDispatched
           && error.Error.RetrySafety == SunderRpcRetrySafety.Safe
           && (error.Error.Kind, error.Error.Code) is
               (SunderRpcErrorKind.Unavailable, "rpc.content.authority-revoked") or
               (SunderRpcErrorKind.DeadlineExceeded, "rpc.call.deadline-exceeded") or
               (SunderRpcErrorKind.StaleEndpoint, "rpc.endpoint.stale") or
               (SunderRpcErrorKind.Cancelled, "rpc.call.cancelled");

    private sealed class ResponseRegistrationRetired(object attempt, SunderRpcException cause) : Exception(cause.Message, cause)
    {
        internal object Attempt { get; } = attempt;
        internal SunderRpcException Cause { get; } = cause;
    }

    // Borrowed for the registration call only; the outer content lease owns disposal.
    private sealed class RegistrationReadStream(Stream source) : Stream
    {
        internal bool Failed { get; private set; }
        private T Observe<T>(Func<T> read) { try { return read(); } catch { Failed = true; throw; } }
        public override bool CanRead => Observe(() => source.CanRead);
        public override bool CanSeek => Observe(() => source.CanSeek);
        public override bool CanWrite => false;
        public override long Length => Observe(() => source.Length);
        public override long Position { get => Observe(() => source.Position); set => Observe(() => { source.Position = value; return true; }); }
        public override int Read(byte[] buffer, int offset, int count) => Observe(() => source.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) { try { return source.Read(buffer); } catch { Failed = true; throw; } }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { try { return await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); } catch { Failed = true; throw; } }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { try { return await source.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false); } catch { Failed = true; throw; } }
        public override long Seek(long offset, SeekOrigin origin) => Observe(() => source.Seek(offset, origin));
        public override void Flush() => Observe(() => { source.Flush(); return true; });
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
