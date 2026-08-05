using Sunder.Runtime.Contracts;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcCatalog : IDisposable
{
    internal const int ReplayCapacity = 256;
    internal const int SubscriberCapacity = 32;
    private const int MaximumStaleEndpoints = 2048;
    private readonly object _gate = new();
    private readonly BoundedReplayFeed<SunderRpcCatalogEvent> _events = new(ReplayCapacity, SubscriberCapacity);
    private readonly Dictionary<string, RuntimeRpcPackageActivation> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeRpcProviderActivation> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeRpcProviderActivation> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeRpcProviderActivation> _staleEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<RuntimeRpcPackageActivationKey, Task> _retiredPackageCallbacks = [];
    private readonly Queue<string> _staleEndpointOrder = new();
    private long _revision;
    private long _activationEpoch;
    private long _sessionGeneration;
    private bool _disposed;

    public long Revision
    {
        get
        {
            lock (_gate) return _revision;
        }
    }

    public long SessionGeneration
    {
        get
        {
            lock (_gate) return _sessionGeneration;
        }
    }

    public void ActivateSession(ActivePackageSession session, long sessionGeneration)
    {
        ArgumentNullException.ThrowIfNull(session);
        var packages = session.LoadedPackageMap.Values
            .Where(package => session.IsPackageEnabled(package.Descriptor.PackageId))
            .OrderBy(static package => package.Descriptor.PackageId, StringComparer.Ordinal)
            .ToArray();
        var duplicateProvider = packages.SelectMany(static package => package.RpcProviders.Keys)
            .GroupBy(static providerId => providerId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateProvider is not null)
        {
            throw new InvalidOperationException(
                $"RPC provider id '{duplicateProvider.Key}' is registered by more than one active package.");
        }

        RuntimeRpcPackageActivation[] retired;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (sessionGeneration < _sessionGeneration)
            {
                return;
            }
            var nextRevision = checked(_revision + 1);
            var nextPackages = new Dictionary<string, RuntimeRpcPackageActivation>(StringComparer.Ordinal);
            var nextProviders = new Dictionary<string, RuntimeRpcProviderActivation>(StringComparer.Ordinal);
            var nextEndpoints = new Dictionary<string, RuntimeRpcProviderActivation>(StringComparer.Ordinal);
            foreach (var package in packages)
            {
                var owner = new RuntimeRpcPackageActivation(package, sessionGeneration);
                nextPackages.Add(package.Descriptor.PackageId, owner);
                foreach (var registration in package.RpcProviders.Values.OrderBy(
                             static registration => registration.ProviderId,
                             StringComparer.Ordinal))
                {
                    var endpoint = new SunderRpcEndpointReference("rpc1_" + Guid.NewGuid().ToString("N"));
                    var snapshot = new SunderRpcProviderSnapshot(
                        package.Descriptor.PackageId,
                        package.Descriptor.Version,
                        registration.ProviderId,
                        registration.ContractId,
                        registration.ContractVersion,
                        registration.ContractSha256,
                        package.RuntimeActivationId,
                        checked(++_activationEpoch),
                        sessionGeneration,
                        endpoint,
                        nextRevision,
                        SunderRpcProviderState.Active);
                    var provider = new RuntimeRpcProviderActivation(owner, registration, snapshot);
                    nextProviders.Add(registration.ProviderId, provider);
                    nextEndpoints.Add(endpoint.Value, provider);
                }
            }

            retired = _packages.Values.ToArray();
            foreach (var provider in _providers.Values.OrderBy(
                         static provider => provider.Snapshot.ProviderId,
                         StringComparer.Ordinal))
            {
                var inactive = provider.Snapshot with
                {
                    CatalogRevision = nextRevision,
                    State = SunderRpcProviderState.Inactive,
                };
                provider.UpdateSnapshot(inactive);
                RememberStaleEndpoint(provider);
                Publish(nextRevision, SunderRpcCatalogEventKind.Deactivated, inactive);
                Publish(nextRevision, SunderRpcCatalogEventKind.Removed, inactive);
            }

            _packages.Clear();
            _providers.Clear();
            _endpoints.Clear();
            foreach (var pair in nextPackages) _packages.Add(pair.Key, pair.Value);
            foreach (var pair in nextProviders) _providers.Add(pair.Key, pair.Value);
            foreach (var pair in nextEndpoints) _endpoints.Add(pair.Key, pair.Value);
            foreach (var owner in retired)
            {
                RememberRetirementLocked(owner, owner.Retire());
            }
            _revision = nextRevision;
            _sessionGeneration = sessionGeneration;
            foreach (var provider in _providers.Values.OrderBy(
                         static provider => provider.Snapshot.ProviderId,
                         StringComparer.Ordinal))
            {
                Publish(nextRevision, SunderRpcCatalogEventKind.Added, provider.Snapshot);
                Publish(nextRevision, SunderRpcCatalogEventKind.Activated, provider.Snapshot);
            }
        }

    }

    public bool DeactivatePackage(
        string packageId,
        Guid activationId,
        bool faulted,
        string? faultCode = null)
        => TryDeactivatePackage(packageId, activationId, faulted, faultCode, out _);

    internal Task DeactivatePackageWithRetirement(
        string packageId,
        Guid activationId,
        bool faulted,
        string? faultCode = null)
    {
        _ = TryDeactivatePackage(packageId, activationId, faulted, faultCode, out var retirementCallbacks);
        return retirementCallbacks;
    }

    private bool TryDeactivatePackage(
        string packageId,
        Guid activationId,
        bool faulted,
        string? faultCode,
        out Task retirementCallbacks)
    {
        RuntimeRpcPackageActivation? owner;
        lock (_gate)
        {
            if (_disposed
                || !_packages.TryGetValue(packageId, out owner)
                || owner.Package.RuntimeActivationId != activationId)
            {
                retirementCallbacks = GetRetirementCallbacksLocked(packageId, activationId);
                return false;
            }
            var nextRevision = checked(++_revision);
            _packages.Remove(packageId);
            foreach (var provider in _providers.Values.Where(provider => ReferenceEquals(provider.Owner, owner)).ToArray())
            {
                _providers.Remove(provider.Snapshot.ProviderId);
                _endpoints.Remove(provider.Snapshot.Endpoint.Value);
                var snapshot = provider.Snapshot with
                {
                    CatalogRevision = nextRevision,
                    State = faulted ? SunderRpcProviderState.Faulted : SunderRpcProviderState.Inactive,
                    FaultCode = faulted ? NormalizeFaultCode(faultCode) : null,
                };
                provider.UpdateSnapshot(snapshot);
                RememberStaleEndpoint(provider);
                Publish(
                    nextRevision,
                    faulted ? SunderRpcCatalogEventKind.Faulted : SunderRpcCatalogEventKind.Deactivated,
                    snapshot);
                Publish(nextRevision, SunderRpcCatalogEventKind.Removed, snapshot);
            }
            retirementCallbacks = RememberRetirementLocked(owner, owner.Retire());
        }
        return true;
    }

    public void DeactivateAll(long sessionGeneration)
        => ActivateSession(ActivePackageSession.CreateEmpty(), sessionGeneration);

    public void VerifySessionGeneration(long sessionGeneration)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sessionGeneration != sessionGeneration
                || _packages.Values.Any(package => package.SessionGeneration != sessionGeneration)
                || _providers.Values.Any(provider => provider.Snapshot.SessionGeneration != sessionGeneration))
            {
                throw new InvalidOperationException(
                    $"RPC catalog generation {_sessionGeneration} does not match Runtime session generation {sessionGeneration}.");
            }
        }
    }

    public SunderRpcCatalogSnapshot GetSnapshot(Func<SunderRpcProviderSnapshot, bool>? filter = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var providers = _providers.Values.Select(static provider => provider.Snapshot)
                .Where(filter ?? AcceptAll)
                .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
                .ToArray();
            return new SunderRpcCatalogSnapshot(_revision, _events.CurrentSequenceId, providers);
        }
    }

    public RuntimeRpcCatalogEventPage GetEventPage(long afterRevision, long afterSequence)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var replay = _events.Snapshot(afterSequence, ReplayCapacity);
            var reset = replay.HistoryGap || afterRevision > _revision;
            var events = reset ? [] : replay.Items;
            return new RuntimeRpcCatalogEventPage(
                _revision,
                replay.SequenceId,
                events.Select(RuntimeRpcContractMapper.ToProtocol).ToArray(),
                reset);
        }
    }

    public RuntimeRpcCatalogSubscription Subscribe(long afterRevision, long afterSequence)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var subscription = _events.Subscribe(afterSequence);
            return new RuntimeRpcCatalogSubscription(
                subscription,
                subscription.HistoryGap || afterRevision > _revision,
                _revision,
                _events.CurrentSequenceId);
        }
    }

    public bool TryGetActiveEndpoint(
        SunderRpcEndpointReference endpoint,
        out RuntimeRpcProviderActivation? provider,
        out bool stale)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_endpoints.TryGetValue(endpoint.Value, out provider))
            {
                stale = false;
                return true;
            }
            stale = _staleEndpoints.TryGetValue(endpoint.Value, out provider);
            return false;
        }
    }

    public bool TryGetPackage(
        string packageId,
        Guid activationId,
        out RuntimeRpcPackageActivation? package)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _packages.TryGetValue(packageId, out package)
                   && package.Package.RuntimeActivationId == activationId;
        }
    }

    public void Dispose()
    {
        RuntimeRpcPackageActivation[] packages;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            packages = _packages.Values.ToArray();
            _packages.Clear();
            _providers.Clear();
            _endpoints.Clear();
            _staleEndpoints.Clear();
            _staleEndpointOrder.Clear();
            _retiredPackageCallbacks.Clear();
            _events.Complete();
        }
        foreach (var package in packages) _ = package.Retire();
    }

    private void Publish(long revision, SunderRpcCatalogEventKind kind, SunderRpcProviderSnapshot provider)
        => _events.Publish(sequence => new SunderRpcCatalogEvent(revision, sequence, kind, provider));

    private void RememberStaleEndpoint(RuntimeRpcProviderActivation provider)
    {
        var endpoint = provider.Snapshot.Endpoint.Value;
        if (_staleEndpoints.TryAdd(endpoint, provider))
        {
            _staleEndpointOrder.Enqueue(endpoint);
        }
        while (_staleEndpointOrder.Count > MaximumStaleEndpoints)
        {
            _staleEndpoints.Remove(_staleEndpointOrder.Dequeue());
        }
    }

    private Task GetRetirementCallbacksLocked(string packageId, Guid activationId)
        => _retiredPackageCallbacks.GetValueOrDefault(
               new RuntimeRpcPackageActivationKey(packageId, activationId))
           ?? Task.CompletedTask;

    private Task RememberRetirementLocked(RuntimeRpcPackageActivation owner, Task callbacks)
    {
        var key = new RuntimeRpcPackageActivationKey(
            owner.PackageId,
            owner.Package.RuntimeActivationId);
        if (_retiredPackageCallbacks.TryGetValue(key, out var existing)
            && !existing.IsCompletedSuccessfully)
        {
            callbacks = Task.WhenAll(existing, callbacks);
        }
        if (callbacks.IsCompletedSuccessfully)
        {
            _retiredPackageCallbacks.Remove(key);
            return callbacks;
        }
        _retiredPackageCallbacks[key] = callbacks;
        _ = RemoveRetirementWhenCompleteAsync(key, callbacks);
        return callbacks;
    }

    private async Task RemoveRetirementWhenCompleteAsync(
        RuntimeRpcPackageActivationKey key,
        Task callbacks)
    {
        await callbacks.ConfigureAwait(false);
        lock (_gate)
        {
            if (_retiredPackageCallbacks.TryGetValue(key, out var current)
                && ReferenceEquals(current, callbacks))
            {
                _retiredPackageCallbacks.Remove(key);
            }
        }
    }

    private static string NormalizeFaultCode(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 128
            ? "rpc.provider-fault"
            : value;

    private static bool AcceptAll(SunderRpcProviderSnapshot _) => true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct RuntimeRpcPackageActivationKey(string PackageId, Guid ActivationId);
}

