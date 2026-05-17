namespace Sunder.App.Services;

internal static class BackgroundProcessCancellation
{
    public static async Task WaitForRunningTasksAsync(Task[] runningTasks, CancellationToken cancellationToken)
    {
        if (runningTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(runningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Individual process failures are reflected through process state snapshots.
        }
    }

    public static void Deliver(CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is null)
        {
            return;
        }

        Deliver([cancellationTokenSource]);
    }

    public static void Deliver(IEnumerable<CancellationTokenSource> cancellationTokenSources)
    {
        foreach (var cancellationTokenSource in cancellationTokenSources)
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The process completed between marking it as cancelling and delivering cancellation.
            }
        }
    }
}
