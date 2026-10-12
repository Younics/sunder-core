namespace Sunder.Sdk.Rpc;

public sealed partial class SunderRpcServiceBindingLifetime
{
    private async Task ObserveAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            long epoch;
            lock (_gate)
            {
                if (_dispose is not null) return;
                epoch = _epoch;
                _observation = observation;
                if (_ready.Task.IsCompleted) _ready = NewCompletion();
            }
            try
            {
                var cursor = await RefreshAllAsync(epoch, observation.Token).ConfigureAwait(false);
                await using var watch = _client.WatchAsync(cursor.Revision, cursor.Sequence, observation.Token)
                    .GetAsyncEnumerator(observation.Token);
                Task<bool>? next = null;
                try
                {
                    next = watch.MoveNextAsync().AsTask();
                    // Establish the broker subscription before this resnapshot. Publication of ready
                    // bindings cannot race a withdrawal between the initial snapshot and subscription.
                    await RefreshAllAsync(epoch, observation.Token).ConfigureAwait(false);
                    var initialEvents = 0;
                    while (next.IsCompleted)
                    {
                        if (!await next.ConfigureAwait(false)) throw Unavailable();
                        if (++initialEvents > MaximumBindings) throw Capacity();
                        cursor = ApplyEvent(watch.Current, cursor, epoch);
                        next = watch.MoveNextAsync().AsTask();
                    }
                    lock (_gate)
                    {
                        CheckEpoch(epoch);
                        _connected = true;
                        _availabilityError = null;
                        _ready.TrySetResult();
                    }
                    while (await next.ConfigureAwait(false))
                    {
                        cursor = ApplyEvent(watch.Current, cursor, epoch);
                        next = watch.MoveNextAsync().AsTask();
                    }
                    throw Unavailable(); // EOF is a disconnect, not a permanent end of retirement observation.
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    // Revoke issued bindings before potentially asynchronous transport cleanup.
                    InvalidateAll(Failure(exception), epoch, cancelObservation: false);
                    throw;
                }
                finally
                {
                    // An async enumerator cannot be disposed while MoveNext is still in flight.
                    if (!observation.IsCancellationRequested) await observation.CancelAsync().ConfigureAwait(false);
                    if (next is not null)
                    {
                        try { await next.ConfigureAwait(false); }
                        catch { } // The observation failure is retained by the enclosing catch.
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                InvalidateAll(Failure(exception), epoch, cancelObservation: false);
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_observation, observation)) _observation = null;
            }
            try { await Task.Delay(RecoveryDelay, _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
        }
    }