internal sealed class RuntimeRpcPackageActivation : IRuntimeRpcCallerActivation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callerCalls;
    private int _activeLeases;
    private bool _retired;
    private Task _retirementCallbacks = Task.CompletedTask;

    public RuntimeRpcPackageActivation(ActiveLoadedPackage package, long sessionGeneration)
    {
        Package = package;
        SessionGeneration = sessionGeneration;
    }

    public ActiveLoadedPackage Package { get; }
    public long SessionGeneration { get; }
    public string PackageId => Package.Descriptor.PackageId;
    public string PackageVersion => Package.Descriptor.Version;
    public string ManifestSha256 => Package.RpcManifestSha256;
    public PackageSourceKind? SourceKind => Package.Source.Kind;
    public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts => Package.RpcContracts;
    public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses => Package.RpcContractUses;
    public CancellationToken RetirementToken => _retirement.Token;
    public Task Drained => _drained.Task;

    public bool IsCurrent(PackageSessionLease sessionLease)
        => sessionLease.Generation == SessionGeneration
           && sessionLease.Session.TryGetLoadedPackage(PackageId, out var package)
           && package?.RuntimeActivationId == Package.RuntimeActivationId;

    public bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease)
    {
        lock (_gate)
        {
            if (_retired || _callerCalls >= maximumCalls)
            {
                lease = null;
                return false;
            }
            _callerCalls++;
            _activeLeases++;
            lease = new RuntimeRpcActivationLease(_retirement.Token, ReleaseCaller);
            return true;
        }
    }

    public bool TryAcquireCallee(out RuntimeRpcActivationLease? lease)
    {
        lock (_gate)
        {
            if (_retired)
            {
                lease = null;
                return false;
            }
            _activeLeases++;
            lease = new RuntimeRpcActivationLease(_retirement.Token, ReleaseCallee);
            return true;
        }
    }

    public Task Retire()
    {
        var drained = false;
        lock (_gate)
        {
            if (_retired) return _retirementCallbacks;
            _retired = true;
            drained = _activeLeases == 0;
            _retirementCallbacks = RuntimeCancellation.Signal(_retirement);
        }
        if (drained) _drained.TrySetResult();
        return _retirementCallbacks;
    }

    private void ReleaseCaller()
    {
        var drained = false;
        lock (_gate)
        {
            if (_callerCalls > 0) _callerCalls--;
            if (_activeLeases > 0) _activeLeases--;
            drained = _retired && _activeLeases == 0;
        }
        if (drained) _drained.TrySetResult();
    }

    private void ReleaseCallee()
    {
        var drained = false;
        lock (_gate)
        {
            if (_activeLeases > 0) _activeLeases--;
            drained = _retired && _activeLeases == 0;
        }
        if (drained) _drained.TrySetResult();
    }
}

