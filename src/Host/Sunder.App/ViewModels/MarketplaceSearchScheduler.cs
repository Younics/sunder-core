namespace Sunder.App.ViewModels;

internal sealed class MarketplaceSearchScheduler(Func<CancellationToken, Task> searchAsync, TimeSpan defaultDelay) : IDisposable
{
    private CancellationTokenSource? _pendingSearchCts;
    private bool _disposed;

    public void Queue(TimeSpan? delay = null)
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        var cancellationTokenSource = new CancellationTokenSource();
        _pendingSearchCts = cancellationTokenSource;
        _ = RunQueuedSearchAsync(cancellationTokenSource, delay ?? defaultDelay);
    }

    public void Cancel()
    {
        var cancellationTokenSource = _pendingSearchCts;
        if (cancellationTokenSource is null)
        {
            return;
        }

        _pendingSearchCts = null;
        cancellationTokenSource.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private async Task RunQueuedSearchAsync(
        CancellationTokenSource cancellationTokenSource,
        TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationTokenSource.Token);
            }

            await searchAsync(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_pendingSearchCts, cancellationTokenSource))
            {
                _pendingSearchCts = null;
            }

            cancellationTokenSource.Dispose();
        }
    }
}
