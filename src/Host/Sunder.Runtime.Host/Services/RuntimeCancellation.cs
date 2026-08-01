namespace Sunder.Runtime.Host.Services;

internal static class RuntimeCancellation
{
    public static Task Signal(CancellationTokenSource cancellation)
    {
        try
        {
            return ObserveAsync(cancellation.CancelAsync());
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask;
        }
    }

    public static void DisposeAfterCallbacks(CancellationTokenSource cancellation, Task callbacks)
    {
        if (callbacks.IsCompleted)
        {
            cancellation.Dispose();
            return;
        }
        _ = DisposeAfterCallbacksAsync(cancellation, callbacks);
    }

    private static async Task ObserveAsync(Task callbacks)
    {
        try
        {
            await callbacks.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation is already visible; callback failures cannot roll it back.
        }
    }

    private static async Task DisposeAfterCallbacksAsync(
        CancellationTokenSource cancellation,
        Task callbacks)
    {
        await callbacks.ConfigureAwait(false);
        cancellation.Dispose();
    }
}
