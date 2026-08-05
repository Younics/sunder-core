using System.Threading.Channels;

namespace Sunder.Sdk.Worker.Tests.TestSupport;

internal sealed class AsyncByteStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private byte[]? _current;
    private int _offset;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (_current is null || _offset == _current.Length)
        {
            _current = null;
            _offset = 0;
            while (await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_chunks.Reader.TryRead(out _current)) break;
            }
            if (_current is null) return 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_chunks.Writer.TryWrite(buffer.ToArray()))
        {
            throw new IOException("The in-memory byte stream is closed.");
        }
        return ValueTask.CompletedTask;
    }

    public void CompleteWriting() => _chunks.Writer.TryComplete();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _chunks.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }
}
