namespace Sunder.Sdk.Services;

internal sealed class SunderContentReadStream(Stream inner, CancellationTokenSource cancellation,
    Action<SunderContentReadStream> removed, Func<ValueTask>? release = null) : Stream
{
    private readonly object _gate = new();
    private readonly CancellationToken _token = cancellation.Token;
    private CancellationTokenRegistration _registration;
    private Task? _dispose;
    private bool _cancelled;

    internal void ObserveCancellation()
    {
        var registration = _token.UnsafeRegister(static state => ((SunderContentReadStream)state!).Cancel(), this);
        lock (_gate)
        {
            if (_dispose is null) { _registration = registration; return; }
        }
        registration.Unregister();
    }

    private async void Cancel()
    {
        // The disposal task remains observable by DisposeAsync. Cancellation callbacks cannot await it.
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    public override bool CanRead => _dispose is null && inner.CanRead;
    public override bool CanSeek => _dispose is null && inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { Check(); return inner.Length; } }
    public override long Position { get { Check(); return inner.Position; } set { Check(); inner.Position = value; } }
    public override void Flush() { Check(); }
    public override int Read(byte[] buffer, int offset, int count) { Check(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { Check(); return inner.Read(buffer); }
    public override int ReadByte() { Check(); return inner.ReadByte(); }
    public override long Seek(long offset, SeekOrigin origin) { Check(); return inner.Seek(offset, origin); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        try { return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false); }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            throw new OperationCanceledException(linked.Token);
        }
    }

    private void Check()
    {
        lock (_gate)
        {
            if (_dispose is not null && !_cancelled) throw new ObjectDisposedException(nameof(SunderContentReadStream));
            _token.ThrowIfCancellationRequested();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _cancelled = _token.IsCancellationRequested;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                _registration.Unregister();
                try
                {
                    if (!cancellation.IsCancellationRequested) await cancellation.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (release is null) await inner.DisposeAsync().ConfigureAwait(false);
                    else await release().ConfigureAwait(false);
                }
            }
            finally { cancellation.Dispose(); removed(this); }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
