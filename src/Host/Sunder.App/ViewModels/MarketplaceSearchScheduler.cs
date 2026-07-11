using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class MarketplaceSearchScheduler(Func<CancellationToken, Task> searchAsync, TimeSpan defaultDelay) : IDisposable
{
    private readonly OwnedTaskObserver _tasks = new(nameof(MarketplaceSearchScheduler));
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
        _tasks.Observe(RunQueuedSearchAsync(cancellationTokenSource, delay ?? defaultDelay), "running a queued search");
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
        _tasks.Dispose();
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
