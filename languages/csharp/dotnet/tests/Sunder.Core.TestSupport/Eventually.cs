using System.Diagnostics;

namespace Sunder.Core.TestSupport;

public static class Eventually
{
    public static async Task<T> ObserveAsync<T>(
        Func<CancellationToken, ValueTask<T>> observe,
        Func<T, bool> isSatisfied,
        TimeSpan timeout,
        TimeSpan? interval = null,
        Func<T?, Exception?, string>? describe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(isSatisfied);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var delay = interval ?? TimeSpan.FromMilliseconds(25);
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        var stopwatch = Stopwatch.StartNew();
        T? last = default;
        Exception? lastError = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                last = await observe(cancellationToken).ConfigureAwait(false);
                lastError = null;
                if (isSatisfied(last)) return last;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                var diagnostic = describe?.Invoke(last, lastError)
                                 ?? $"last={Format(last)}; lastError={Format(lastError)}";
                throw new EventualTimeoutException(timeout, diagnostic, lastError);
            }
            await Task.Delay(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task AssertPendingAsync(Task task, Func<string> describe)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(describe);
        await Task.Yield();
        await Task.Yield();
        if (task.IsCompleted)
        {
            string outcome;
            if (task.IsCanceled) outcome = "canceled";
            else if (task.IsFaulted) outcome = $"faulted: {task.Exception?.GetBaseException().Message}";
            else outcome = "completed successfully";
            throw new ObservationException($"Expected operation to remain pending, but it {outcome}. {describe()}");
        }
    }

    private static string Format(object? value)
        => value switch
        {
            null => "<null>",
            Exception exception => $"{exception.GetType().Name}: {exception.Message}",
            _ => value.ToString() ?? "<null-string>",
        };
}

public sealed class EventualTimeoutException(
    TimeSpan timeout,
    string diagnostic,
    Exception? innerException = null)
    : TimeoutException(
        $"Condition was not observed within {timeout.TotalMilliseconds:F0} ms. {diagnostic}",
        innerException)
{
    public TimeSpan Timeout { get; } = timeout;

    public string Diagnostic { get; } = diagnostic;
}

public sealed class ObservationException(string message) : Exception(message);
