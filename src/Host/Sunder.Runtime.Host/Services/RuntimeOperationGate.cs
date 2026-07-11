namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeOperationGate : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _legacyCancellation;
    private bool _stopping;
    private bool _stopped;
    private readonly RuntimeEventStreamService? _eventStream;

    public RuntimeOperationGate(RuntimeEventStreamService? eventStream = null)
    {
        _eventStream = eventStream;
    }

    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("The Runtime is shutting down and is not accepting new operations.");
            }
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _gate.WaitAsync(linkedCancellation.Token);
        }
        catch
        {
            linkedCancellation.Dispose();
            throw;
        }

        lock (_syncRoot)
        {
            if (_stopping)
            {
                _gate.Release();
                linkedCancellation.Dispose();
                throw new InvalidOperationException("The Runtime is shutting down and is not accepting new operations.");
            }
        }

        _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.PackageOperation);
        return new Lease(this, linkedCancellation);
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var lease = await EnterAsync(cancellationToken);
        lock (_syncRoot)
        {
            _legacyCancellation = lease.DetachCancellation();
        }
    }

    public void Release()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            cancellation = _legacyCancellation;
            _legacyCancellation = null;
        }

        cancellation?.Dispose();
        _gate.Release();
        _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.Idle);
    }

    public async Task ShutdownAsync(Func<Task> cleanup)
    {
        lock (_syncRoot)
        {
            if (_stopped)
            {
                return;
            }

            _stopping = true;
            _shutdown.Cancel();
            _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.ShuttingDown);
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await cleanup();
            lock (_syncRoot)
            {
                _stopped = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(static () => Task.CompletedTask);
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private void ReleaseLease(CancellationTokenSource cancellation)
    {
        _gate.Release();
        cancellation.Dispose();
        _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.Idle);
    }

    internal sealed class Lease : IAsyncDisposable
    {
        private RuntimeOperationGate? _owner;
        private readonly CancellationTokenSource _cancellation;

        internal Lease(RuntimeOperationGate owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        public CancellationToken CancellationToken => _cancellation.Token;

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                owner.ReleaseLease(_cancellation);
            }

            return ValueTask.CompletedTask;
        }

        internal CancellationTokenSource DetachCancellation()
        {
            _owner = null;
            return _cancellation;
        }
    }
}