internal sealed class RuntimeRpcProviderActivation
{
    private readonly object _gate = new();
    private int _calleeCalls;
    private SunderRpcProviderSnapshot _snapshot;

    public RuntimeRpcProviderActivation(
        RuntimeRpcPackageActivation owner,
        RuntimeRpcProviderRegistration registration,
        SunderRpcProviderSnapshot snapshot)
    {
        Owner = owner;
        Registration = registration;
        _snapshot = snapshot;
    }

    public RuntimeRpcPackageActivation Owner { get; }
    public RuntimeRpcProviderRegistration Registration { get; }
    public SunderRpcProviderSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public bool TryAcquireCallee(int maximumCalls, out RuntimeRpcActivationLease? lease)
    {
        RuntimeRpcActivationLease? ownerLease;
        if (!Owner.TryAcquireCallee(out ownerLease) || ownerLease is null)
        {
            lease = null;
            return false;
        }
        lock (_gate)
        {
            if (_snapshot.State != SunderRpcProviderState.Active || _calleeCalls >= maximumCalls)
            {
                ownerLease.Dispose();
                lease = null;
                return false;
            }
            _calleeCalls++;
            lease = new RuntimeRpcActivationLease(
                Owner.RetirementToken,
                () =>
                {
                    ReleaseCallee();
                    ownerLease.Dispose();
                });
            return true;
        }
    }

