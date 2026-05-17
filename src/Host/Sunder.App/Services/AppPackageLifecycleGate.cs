namespace Sunder.App.Services;

internal sealed class AppPackageLifecycleGate(string ownerName)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return new Releaser(_semaphore);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    public async Task<IDisposable?> TryEnterDisposeAsync()
    {
        if (_disposed)
        {
            return null;
        }

        await _semaphore.WaitAsync();
        if (_disposed)
        {
            _semaphore.Release();
            return null;
        }

        _disposed = true;
        return new Releaser(_semaphore);
    }

    public void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(ownerName);
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            semaphore.Release();
        }
    }
}
