namespace Sunder.App.Services;

// One writer per ShellStateService/application session. Snapshots enter in caller
// order, before any debounce or I/O wait. No worker reads or mutates live UI state.
internal sealed class ShellStateWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly OwnedTaskObserver _tasks = new(nameof(ShellStateWriter));
    private Task _tail = Task.CompletedTask;
    private PendingDelay? _pendingDelay;

    public Task<long> Enqueue(
        Func<long> write,
        Func<CancellationToken, Task>? wait = null,
        CancellationToken cancellationToken = default,
        bool supersedePending = true)
    {
        lock (_gate)
        {
            if (_pendingDelay is { } pending)
            {
                if (!supersedePending) pending.SkipSnapshot = false;
                pending.Cancellation.Cancel();
            }
            var delay = wait is null ? null : new PendingDelay();
            _pendingDelay = delay;
            var previous = _tail;
            var task = Task.Run(async () =>
            {
                try
                {
                    // A failed write must be reported, but cannot prevent a later
                    // snapshot (in particular the final flush) from being attempted.
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex)) { }
                    if (wait is not null)
                    {
                        try { await wait(delay!.Cancellation.Token).WaitAsync(delay.Cancellation.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (delay!.Cancellation.IsCancellationRequested)
                        {
                            if (delay.SkipSnapshot) return 0;
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return write();
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_pendingDelay, delay)) _pendingDelay = null;
                        delay?.Cancellation.Dispose();
                    }
                }
            });
            _tail = task;
            _tasks.Observe(task, "persisting a shell snapshot");
            return task;
        }
    }

    // A field patch must merge with the preceding full snapshot, not supersede
    // it. Expedite its debounce and drain I/O without involving the UI dispatcher.
    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (_pendingDelay is { } pending)
            {
                pending.SkipSnapshot = false;
                pending.Cancellation.Cancel();
            }
            return _tail;
        }
    }

    public void Dispose() => _tasks.Dispose();

    private sealed class PendingDelay
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public volatile bool SkipSnapshot = true;
    }
}
