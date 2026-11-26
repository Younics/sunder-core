using System;
using System.Threading;
using System.Threading.Tasks;

// A bound on observing the fixture, not on the lifetime of a native operation.
// Use one clock across all views/reopens; the manager still owns late completions.
internal sealed class BrowserProbeDeadline(TimeSpan allowance, TimeProvider clock)
{
    private readonly long _started = clock.GetTimestamp();
    internal TimeSpan Remaining => allowance - clock.GetElapsedTime(_started);

    internal async Task ObserveAsync(string phase, Task operation, Task<Exception>? failure = null)
    {
        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero) throw Expired();
        Task completed;
        try
        {
            completed = await (failure is null ? Task.WhenAny(operation) : Task.WhenAny(operation, failure))
                .WaitAsync(remaining, clock, CancellationToken.None);
        }
        catch (TimeoutException) { throw Expired(); }
        if (failure is { IsCompletedSuccessfully: true })
            throw new InvalidOperationException($"Native fixture failed during {phase}.", await failure);
        await completed; // Propagate the original operation error, including a native timeout.

        TimeoutException Expired() => new($"Native fixture stalled during {phase}; elapsed={clock.GetElapsedTime(_started)}; operation={operation.Status}.");
    }

    internal static async Task BothAsync(Task first, Task second)
    {
        // WhenAll alone hides a real failure while its sibling is pending.
        var completed = await Task.WhenAny(first, second);
        var other = ReferenceEquals(completed, first) ? second : first;
        try { await completed; }
        catch { _ = ObserveOtherAsync(other); throw; }
        await other;

        static async Task ObserveOtherAsync(Task task)
        {
            try { await task; }
            catch (Exception error) { Console.Error.WriteLine($"Sibling fixture observation: {error}"); }
        }
    }
}
