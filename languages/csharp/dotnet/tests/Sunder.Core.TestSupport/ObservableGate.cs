namespace Sunder.Core.TestSupport;

public sealed class ObservableGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public bool IsReleased => _released.Task.IsCompleted;

    public void SignalEntered() => _entered.TrySetResult();

    public void Release() => _released.TrySetResult();

    public async Task EnterAndWaitAsync(CancellationToken cancellationToken = default)
    {
        SignalEntered();
        await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForReleaseAsync(CancellationToken cancellationToken = default)
        => _released.Task.WaitAsync(cancellationToken);
}
