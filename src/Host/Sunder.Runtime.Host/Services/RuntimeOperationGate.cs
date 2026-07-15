namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeOperationGate : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _syncRoot = new();
    private bool _stopping;
    private bool _stopped;
    private readonly TaskCompletionSource _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        var stopping = false;
        lock (_syncRoot)
        {
            if (_stopping)
            {
                stopping = true;
            }
        }
        if (stopping)
        {
            _gate.Release();
            linkedCancellation.Dispose();
            throw new InvalidOperationException("The Runtime is shutting down and is not accepting new operations.");
        }

        _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.PackageOperation);
        return new Lease(this, linkedCancellation);
    }

    public async Task ShutdownAsync(Func<Task> cleanup)
    {
        Task? existingShutdown = null;
        lock (_syncRoot)
        {
            if (_stopped)
            {
                return;
            }
            if (_stopping)
            {
                existingShutdown = _shutdownCompletion.Task;
            }
            else
            {
                _stopping = true;
            }
        }

        if (existingShutdown is not null)
        {
            await existingShutdown;
            return;
        }

        _shutdown.Cancel();
        _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.ShuttingDown);
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await cleanup();
        }
        finally
        {
            lock (_syncRoot)
            {
                _stopped = true;
            }
            _shutdownCompletion.TrySetResult();
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
        bool stopping;
        lock (_syncRoot)
        {
            stopping = _stopping;
        }
        if (!stopping)
        {
            _eventStream?.PublishOperationPhase(Sunder.Runtime.Contracts.RuntimeOperationPhase.Idle);
        }
        cancellation.Dispose();
        _gate.Release();
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
    }
}
