namespace Sunder.Sdk.Rpc;

internal sealed class SunderRpcServiceBinding
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private SunderRpcException? _error;
    private Task _retired = Task.CompletedTask;

    internal SunderRpcServiceBinding(SunderRpcServiceBindingLifetime owner, SunderRpcProviderSnapshot snapshot,
        long epoch, long sequence)
    {
        Owner = owner;
        Snapshot = snapshot;
        Epoch = epoch;
        Sequence = sequence;
        Token = _retirement.Token;
    }

    internal SunderRpcServiceBindingLifetime Owner { get; }
    internal SunderRpcProviderSnapshot Snapshot { get; }
    internal long Epoch { get; }
    internal long Sequence { get; }
    internal CancellationToken Token { get; }
    // Accessed only under the owning lifetime's gate; cleared on every retirement.
    internal Dictionary<Type, object> Entries { get; } = [];
    internal bool IsRetired { get { lock (_gate) return _error is not null; } }
    internal Task Retirement { get { lock (_gate) return _retired; } }

    internal void ThrowIfRetired()
    {
        lock (_gate) if (_error is { } error) throw error;
    }

    internal void Retire(SunderRpcException error)
    {
        lock (_gate)
        {
            if (_error is not null) return;
            _error = error;
            _retired = CancelAsync();
        }
    }

    private async Task CancelAsync()
    {
        try { await _retirement.CancelAsync().ConfigureAwait(false); }
        catch { } // Business cancellation callbacks cannot stop catalog reconciliation or DI teardown.
        finally { _retirement.Dispose(); }
    }

    internal ValueTask EnsureCurrentAsync(CancellationToken cancellationToken) => Owner.EnsureCurrentAsync(this, cancellationToken);
    internal ValueTask<bool> TryReportAsync(Exception exception, CancellationToken cancellationToken)
        => Owner.TryReportAsync(this, exception, cancellationToken);
}
