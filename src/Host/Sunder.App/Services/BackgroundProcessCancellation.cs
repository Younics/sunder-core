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

        _ = DeliverAsync([cancellationTokenSource]);
    }

    public static void Deliver(IEnumerable<CancellationTokenSource> cancellationTokenSources)
        => _ = DeliverAsync(cancellationTokenSources);

    private static Task DeliverAsync(IEnumerable<CancellationTokenSource> cancellationTokenSources)
    {
        var cancellations = new List<Task>();
        foreach (var cancellationTokenSource in cancellationTokenSources)
        {
            try
            {
                cancellations.Add(cancellationTokenSource.CancelAsync());
            }
            catch (ObjectDisposedException)
            {
                // The process completed between marking it as cancelling and delivering cancellation.
            }
        }

        return ObserveCancellationAsync(cancellations);
    }

    private static async Task ObserveCancellationAsync(IReadOnlyList<Task> cancellations)
    {
        try
        {
            await Task.WhenAll(cancellations).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("A background process cancellation callback failed.", exception);
        }
    }
}