    public void UpdateSnapshot(SunderRpcProviderSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
    }

    private void ReleaseCallee()
    {
        lock (_gate)
        {
            if (_calleeCalls > 0) _calleeCalls--;
        }
    }
}

internal sealed class RuntimeRpcActivationLease(CancellationToken retirementToken, Action release) : IDisposable
{
    private Action? _release = release;
    public CancellationToken RetirementToken { get; } = retirementToken;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed class RuntimeRpcCatalogSubscription(
    BoundedReplayFeed<SunderRpcCatalogEvent>.ReplayFeedSubscription<SunderRpcCatalogEvent> subscription,
    bool resetRequired,
    long revision,
    long sequence) : IAsyncDisposable
{
    public System.Threading.Channels.ChannelReader<SunderRpcCatalogEvent> Reader => subscription.Reader;
    public bool ResetRequired { get; } = resetRequired;
    public long Revision { get; } = revision;
    public long Sequence { get; } = sequence;
    public ValueTask DisposeAsync() => subscription.DisposeAsync();
}

internal static class RuntimeRpcContractMapper
{
    public static RuntimeRpcProviderDescriptor ToProtocol(SunderRpcProviderSnapshot provider)
        => new(
            provider.PackageId,
            provider.PackageVersion,
            provider.ProviderId,
            provider.ContractId,
            provider.ContractVersion,
            provider.ContractSha256,
            provider.ActivationId,
            provider.ActivationEpoch,
            provider.SessionGeneration,
            provider.Endpoint.Value,
            provider.CatalogRevision,
            provider.State switch
            {
                SunderRpcProviderState.Active => RuntimeRpcProviderState.Active,
                SunderRpcProviderState.Inactive => RuntimeRpcProviderState.Inactive,
                _ => RuntimeRpcProviderState.Faulted,
            },
            provider.FaultCode);

    public static RuntimeRpcCatalogEventDescriptor ToProtocol(SunderRpcCatalogEvent value)
        => new(
            value.Revision,
            value.Sequence,
            value.Kind switch
            {
                SunderRpcCatalogEventKind.Added => RuntimeRpcCatalogEventKind.Added,
                SunderRpcCatalogEventKind.Removed => RuntimeRpcCatalogEventKind.Removed,
                SunderRpcCatalogEventKind.Activated => RuntimeRpcCatalogEventKind.Activated,
                SunderRpcCatalogEventKind.Deactivated => RuntimeRpcCatalogEventKind.Deactivated,
                SunderRpcCatalogEventKind.Faulted => RuntimeRpcCatalogEventKind.Faulted,
                _ => RuntimeRpcCatalogEventKind.ResetRequired,
            },
            value.Provider is null ? null : ToProtocol(value.Provider));
}
