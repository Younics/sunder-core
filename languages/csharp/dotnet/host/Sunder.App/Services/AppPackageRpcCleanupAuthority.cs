using System.Diagnostics;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

// The timer and cancellation observers never capture an owner's ExecutionContext.
// Expiry cancels transport work; the monotonic check also fences delayed timer delivery.
internal sealed class AppPackageRpcCleanupAuthority
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _budget;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly ITimer _timer;
    private readonly AsyncLocal<AppPackageRpcCleanupAuthority?> _ambient;
    private int _completed;

    internal AppPackageRpcCleanupAuthority(ISunderRpcClient client, TimeSpan budget, AsyncLocal<AppPackageRpcCleanupAuthority?> ambient)
    {
        _budget = budget;
        _ambient = ambient;
        _token = _cancellation.Token;
        DeadlineUtc = DateTimeOffset.UtcNow + budget;
        Client = new AppPackageCleanupRpcClient(client, this);
        if (ExecutionContext.IsFlowSuppressed()) _timer = CreateTimer();
        else { using (ExecutionContext.SuppressFlow()) _timer = CreateTimer(); }
        _timer.Change(budget, Timeout.InfiniteTimeSpan);
    }

    internal ISunderRpcClient Client { get; }
    internal DateTimeOffset DeadlineUtc { get; }
    internal bool IsActive => Volatile.Read(ref _completed) == 0 && Stopwatch.GetElapsedTime(_started) < _budget;

    private ITimer CreateTimer() => TimeProvider.System.CreateTimer(
        static state => ((AppPackageRpcCleanupAuthority)state!).Complete(), this,
        Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    internal void RequireActive(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (IsActive) return;
        Complete();
        throw new OperationCanceledException("The owning App RPC cleanup authority has ended.", _token);
    }

    internal CancellationTokenSource BeginOperation(CancellationToken caller)
    {
        lock (_gate)
        {
            RequireActive(caller);
            return CancellationTokenSource.CreateLinkedTokenSource(caller, _token);
        }
    }

    internal void RequireAmbient(CancellationToken caller)
    {
        RequireActive(caller);
        if (!ReferenceEquals(_ambient.Value, this))
            throw new InvalidOperationException("App RPC cleanup work requires its owning disposal context.");
    }

    internal SunderRpcCallOptions Clamp(SunderRpcCallOptions? options)
    {
        RequireActive(default);
        var remainingDeadline = DateTimeOffset.UtcNow + (_budget - Stopwatch.GetElapsedTime(_started));
        var deadline = remainingDeadline < DeadlineUtc ? remainingDeadline : DeadlineUtc;
        if (options?.DeadlineUtc is { } requested && requested < deadline) deadline = requested;
        return (options ?? new SunderRpcCallOptions()) with { DeadlineUtc = deadline };
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _timer.Dispose();
            // Cancellation callbacks must not block a retirement deadline or replace
            // its result. Observe their failure while keeping their ambient context out
            // of the host's cancellation-drain task.
            if (ExecutionContext.IsFlowSuppressed()) Cancel();
            else { using (ExecutionContext.SuppressFlow()) Cancel(); }
        }
    }

    private void Cancel() => _ = DrainCancellationAsync(_cancellation.CancelAsync(), _cancellation);

    private static async Task DrainCancellationAsync(Task cancellation, CancellationTokenSource source)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError("Failed to cancel an App RPC cleanup operation.", exception);
        }
        finally { source.Dispose(); }
    }
}
