namespace Sunder.App.Services;

internal sealed class AppPackageCleanupContentStream(Stream stream, AppPackageRpcCleanupAuthority authority,
    Action<CancellationToken> requireScope) : Stream
{
    private readonly object _disposeGate = new();
    private Task? _disposal;
    private int _disposed;
    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => stream.CanWrite;
    public override long Length { get { RequireAccess(default); return stream.Length; } }
    public override long Position
    {
        get { RequireAccess(default); return stream.Position; }
        set { RequireAccess(default); stream.Position = value; RequireAccess(default); }
    }

    private void RequireAccess(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        requireScope(token);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        RequireAccess(default);
        var count = stream.Read(buffer);
        RequireAccess(default);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        RequireAccess(cancellationToken);
        using var linked = authority.BeginOperation(cancellationToken);
        var count = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        RequireAccess(cancellationToken);
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireAccess(default);
        stream.Write(buffer);
        RequireAccess(default);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        RequireAccess(cancellationToken);
        using var linked = authority.BeginOperation(cancellationToken);
        await stream.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
        RequireAccess(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        RequireAccess(default);
        var position = stream.Seek(offset, origin);
        RequireAccess(default);
        return position;
    }
    public override void SetLength(long value) { RequireAccess(default); stream.SetLength(value); RequireAccess(default); }
    public override void Flush() { RequireAccess(default); stream.Flush(); RequireAccess(default); }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        RequireAccess(cancellationToken);
        using var linked = authority.BeginOperation(cancellationToken);
        await stream.FlushAsync(linked.Token).ConfigureAwait(false);
        RequireAccess(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            Interlocked.Exchange(ref _disposed, 1);
            GC.SuppressFinalize(this);
            return new(_disposal ??= CloseAsync());
        }
    }
    private async Task CloseAsync() => await stream.DisposeAsync().ConfigureAwait(false);
}
