namespace Sunder.Sdk.Rpc;

public sealed partial class SunderRpcServiceBindingLifetime
{
    // Optionality is selected by the generated member visitor, never by the target type.
    internal async ValueTask<SunderRpcServiceBinding?> ResolveOptionalBindingAsync(
        SunderRpcProviderSnapshot locator, CancellationToken token)
    {
        ValidateProvider(locator);
        var current = await ProbeOptionalProviderAsync(locator, token).ConfigureAwait(false);
        if (current is null || current.State != SunderRpcProviderState.Active
            || !SunderRpcServiceLocator.SameIdentity(locator, current)) return null;
        await DiscoverAsync(locator.ContractId, token).ConfigureAwait(false);
        var binding = Track(current);
        return await EnsureOptionalCurrentAsync(binding, token).ConfigureAwait(false) ? binding : null;
    }

    internal async ValueTask<bool> EnsureOptionalCurrentAsync(SunderRpcServiceBinding binding, CancellationToken token)
    {
        // Fresh visibility/permission wins over an older cached retirement reason.
        var current = await ProbeOptionalProviderAsync(binding.Snapshot, token).ConfigureAwait(false);
        if (!SunderRpcOptionalReferenceAvailability.IsAvailable(binding, token)) return false;
        if (current is null || current.State != SunderRpcProviderState.Active
            || !SunderRpcServiceLocator.SameIdentity(binding.Snapshot, current))
        {
            RetireBinding(binding, SunderRpcServiceLocator.Stale());
            token.ThrowIfCancellationRequested();
            return false;
        }
        await ReadyAsync(token).ConfigureAwait(false);
        if (!SunderRpcOptionalReferenceAvailability.IsAvailable(binding, token)) return false;
        token.ThrowIfCancellationRequested();
        _stopping.Token.ThrowIfCancellationRequested();
        return true;
    }

    private async ValueTask<SunderRpcProviderSnapshot?> ProbeOptionalProviderAsync(SunderRpcProviderSnapshot locator, CancellationToken token)
    {
        ValidateProvider(locator);
        token.ThrowIfCancellationRequested();
        AddContract(locator.ContractId);
        Start();
        var epoch = EnterQuery(requireConnected: false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        try
        {
            SunderRpcProviderSnapshot? current;
            try
            {
                try { current = await _client.GetProviderAsync(locator.Endpoint, linked.Token).ConfigureAwait(false); }
                catch (SunderRpcException error) when (!_stopping.IsCancellationRequested
                    && SunderRpcOptionalReferenceAvailability.IsExpectedAbsence(error, token))
                {
                    current = null;
                }
                if (current is null)
                {
                    // The host cannot authorize a removed endpoint: there is no
                    // provider left to inspect. Check current contract visibility
                    // before accepting absence, without waiting on cached readiness.
                    var snapshot = await _client.DiscoverAsync(locator.ContractId, linked.Token).ConfigureAwait(false);
                    ValidateSnapshot(locator.ContractId, snapshot);
                }
            }
            catch (SunderRpcException error) when (!_stopping.IsCancellationRequested
                && SunderRpcOptionalReferenceAvailability.IsExpectedAbsence(error, token))
            {
                // A safe, single-target absence is not a catalog-wide failure.
                token.ThrowIfCancellationRequested();
                _stopping.Token.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception error) when (!token.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                QueryFailed(error, locator.ContractId, epoch);
                throw;
            }
            token.ThrowIfCancellationRequested();
            _stopping.Token.ThrowIfCancellationRequested();
            if (current is not null) ValidateProvider(current);
            lock (_gate) CheckEpoch(epoch);
            return current;
        }
        finally { ExitQuery(); }
    }
}

internal static class SunderRpcOptionalReferenceAvailability
{
    internal static bool IsExpectedAbsence(SunderRpcException error, CancellationToken token)
        => !token.IsCancellationRequested
           && error.Error.DispatchState == SunderRpcDispatchState.NotDispatched
           && error.Error.RetrySafety == SunderRpcRetrySafety.Safe
           && error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable;

    internal static bool IsAvailable(SunderRpcServiceBinding binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { binding.ThrowIfRetired(); return true; }
        catch (SunderRpcException error) when (IsExpectedAbsence(error, token)) { return false; }
    }
}
