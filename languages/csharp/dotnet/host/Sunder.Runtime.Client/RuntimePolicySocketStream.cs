using System.Net.Sockets;

namespace Sunder.Runtime.Client;

// Synchronous caller, nonblocking socket: each wait consumes the same absolute
// exchange budget, without pool callbacks or mutable SO_RCVTIMEO/SO_SNDTIMEO.
// macOS can reject timeout-option changes after a Unix peer closes even while
// its complete response remains readable. Never discard that buffered proof or
// ignore a socket error to work around the option-setting failure.
internal sealed class RuntimePolicySocketStream : Stream
{
    private readonly Socket _socket;
    private readonly long _started;
    private readonly CancellationToken _token;
    private int _disposed;

    internal RuntimePolicySocketStream(Socket socket, long started, CancellationToken token)
    {
        if (socket.Blocking) throw new ArgumentException("Policy sockets must remain nonblocking.", nameof(socket));
        _socket = socket;
        _started = started;
        _token = token;
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanWrite => CanRead;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private int Remaining()
    {
        _token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return RuntimePolicyHttpMessageHandler.RemainingMilliseconds(_started, _token);
    }

    public override int Read(Span<byte> buffer)
    {
        _ = Remaining();
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            _ = Remaining();
            try
            {
                var received = _socket.Receive(buffer, SocketFlags.None);
                _ = Remaining();
                return received;
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.Interrupted)
            { Wait(SelectMode.SelectRead); }
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _ = Remaining();
        while (!buffer.IsEmpty)
        {
            _ = Remaining();
            try
            {
                var sent = _socket.Send(buffer, SocketFlags.None);
                if (sent == 0) throw new EndOfStreamException("Local policy connection closed before the request completed.");
                buffer = buffer[sent..];
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.Interrupted)
            { Wait(SelectMode.SelectWrite); }
        }
        _ = Remaining();
    }

    private void Wait(SelectMode mode)
    {
        while (true)
        {
            var remaining = Remaining();
            try
            {
                if (_socket.Poll(checked(remaining * 1000), mode)) return;
                _token.ThrowIfCancellationRequested();
                throw new TimeoutException("Local policy exchange exceeded its deadline.");
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.Interrupted) { }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Flush() => _ = Remaining();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) _socket.Dispose();
        base.Dispose(disposing);
    }
}
