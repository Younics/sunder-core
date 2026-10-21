using System.ComponentModel;
using System.Text.Json;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Services;

namespace Sunder.Sdk.Rpc;

/// <summary>Shares exact provider bindings and one lazy catalog observer within a receiver's DI scope.</summary>
/// <remarks>
/// For generated bindings. Construction performs no discovery. Discovery or use of a bound reference
/// starts observation. Retirement is permanent for an issued binding, including after catalog recovery.
/// Factories create exact-bound proxies borrowing this DI scope; they must observe the supplied retirement token.
/// Independent discovery can recover after the receiver's transport reopens and observation is reestablished;
/// it does not revive previously issued references or replay service invocations.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed partial class SunderRpcServiceBindingLifetime : IDisposable, IAsyncDisposable
{
    private const int MaximumContracts = 128;
    private const int MaximumBindings = 4096;
    private const int MaximumTypedBindings = 128;
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(250);
    private readonly ISunderRpcClient _client;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, ObservedContract> _contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<SunderRpcProviderSnapshot, SunderRpcServiceBinding> _bindings = [];
    private readonly HashSet<Task> _retirements = [];
    private TaskCompletionSource _ready = NewCompletion();
    private Task? _watchTask;
    private Task? _dispose;
    private CancellationTokenSource? _observation;
    private SunderRpcException? _availabilityError;
    private bool _connected;
    private long _epoch;
    private int _queries;
    private readonly TaskCompletionSource _queriesDrained = NewCompletion();

    /// <summary>Creates a dormant receiver-owned binding lifetime.</summary>
    public SunderRpcServiceBindingLifetime(ISunderRpcClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        LifetimeToken = _stopping.Token;
    }

    internal ISunderRpcClient Client => _client;
    internal CancellationToken LifetimeToken { get; }

    /// <summary>Discovers a fresh visible catalog and starts the shared observer on first use.</summary>
    /// <remarks>Both returned cursor components belong to the same broker snapshot.</remarks>
    public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId,
        CancellationToken cancellationToken = default)
    {
        AddContract(contractId);
        cancellationToken.ThrowIfCancellationRequested();
        Start();
        var epoch = EnterQuery(out var observationReady);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            // Explicit reads must reach the receiver even while observation is recovering.
            // In particular, an older availability failure cannot mask a new authorization denial.
            var snapshot = await ReadAsync().ConfigureAwait(false);
            // A successful probe is not permission to publish bindings from a broken observation.
            var readyEpoch = await ReadyAfterDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            // Initial subscription/resnapshot may have observed a withdrawal after the probe.
            // Preserve the startup race barrier without delaying a failed explicit probe behind it.
            if (!observationReady || epoch != readyEpoch)
            {
                epoch = readyEpoch;
                snapshot = await ReadAsync().ConfigureAwait(false);
            }
            ApplySnapshot(contractId, snapshot, epoch);
            return snapshot;
        }
        finally { ExitQuery(); }

        async ValueTask<SunderRpcCatalogSnapshot> ReadAsync()
        {
            try
            {
                var snapshot = await _client.DiscoverAsync(contractId, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                ValidateSnapshot(contractId, snapshot);
                return snapshot;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                QueryFailed(exception, contractId, epoch);
                throw;
            }
        }
    }

    /// <summary>Creates or reuses an exact typed entry from discovered provider metadata.</summary>
    /// <remarks>The factory receives this receiver's client and a permanent binding retirement token.</remarks>
    public ServiceProvider<TService> CreateProvider<TService>(SunderRpcProviderSnapshot snapshot,
        Func<ISunderRpcClient, SunderRpcProviderSnapshot, CancellationToken, TService> createService) where TService : class
    {
        ArgumentNullException.ThrowIfNull(createService);
        var binding = Track(snapshot);
        Start();
        lock (_gate)
        {
            if (binding.Entries.TryGetValue(typeof(TService), out var cached)) return (ServiceProvider<TService>)cached;
            if (binding.Entries.Count >= MaximumTypedBindings) throw Capacity();
        }
        var entry = new ServiceProvider<TService>(snapshot, createService(_client, snapshot, binding.Token), binding);
        lock (_gate)
        {
            ThrowIfStopped();
            if (binding.Entries.TryGetValue(typeof(TService), out var cached)) return (ServiceProvider<TService>)cached;
            if (!binding.IsRetired)
            {
                if (binding.Entries.Count >= MaximumTypedBindings) throw Capacity();
                binding.Entries.Add(typeof(TService), entry);
            }
        }
        return entry;
    }

    /// <summary>Resolves all eleven locator fields using this receiver's visibility and creates its typed reference.</summary>
    public async ValueTask<ServiceReference<TService>> ResolveAsync<TService>(SunderRpcProviderSnapshot locator,
        Func<ISunderRpcClient, SunderRpcProviderSnapshot, CancellationToken, TService> createService,
        CancellationToken cancellationToken = default) where TService : class
    {
        ArgumentNullException.ThrowIfNull(createService);
        var binding = await ResolveBindingAsync(locator, cancellationToken).ConfigureAwait(false);
        var result = CreateProvider(binding.Snapshot, createService).Reference;
        binding.ThrowIfRetired();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal async ValueTask<SunderRpcServiceBinding> ResolveBindingAsync(SunderRpcProviderSnapshot locator,
        CancellationToken cancellationToken)
    {
        ValidateProvider(locator);
        // The endpoint lookup is authoritative for this reference's visibility. Probe it before
        // catalog readiness so a fresh permission denial cannot become a cached outage response.
        var current = await ProbeProviderAsync(locator, cancellationToken).ConfigureAwait(false);
        if (current is null || current.State != SunderRpcProviderState.Active
            || !SunderRpcServiceLocator.SameIdentity(locator, current)) throw SunderRpcServiceLocator.Stale();
        await DiscoverAsync(locator.ContractId, cancellationToken).ConfigureAwait(false);
        var binding = Track(current);
        await EnsureCurrentAsync(binding, cancellationToken).ConfigureAwait(false);
        return binding;
    }

    private async ValueTask<SunderRpcProviderSnapshot?> ProbeProviderAsync(SunderRpcProviderSnapshot locator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddContract(locator.ContractId);
        Start();
        var epoch = EnterQuery(requireConnected: false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            var current = await _client.GetProviderAsync(locator.Endpoint, linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (current is not null) ValidateProvider(current);
            lock (_gate) CheckEpoch(epoch);
            return current;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            ProviderQueryFailed(exception, locator, epoch);
            throw;
        }
        finally { ExitQuery(); }
    }

    internal SunderRpcServiceBinding Track(SunderRpcProviderSnapshot snapshot)
    {
        ValidateProvider(snapshot);
        AddContract(snapshot.ContractId);
        SunderRpcServiceBinding binding;
        SunderRpcException? retirement = null;
        lock (_gate)
        {
            ThrowIfStopped();
            if (!_connected && _availabilityError is { } unavailable) throw unavailable;
            var key = Identity(snapshot);
            if (_bindings.TryGetValue(key, out var existing)) return existing;
            var contract = _contracts[snapshot.ContractId];
            binding = new(this, snapshot, _epoch, contract.Sequence);
            if (snapshot.State != SunderRpcProviderState.Active
                || contract.HasSnapshot && (!contract.Providers.TryGetValue(snapshot.Endpoint.Value, out var observed)
                    || !SunderRpcServiceLocator.SameIdentity(snapshot, observed)))
                retirement = contract.Error ?? SunderRpcServiceLocator.Stale();
            else
            {
                if (_bindings.Count + _retirements.Count >= MaximumBindings) throw Capacity();
                _bindings.Add(key, binding);
            }
        }
        if (retirement is not null) binding.Retire(retirement);
        return binding;
    }

    internal void Start(bool throwIfStopped = true)
    {
        lock (_gate)
        {
            if (_dispose is not null)
            {
                if (throwIfStopped) ThrowIfStopped();
                return;
            }
            if (_watchTask is not null) return;
            // The lazy observer outlives the discovery/invocation which first used
            // this DI scope. Never capture that caller's invocation/deadline context.
            if (ExecutionContext.IsFlowSuppressed()) _watchTask = Task.Run(ObserveAsync);
            else { using (ExecutionContext.SuppressFlow()) _watchTask = Task.Run(ObserveAsync); }
        }
    }

    internal async ValueTask EnsureCurrentAsync(SunderRpcServiceBinding binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start();
        var epoch = EnterQuery(requireConnected: false);
        // A retired binding may still be explicitly queried for a current authorization result.
        // Do not pre-cancel that read with the binding token. Success never revives the binding.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            SunderRpcProviderSnapshot? current;
            try
            {
                current = await _client.GetProviderAsync(binding.Snapshot.Endpoint, linked.Token).ConfigureAwait(false);
                if (current is not null) ValidateProvider(current);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                ProviderQueryFailed(exception, binding.Snapshot, epoch);
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            binding.ThrowIfRetired();
            if (current is null || current.State != SunderRpcProviderState.Active
                || !SunderRpcServiceLocator.SameIdentity(binding.Snapshot, current))
            {
                RetireBinding(binding, SunderRpcServiceLocator.Stale());
                binding.ThrowIfRetired();
            }
            await ReadyAsync(cancellationToken).ConfigureAwait(false);
            binding.ThrowIfRetired();
            lock (_gate) CheckEpoch(epoch);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && binding.IsRetired)
        {
            binding.ThrowIfRetired();
            throw;
        }
        finally { ExitQuery(); }
    }

    internal async ValueTask<bool> TryReportAsync(SunderRpcServiceBinding binding, Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        cancellationToken.ThrowIfCancellationRequested();
        try { await EnsureCurrentAsync(binding, cancellationToken).ConfigureAwait(false); }
        catch (SunderRpcException stale) when (stale.Error.Kind == SunderRpcErrorKind.StaleEndpoint) { return false; }
        var epoch = EnterQuery();
        // Reporting can itself fault/retire the provider. Do not cancel its acknowledgement with that token.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            binding.ThrowIfRetired();
            var reported = await _client.TryReportInvariantViolationAsync(binding.Snapshot.Endpoint, exception, linked.Token)
                .ConfigureAwait(false);
            if (reported) RetireBinding(binding, new(new(SunderRpcErrorKind.ProviderFaulted,
                "rpc.provider-faulted", "The exact service activation was reported for an invariant violation.")));
            return reported;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            QueryFailed(failure, binding.Snapshot.ContractId, epoch);
            throw;
        }
        finally { ExitQuery(); }
    }

    private void AddContract(string contractId)
    {
        if (!PackageId.TryParse(contractId, out _)) throw new ArgumentException("Invalid service contract identity.", nameof(contractId));
        lock (_gate)
        {
            ThrowIfStopped();
            if (_contracts.ContainsKey(contractId)) return;
            if (_contracts.Count >= MaximumContracts) throw Capacity();
            _contracts.Add(contractId, new());
        }
    }

    private async ValueTask ReadyAsync(CancellationToken cancellationToken)
    {
        Task ready;
        lock (_gate)
        {
            ThrowIfStopped();
            if (!_connected && _availabilityError is { } error) throw error;
            ready = _ready.Task;
        }
        await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> ReadyAfterDiscoveryAsync(CancellationToken cancellationToken)
    {
        Task ready;
        lock (_gate)
        {
            ThrowIfStopped();
            if (_connected) return _epoch;
            // The explicit discovery just succeeded on the receiver's current transport.
            // Do not substitute a cached failure from its previous transport. The single
            // observer must still subscribe/resnapshot before any new binding is issued.
            if (_ready.Task.IsCompleted) _ready = NewCompletion();
            ready = _ready.Task;
        }
        await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfStopped();
            if (!_connected) throw _availabilityError ?? Unavailable();
            return _epoch;
        }
    }

    private long EnterQuery(bool requireConnected = true)
        => EnterQuery(out _, requireConnected);

    private long EnterQuery(out bool connected, bool requireConnected = false)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (requireConnected && !_connected) throw _availabilityError ?? Unavailable();
            connected = _connected;
            _queries++;
            return _epoch;
        }
    }

    private void ExitQuery()
    {
        lock (_gate) if (--_queries == 0 && _dispose is not null) _queriesDrained.TrySetResult();
    }

    private void CheckEpoch(long epoch)
    {
        ThrowIfStopped();
        if (epoch != _epoch) throw _availabilityError ?? Unavailable();
    }

    private void ThrowIfStopped()
    {
        if (_dispose is not null) throw Unavailable("rpc.binding.lifetime-disposed", "The receiving service lifetime has ended.");
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void FailReady(SunderRpcException error)
    {
        if (_ready.TrySetException(error)) _ = _ready.Task.Exception;
    }
    private static SunderRpcProviderSnapshot Identity(SunderRpcProviderSnapshot snapshot)
        => snapshot with { CatalogRevision = 0, State = SunderRpcProviderState.Active, FaultCode = null };
    private static SunderRpcException Capacity() => new(new(SunderRpcErrorKind.ResourceExhausted,
        "rpc.binding.capacity", "The receiving service binding limit was reached."));
    private static SunderRpcException Unavailable(string code = "rpc.catalog.disconnected",
        string message = "The receiver's service catalog is unavailable.") => new(new(SunderRpcErrorKind.Unavailable, code, message));
    private static SunderRpcException InvalidCatalog() => new(new(SunderRpcErrorKind.Protocol,
        "rpc.binding.invalid-catalog", "The broker returned invalid service catalog metadata."));
    private static SunderRpcException Failure(Exception exception) => exception as SunderRpcException
        ?? (exception is JsonException ? InvalidCatalog() : Unavailable());

    private static void ValidateProvider(SunderRpcProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try { SunderRpcServiceLocator.Validate(snapshot); }
        catch (JsonException) { throw InvalidCatalog(); }
        if (!Enum.IsDefined(snapshot.State) || snapshot.CatalogRevision is < 0 or > 9_007_199_254_740_991)
            throw InvalidCatalog();
    }

    private sealed class ObservedContract
    {
        internal readonly Dictionary<string, SunderRpcProviderSnapshot> Providers = new(StringComparer.Ordinal);
        internal bool HasSnapshot;
        internal long Sequence;
        internal SunderRpcException? Error;
    }

    /// <summary>Retires all issued bindings and drains observation and in-flight SDK queries.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Retires all issued bindings and drains observation and in-flight SDK queries.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] retirements;
        Task? watch;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            completion = NewCompletion();
            _dispose = completion.Task;
            foreach (var binding in _bindings.Values.ToArray())
                RetireLocked(binding, Unavailable("rpc.binding.lifetime-disposed", "The receiving service lifetime has ended."));
            retirements = _retirements.ToArray();
            _bindings.Clear();
            _contracts.Clear();
            FailReady(Unavailable());
            if (_queries == 0) _queriesDrained.TrySetResult();
            watch = _watchTask;
        }
        _ = StopAsync(retirements, watch, completion);
        return new(completion.Task);
    }

    private async Task StopAsync(Task[] retirements, Task? watch, TaskCompletionSource completion)
    {
        try
        {
            try { await _stopping.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try
                {
                    if (watch is not null) await watch.ConfigureAwait(false);
                    await _queriesDrained.Task.ConfigureAwait(false);
                    await Task.WhenAll(retirements).ConfigureAwait(false);
                }
                finally { _stopping.Dispose(); }
            }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
