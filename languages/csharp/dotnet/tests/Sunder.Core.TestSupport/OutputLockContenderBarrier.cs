namespace Sunder.Core.TestSupport;

public sealed class OutputLockContenderBarrier<T> : IDisposable
{
    private readonly TaskCompletionSource _attempting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string> _describe;
    private OutputLockContenderBarrier(Func<Action, T> acquire, Func<string> describe)
    {
        _describe = describe;
        // Only this contender can report its attempt. Filesystem notifications
        // may describe another owner's earlier acquisition/release transition.
        Completion = Task.Run(() => acquire(() => _attempting.TrySetResult()));
    }

    public Task<T> Completion { get; }

    public static OutputLockContenderBarrier<T> Start(
        Func<Action, T> acquire,
        Func<string> describe)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(describe);
        return new OutputLockContenderBarrier<T>(acquire, describe);
    }

    public async Task WaitForAttemptAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.WhenAny(_attempting.Task, Completion).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!_attempting.Task.IsCompleted)
            {
                await Completion.ConfigureAwait(false);
                throw new ObservationException($"Output-lock contender completed without reporting an acquisition attempt. {_describe()}");
            }
            await _attempting.Task.ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Output-lock contender did not report an acquisition attempt. {_describe()}", exception);
        }
    }

    public Task AssertContendedAsync()
        => Eventually.AssertPendingAsync(Completion, _describe);

    public void Dispose() => _attempting.TrySetCanceled();
}
