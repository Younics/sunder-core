namespace Sunder.App.Services;

public sealed class LatestAsyncRequest : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _currentCancellation;
    private long _generation;
    private bool _disposed;

    public event Action? StateChanged;

    public long Generation { get; private set; }

    public bool IsBusy { get; private set; }

    public string? Error { get; private set; }

    public Lease Start(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _currentCancellation?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentCancellation = cancellation;
            Generation = ++_generation;
            IsBusy = true;
            Error = null;
        }

        StateChanged?.Invoke();
        return new Lease(this, Generation, cancellation);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _generation++;
            Generation = _generation;
            IsBusy = false;
            Error = null;
            _currentCancellation?.Cancel();
            _currentCancellation = null;
        }

        StateChanged?.Invoke();
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return !_disposed && generation == _generation;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            IsBusy = false;
            _currentCancellation?.Cancel();
            _currentCancellation = null;
        }

        StateChanged?.Invoke();
    }

    private void Finish(long generation, CancellationTokenSource cancellation, string? error)
    {
        var stateChanged = false;
        lock (_gate)
        {
            if (!_disposed && generation == _generation && ReferenceEquals(_currentCancellation, cancellation))
            {
                _currentCancellation = null;
                IsBusy = false;
                Error = error;
                stateChanged = true;
            }
        }

        cancellation.Dispose();
        if (stateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly LatestAsyncRequest _owner;
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationToken _token;
        private bool _finished;

        internal Lease(LatestAsyncRequest owner, long generation, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
            _token = cancellation.Token;
            Generation = generation;
        }

        public long Generation { get; }

        public CancellationToken Token => _token;

        public bool IsCurrent => _owner.IsCurrent(Generation) && !_cancellation.IsCancellationRequested;

        public void Complete() => Finish(null);

        public void Fail(Exception exception) => Finish(exception.Message);

        public void Dispose() => Complete();

        private void Finish(string? error)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _owner.Finish(Generation, _cancellation, error);
        }
    }
}
