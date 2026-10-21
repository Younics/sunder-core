using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class AppWebPendingRequest : IDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private CancellationTokenRegistration _requestRegistration;
    private CancellationTokenRegistration _lifetimeRegistration;
    private CancellationTokenRegistration _deadlineRegistration;
    private int _cancellationReason;
    private int _dispatchState;
    private int _retrySafety;
    private int _completed;
    private int _disposed;

    public AppWebPendingRequest(
        long documentGeneration,
        CancellationToken request,
        CancellationToken lifetime,
        CancellationTokenSource deadline)
    {
        DocumentGeneration = documentGeneration;
        _token = _cancellation.Token;
        // The bridge creates the deadline source; this request only observes and disposes it.
        _deadline = deadline;
        _requestRegistration = request.Register(
            static state => ((AppWebPendingRequest)state!).TryCancel(CancellationReason.Cancelled),
            this);
        _lifetimeRegistration = lifetime.Register(
            static state => ((AppWebPendingRequest)state!).TryCancel(CancellationReason.Cancelled),
            this);
        _deadlineRegistration = _deadline.Token.Register(
            static state => ((AppWebPendingRequest)state!).TryCancel(CancellationReason.Deadline),
            this);
    }

    public long DocumentGeneration { get; }

    // Late native completions still use this token in the bridge's cancellation
    // filter after retirement disposed the source. Preserve its original state.
    public CancellationToken Token => _token;

    public bool TimedOut => Volatile.Read(ref _cancellationReason) == (int)CancellationReason.Deadline;

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public SunderRpcDispatchState DispatchState
        => (SunderRpcDispatchState)Volatile.Read(ref _dispatchState);

    public SunderRpcRetrySafety RetrySafety
        => (SunderRpcRetrySafety)Volatile.Read(ref _retrySafety);

    public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;

    public void MarkPossiblyDispatched(SunderRpcRetrySafety retrySafety)
    {
        Volatile.Write(ref _retrySafety, (int)retrySafety);
        Volatile.Write(ref _dispatchState, (int)SunderRpcDispatchState.PossiblyDispatched);
    }

    public void Cancel()
    {
        TryCancel(CancellationReason.Cancelled);
    }

    private void TryCancel(CancellationReason reason)
    {
        if (Interlocked.CompareExchange(ref _cancellationReason, (int)reason, (int)CancellationReason.None)
            != (int)CancellationReason.None)
        {
            return;
        }
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race with navigation or bridge disposal.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _requestRegistration.Dispose();
        _lifetimeRegistration.Dispose();
        _deadlineRegistration.Dispose();
        _deadline.Dispose();
        _cancellation.Dispose();
    }

    private enum CancellationReason
    {
        None,
        Cancelled,
        Deadline,
    }
}
