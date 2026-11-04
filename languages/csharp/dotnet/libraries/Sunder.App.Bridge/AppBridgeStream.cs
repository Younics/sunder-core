namespace Sunder.App.Bridge;

// Streams cross the bridge as BCL types; failures during reads and disposal must do so too.
internal sealed class AppBridgeStream(Stream inner, Func<Exception, Exception?> translate, bool leaveOpen = false) : Stream
{
    private T ReadProperty<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    public override bool CanRead => ReadProperty(() => inner.CanRead);
    public override bool CanSeek => ReadProperty(() => inner.CanSeek);
    public override bool CanWrite => ReadProperty(() => inner.CanWrite);
    public override bool CanTimeout => ReadProperty(() => inner.CanTimeout);
    public override long Length => ReadProperty(() => inner.Length);
    public override long Position { get => ReadProperty(() => inner.Position); set => Run(() => inner.Position = value); }
    public override int ReadTimeout { get => ReadProperty(() => inner.ReadTimeout); set => Run(() => inner.ReadTimeout = value); }
    public override int WriteTimeout { get => ReadProperty(() => inner.WriteTimeout); set => Run(() => inner.WriteTimeout = value); }
    public override void Flush() => Run(inner.Flush);
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try { await inner.FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    public override long Seek(long offset, SeekOrigin origin) => ReadProperty(() => inner.Seek(offset, origin));
    public override void SetLength(long value) => Run(() => inner.SetLength(value));
    public override int Read(byte[] buffer, int offset, int count) => ReadProperty(() => inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer)
    {
        try { return inner.Read(buffer); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    public override void Write(byte[] buffer, int offset, int count) => Run(() => inner.Write(buffer, offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try { inner.Write(buffer); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
    }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing && !leaveOpen) Run(inner.Dispose); }
        finally { base.Dispose(disposing); }
    }
    public override async ValueTask DisposeAsync()
    {
        try { if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (translate(error) is { } mapped) { throw mapped; }
        finally { GC.SuppressFinalize(this); }
    }
}