    private async ValueTask<(long Revision, long Sequence)> RefreshAllAsync(long epoch, CancellationToken token)
    {
        string[] contracts;
        lock (_gate) { CheckEpoch(epoch); contracts = _contracts.Keys.ToArray(); }
        (long Revision, long Sequence)? cursor = null;
        SunderRpcException? denied = null;
        foreach (var contractId in contracts)
        {
            try
            {
                var snapshot = await _client.DiscoverAsync(contractId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ValidateSnapshot(contractId, snapshot);
                ApplySnapshot(contractId, snapshot, epoch);
                // Use a real snapshot's complete cursor, never independently chosen revision/sequence minima.
                if (cursor is null || snapshot.Sequence < cursor.Value.Sequence)
                    cursor = (snapshot.Revision, snapshot.Sequence);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.PermissionDenied)
            {
                denied = exception;
                ContractFailed(contractId, exception, epoch);
            }
        }
        return cursor ?? throw (denied ?? Unavailable());
    }

    private static void ValidateSnapshot(string contractId, SunderRpcCatalogSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Revision is < 0 or > 9_007_199_254_740_991
            || snapshot.Sequence is < 0 or > 9_007_199_254_740_991 || snapshot.Providers.Count > MaximumBindings)
            throw InvalidCatalog();
        if (snapshot.ResetRequired) throw Unavailable("rpc.catalog.reset", "The service catalog requires a fresh observation.");
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in snapshot.Providers)
        {
            if (provider is null) throw InvalidCatalog();
            ValidateProvider(provider);
            if (provider.ContractId != contractId || provider.CatalogRevision > snapshot.Revision
                || !endpoints.Add(provider.Endpoint.Value)) throw InvalidCatalog();
        }
    }

    private void ApplySnapshot(string contractId, SunderRpcCatalogSnapshot snapshot, long epoch)
    {
        lock (_gate)
        {
            CheckEpoch(epoch);
            var contract = _contracts[contractId];
            if (contract.HasSnapshot && snapshot.Sequence < contract.Sequence) return;
            if (_contracts.Where(pair => pair.Key != contractId).Sum(pair => pair.Value.Providers.Count)
                + snapshot.Providers.Count(provider => provider.State == SunderRpcProviderState.Active) > MaximumBindings)
                throw Capacity();
            contract.HasSnapshot = true;
            contract.Sequence = snapshot.Sequence;
            contract.Error = null;
            contract.Providers.Clear();
            foreach (var provider in snapshot.Providers)
                if (provider.State == SunderRpcProviderState.Active) contract.Providers.Add(provider.Endpoint.Value, provider);
            foreach (var binding in _bindings.Values.Where(binding => binding.Snapshot.ContractId == contractId).ToArray())
            {
                if (!contract.Providers.TryGetValue(binding.Snapshot.Endpoint.Value, out var current)
                    || !SunderRpcServiceLocator.SameIdentity(binding.Snapshot, current))
                {
                    RetireLocked(binding, SunderRpcServiceLocator.Stale());
                }
            }
        }
    }

    private (long Revision, long Sequence) ApplyEvent(SunderRpcCatalogEvent change,
        (long Revision, long Sequence) cursor, long epoch)
    {
        if (change is null || !Enum.IsDefined(change.Kind)) throw InvalidCatalog();
        if (change.Kind == SunderRpcCatalogEventKind.ResetRequired)
            throw Unavailable("rpc.catalog.reset", "The service catalog requires a fresh observation.");
        if (change.Revision < cursor.Revision || change.Sequence <= cursor.Sequence
            || change.Revision > 9_007_199_254_740_991 || change.Sequence > 9_007_199_254_740_991
            || change.Provider is not { } provider) throw InvalidCatalog();
        ValidateProvider(provider);
        if (provider.CatalogRevision > change.Revision) throw InvalidCatalog();
        var withdraw = change.Kind is SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Deactivated
            or SunderRpcCatalogEventKind.Faulted || provider.State != SunderRpcProviderState.Active;
        lock (_gate)
        {
            CheckEpoch(epoch);
            foreach (var binding in _bindings.Values.ToArray())
            {
                if (change.Sequence <= binding.Sequence) continue;
                var previous = binding.Snapshot;
                var sameSlot = previous.Endpoint == provider.Endpoint
                    || previous.PackageId == provider.PackageId && previous.ProviderId == provider.ProviderId
                    && previous.ContractId == provider.ContractId;
                if (sameSlot && (withdraw || !SunderRpcServiceLocator.SameIdentity(previous, provider)))
                {
                    RetireLocked(binding, change.Kind == SunderRpcCatalogEventKind.Faulted
                        ? new(new(SunderRpcErrorKind.ProviderFaulted, "rpc.provider-faulted", "The exact service activation faulted."))
                        : SunderRpcServiceLocator.Stale());
                }
            }
            if (_contracts.TryGetValue(provider.ContractId, out var contract) && change.Sequence > contract.Sequence)
            {
                contract.Sequence = change.Sequence;
                if (withdraw) contract.Providers.Remove(provider.Endpoint.Value);
                else
                {
                    foreach (var old in contract.Providers.Values.Where(value => value.PackageId == provider.PackageId
                                 && value.ProviderId == provider.ProviderId && value.Endpoint != provider.Endpoint).ToArray())
                        contract.Providers.Remove(old.Endpoint.Value);
                    if (_contracts.Values.Sum(value => value.Providers.Count) >= MaximumBindings
                        && !contract.Providers.ContainsKey(provider.Endpoint.Value))
                        throw Capacity();
                    contract.Providers[provider.Endpoint.Value] = provider;
                }
            }
        }
        return (change.Revision, change.Sequence);
    }

    private void QueryFailed(Exception exception, string contractId, long epoch)
    {
        var failure = Failure(exception);
        if (failure.Error.Kind == SunderRpcErrorKind.PermissionDenied) ContractFailed(contractId, failure, epoch);
        else InvalidateAll(failure, epoch, cancelObservation: true);
    }

    private void ProviderQueryFailed(Exception exception, SunderRpcProviderSnapshot provider, long epoch)
    {
        if (exception is not SunderRpcException { Error.Kind: SunderRpcErrorKind.Unavailable } unavailable)
        {
            QueryFailed(exception, provider.ContractId, epoch);
            return;
        }
        lock (_gate)
        {
            if (_dispose is not null || epoch != _epoch) return;
            // Endpoint availability is not catalog continuity. Discovery/watch may still be healthy,
            // and the next generated List -> Ensure must be able to observe a new permission denial.
            // Retire this exact issued binding permanently, without caching its failure globally or
            // restarting the shared observer. A real observer/discovery failure still invalidates all.
            if (_bindings.TryGetValue(Identity(provider), out var binding)) RetireLocked(binding, unavailable);
            if (_contracts.TryGetValue(provider.ContractId, out var contract)
                && contract.Providers.TryGetValue(provider.Endpoint.Value, out var current)
                && SunderRpcServiceLocator.SameIdentity(provider, current))
                contract.Providers.Remove(provider.Endpoint.Value);
        }
    }

    private void ContractFailed(string contractId, SunderRpcException error, long epoch)
    {
        lock (_gate)
        {
            if (_dispose is not null || epoch != _epoch) return;
            var contract = _contracts[contractId];
            contract.Providers.Clear();
            contract.HasSnapshot = true;
            contract.Error = error;
            foreach (var binding in _bindings.Values.Where(binding => binding.Snapshot.ContractId == contractId).ToArray())
                RetireLocked(binding, error);
        }
    }

    private void InvalidateAll(SunderRpcException error, long epoch, bool cancelObservation)
    {
        lock (_gate)
        {
            if (_dispose is not null || epoch != _epoch) return;
            // Foreground probes report their own failures, but must not keep cancelling/restarting
            // the single bounded recovery loop while the observation is already unavailable.
            if (cancelObservation && !_connected && _availabilityError is not null) return;
            _epoch++;
            _connected = false;
            _availabilityError = error;
            FailReady(error);
            foreach (var binding in _bindings.Values.ToArray()) RetireLocked(binding, error);
            _bindings.Clear();
            foreach (var contract in _contracts.Values)
            {
                contract.Providers.Clear();
                contract.HasSnapshot = false;
                contract.Sequence = 0;
                contract.Error = null;
            }
            if (cancelObservation && _observation is { } observation) _ = ObserveCancellationAsync(observation.CancelAsync());
        }
    }

    private void RetireBinding(SunderRpcServiceBinding binding, SunderRpcException error)
    {
        lock (_gate)
        {
            RetireLocked(binding, error);
            if (_contracts.TryGetValue(binding.Snapshot.ContractId, out var contract)
                && contract.Providers.TryGetValue(binding.Snapshot.Endpoint.Value, out var current)
                && SunderRpcServiceLocator.SameIdentity(binding.Snapshot, current))
                contract.Providers.Remove(binding.Snapshot.Endpoint.Value);
        }
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch { } // The observer's MoveNext/Dispose path owns the catalog failure and teardown.
    }

    private void RetireLocked(SunderRpcServiceBinding binding, SunderRpcException error)
    {
        RemoveBinding(binding);
        binding.Retire(error);
        var retirement = binding.Retirement;
        if (retirement.IsCompleted || !_retirements.Add(retirement)) return;
        _ = retirement.ContinueWith(task => { lock (_gate) _retirements.Remove(task); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RemoveBinding(SunderRpcServiceBinding binding)
    {
        var key = Identity(binding.Snapshot);
        if (_bindings.TryGetValue(key, out var known) && ReferenceEquals(known, binding)) _bindings.Remove(key);
        binding.Entries.Clear();
    }
}
