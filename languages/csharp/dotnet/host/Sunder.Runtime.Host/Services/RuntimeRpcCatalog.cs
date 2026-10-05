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
    private ActivePackageSession? _candidateSession;
    private long? _candidateSessionGeneration;
    private IReadOnlyDictionary<string, RuntimeRpcPackageActivation>? _candidatePackages;
    private IReadOnlyDictionary<string, RuntimeRpcProviderActivation>? _candidateProviders;
    private IReadOnlyDictionary<string, RuntimeRpcProviderActivation>? _candidateEndpoints;
    private bool _candidateStarting;
    private readonly HashSet<string> _candidateComposed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _candidateReady = new(StringComparer.Ordinal);
    private RuntimeRpcPackageActivation? _recoveryCandidatePackage;
    private IReadOnlyDictionary<string, RuntimeRpcProviderActivation>? _recoveryCandidateProviders;
    private IReadOnlyDictionary<string, RuntimeRpcProviderActivation>? _recoveryCandidateEndpoints;
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

    internal void PublishAtomically(Action publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        lock (_gate)
        {
            ThrowIfDisposed();
            publication();
        }
    }

    public void ActivateSession(ActivePackageSession session, long sessionGeneration)
    {
        var packages = GetValidatedPackages(session);

        RuntimeRpcPackageActivation[] retired;
        RuntimeRpcPackageActivation[] displacedCandidate = [];
        RuntimeRpcPackageActivation? displacedRecoveryCandidate = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (sessionGeneration < _sessionGeneration)
            {
                return;
            }
            var nextRevision = checked(_revision + 1);
            displacedRecoveryCandidate = _recoveryCandidatePackage;
            ClearRecoveryCandidateUnderLock();
            RuntimeRpcSessionActivation next;
            if (ReferenceEquals(_candidateSession, session)
                && _candidateSessionGeneration == sessionGeneration
                && _candidatePackages is not null
                && _candidateProviders is not null
                && _candidateEndpoints is not null)
            {
                next = new RuntimeRpcSessionActivation(
                    _candidatePackages,
                    _candidateProviders,
                    _candidateEndpoints);
                foreach (var owner in next.Packages.Values)
                {
                    owner.PublishSessionGeneration(sessionGeneration);
                }
                foreach (var provider in next.Providers.Values)
                {
                    provider.UpdateSnapshot(provider.Snapshot with
                    {
                        CatalogRevision = nextRevision,
                        SessionGeneration = sessionGeneration,
                        State = SunderRpcProviderState.Active,
                        FaultCode = null,
                    });
                }
                ClearCandidateUnderLock();
            }
            else
            {
                displacedCandidate = _candidatePackages?.Values.ToArray() ?? [];
                ClearCandidateUnderLock();
                next = BuildSessionActivationUnderLock(
                    packages,
                    sessionGeneration,
                    nextRevision,
                    allowsPrePublicationLease: false);
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
            foreach (var pair in next.Packages) _packages.Add(pair.Key, pair.Value);
            foreach (var pair in next.Providers) _providers.Add(pair.Key, pair.Value);
            foreach (var pair in next.Endpoints) _endpoints.Add(pair.Key, pair.Value);
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
        foreach (var package in displacedCandidate) _ = package.Retire();
        if (displacedRecoveryCandidate is not null) _ = displacedRecoveryCandidate.Retire();
    }

    public void PrepareCandidateSession(ActivePackageSession session, long sessionGeneration)
    {
        var packages = GetValidatedPackages(session);
        RuntimeRpcPackageActivation[] displaced;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_candidateSession, session)
                && _candidateSessionGeneration == sessionGeneration)
            {
                return;
            }
            if (_recoveryCandidatePackage is not null)
            {
                throw new InvalidOperationException(
                    "An RPC whole-session candidate cannot overlap an exact package recovery candidate.");
            }
            if (sessionGeneration < _sessionGeneration)
            {
                throw new InvalidOperationException(
                    "An RPC candidate session cannot precede the active catalog generation.");
            }
            displaced = _candidatePackages?.Values.ToArray() ?? [];
            var candidate = BuildSessionActivationUnderLock(
                packages,
                sessionGeneration,
                checked(_revision + 1),
                allowsPrePublicationLease: true);
            _candidateSession = session;
            _candidateSessionGeneration = sessionGeneration;
            _candidatePackages = candidate.Packages;
            _candidateProviders = candidate.Providers;
            _candidateEndpoints = candidate.Endpoints;
        }
        foreach (var package in displaced) _ = package.Retire();
    }

    internal void PrepareStartingCandidate(ActivePackageSession session, long generation)
    {
        PrepareCandidateSession(session, generation);
        lock (_gate)
        {
            _candidateStarting = true;
            _candidateComposed.Clear();
            _candidateReady.Clear();
        }
    }

    internal void MarkCandidateProviderReady(ActivePackageSession session, string packageId, bool ready)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_candidateSession, session)) throw new InvalidOperationException("Candidate RPC generation changed during startup.");
            _candidateComposed.Add(packageId);
            if (ready) _candidateReady.Add(packageId);
        }
    }

    internal void CompleteStartingCandidate(ActivePackageSession session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_candidateSession, session) || session.LoadedPackageMap.Keys.Any(id => !_candidateReady.Contains(id)))
                throw new InvalidOperationException("The complete candidate provider graph is not ready.");
            _candidateStarting = false;
        }
    }

    public void DiscardCandidateSession(ActivePackageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RuntimeRpcPackageActivation[] retired;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_candidateSession, session)) return;
            retired = _candidatePackages?.Values.ToArray() ?? [];
            ClearCandidateUnderLock();
        }
        foreach (var package in retired) _ = package.Retire();
    }

    public void PreparePackageRecovery(
        ActiveLoadedPackage package,
        long sessionGeneration,
        long activationGeneration)
    {
        ArgumentNullException.ThrowIfNull(package);
        RuntimeRpcPackageActivation? displaced;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_candidateSession is not null)
            {
                throw new InvalidOperationException(
                    "An RPC package recovery candidate cannot overlap a whole-session candidate.");
            }
            if (_packages.ContainsKey(package.Descriptor.PackageId))
            {
                throw new InvalidOperationException(
                    "An RPC package recovery candidate cannot replace an active package endpoint in place.");
            }
            var conflictingProviderId = package.RpcProviders.Keys.FirstOrDefault(_providers.ContainsKey);
            if (conflictingProviderId is not null)
            {
                throw new InvalidOperationException(
                    $"Recovered RPC provider id '{conflictingProviderId}' conflicts with a healthy active provider.");
            }
            displaced = _recoveryCandidatePackage;
            ClearRecoveryCandidateUnderLock();
            var candidatePackages = new Dictionary<string, RuntimeRpcPackageActivation>(StringComparer.Ordinal);
            var candidateProviders = new Dictionary<string, RuntimeRpcProviderActivation>(StringComparer.Ordinal);
            var candidateEndpoints = new Dictionary<string, RuntimeRpcProviderActivation>(StringComparer.Ordinal);
            AddPackageActivationUnderLock(
                package,
                sessionGeneration,
                checked(_revision + 1),
                allowsPrePublicationLease: true,
                candidatePackages,
                candidateProviders,
                candidateEndpoints,
                activationGeneration);
            _recoveryCandidatePackage = candidatePackages.Values.Single();
            _recoveryCandidateProviders = candidateProviders;
            _recoveryCandidateEndpoints = candidateEndpoints;
        }
        if (displaced is not null)
        {
            _ = displaced.Retire();
        }
    }

    public Task DiscardPackageRecovery(string packageId, Guid activationId)
    {
        RuntimeRpcPackageActivation? retired = null;
        lock (_gate)
        {
            if (_disposed
                || _recoveryCandidatePackage is not { } candidate
                || !string.Equals(candidate.PackageId, packageId, StringComparison.Ordinal)
                || candidate.Package.RuntimeActivationId != activationId)
            {
                return Task.CompletedTask;
            }
            retired = candidate;
            ClearRecoveryCandidateUnderLock();
        }
        return retired.Retire();
    }

    private static ActiveLoadedPackage[] GetValidatedPackages(ActivePackageSession session)
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
        return packages;
    }

    private RuntimeRpcSessionActivation BuildSessionActivationUnderLock(
        IReadOnlyList<ActiveLoadedPackage> packages,
        long sessionGeneration,
        long catalogRevision,
        bool allowsPrePublicationLease)
    {
        var nextPackages = new Dictionary<string, RuntimeRpcPackageActivation>(StringComparer.Ordinal);
        var nextProviders = new Dictionary<string, RuntimeRpcProviderActivation>(StringComparer.Ordinal);
        var nextEndpoints = new Dictionary<string, RuntimeRpcProviderActivation>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            AddPackageActivationUnderLock(
                package,
                sessionGeneration,
                catalogRevision,
                allowsPrePublicationLease,
                nextPackages,
                nextProviders,
                nextEndpoints);
        }
        return new RuntimeRpcSessionActivation(nextPackages, nextProviders, nextEndpoints);
    }

    private void AddPackageActivationUnderLock(
        ActiveLoadedPackage package,
        long sessionGeneration,
        long catalogRevision,
        bool allowsPrePublicationLease,
        IDictionary<string, RuntimeRpcPackageActivation> packages,
        IDictionary<string, RuntimeRpcProviderActivation> providers,
        IDictionary<string, RuntimeRpcProviderActivation> endpoints,
        long? activationGeneration = null)
    {
        var owner = new RuntimeRpcPackageActivation(
            package,
            sessionGeneration,
            allowsPrePublicationLease,
            activationGeneration);
        packages.Add(package.Descriptor.PackageId, owner);
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
                registration.DescriptorSha256,
                registration.Contract?.AbiSha256
                ?? registration.Manifest?.AbiSha256
                ?? throw new InvalidDataException(
                    $"RPC provider '{registration.ProviderId}' is missing its ABI identity."),
                package.RuntimeActivationId,
                checked(++_activationEpoch),
                sessionGeneration,
                endpoint,
                catalogRevision,
                SunderRpcProviderState.Active);
            var provider = new RuntimeRpcProviderActivation(owner, registration, snapshot);
            providers.Add(registration.ProviderId, provider);
            endpoints.Add(endpoint.Value, provider);
        }
    }

    private void ClearCandidateUnderLock()
    {
        _candidateSession = null;
        _candidateSessionGeneration = null;
        _candidatePackages = null;
        _candidateProviders = null;
        _candidateEndpoints = null;
        _candidateStarting = false;
        _candidateComposed.Clear();
        _candidateReady.Clear();
    }

    private void ClearRecoveryCandidateUnderLock()
    {
        _recoveryCandidatePackage = null;
        _recoveryCandidateProviders = null;
        _recoveryCandidateEndpoints = null;
    }

    private bool TryGetCandidateProvidersUnderLock(
        RuntimeRpcPackageActivation caller,
        out IReadOnlyDictionary<string, RuntimeRpcProviderActivation> providers)
    {
        if (_candidatePackages is not null
            && _candidateProviders is not null
            && _candidatePackages.TryGetValue(caller.PackageId, out var candidate)
            && ReferenceEquals(candidate, caller))
        {
            providers = _candidateStarting ? _candidateProviders.Where(pair => CandidateProviderVisible(caller, pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : _candidateProviders;
            return true;
        }
        if (_recoveryCandidatePackage is not null
            && _recoveryCandidateProviders is not null
            && ReferenceEquals(_recoveryCandidatePackage, caller))
        {
            var combined = new Dictionary<string, RuntimeRpcProviderActivation>(
                _providers,
                StringComparer.Ordinal);
            foreach (var pair in _recoveryCandidateProviders)
            {
                combined.Add(pair.Key, pair.Value);
            }
            providers = combined;
            return true;
        }
        providers = null!;
        return false;
    }

    private bool TryGetCandidateEndpointsUnderLock(
        RuntimeRpcPackageActivation caller,
        out IReadOnlyDictionary<string, RuntimeRpcProviderActivation> endpoints)
    {
        if (_candidatePackages is not null
            && _candidateEndpoints is not null
            && _candidatePackages.TryGetValue(caller.PackageId, out var candidate)
            && ReferenceEquals(candidate, caller))
        {
            endpoints = _candidateStarting ? _candidateEndpoints.Where(pair => CandidateProviderVisible(caller, pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : _candidateEndpoints;
            return true;
        }
        if (_recoveryCandidatePackage is not null
            && _recoveryCandidateEndpoints is not null
            && ReferenceEquals(_recoveryCandidatePackage, caller))
        {
            var combined = new Dictionary<string, RuntimeRpcProviderActivation>(
                _endpoints,
                StringComparer.Ordinal);
            foreach (var pair in _recoveryCandidateEndpoints)
            {
                combined.Add(pair.Key, pair.Value);
            }
            endpoints = combined;
            return true;
        }
        endpoints = null!;
        return false;
    }

    public bool DeactivatePackage(
        string packageId,
        Guid activationId,
        bool faulted,
        string? faultCode = null)
        => TryDeactivatePackage(packageId, activationId, faulted, faultCode, out _);

    private bool CandidateProviderVisible(RuntimeRpcPackageActivation caller, RuntimeRpcProviderActivation provider)
        => _candidateReady.Contains(provider.Snapshot.PackageId)
            || caller.PackageId == provider.Snapshot.PackageId && _candidateComposed.Contains(caller.PackageId);

    public void ReconcileActiveSession(
        ActivePackageSession session,
        long sessionGeneration,
        string? recoveredPackageId = null,
        Action? commit = null)
    {
        var packages = GetValidatedPackages(session);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (sessionGeneration < _sessionGeneration)
            {
                return;
            }

            var expected = packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                StringComparer.Ordinal);
            foreach (var (packageId, active) in _packages)
            {
                if (!expected.TryGetValue(packageId, out var package)
                    || package.RuntimeActivationId != active.Package.RuntimeActivationId)
                {
                    throw new InvalidOperationException(
                        $"RPC package activation '{packageId}' does not match the exact active Runtime package.");
                }
            }

            var missing = expected.Keys.Except(_packages.Keys, StringComparer.Ordinal).ToArray();
            if (recoveredPackageId is null)
            {
                if (missing.Length != 0)
                {
                    throw new InvalidOperationException(
                        "RPC catalog reconciliation cannot create endpoints for an unrelated active package.");
                }
            }
            else if (missing.Length != 1
                     || !string.Equals(missing[0], recoveredPackageId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "RPC package recovery must add exactly the requested recovered package activation.");
            }

            RuntimeRpcPackageActivation? recoveredOwner = null;
            IReadOnlyDictionary<string, RuntimeRpcProviderActivation>? recoveredProviders = null;
            IReadOnlyDictionary<string, RuntimeRpcProviderActivation>? recoveredEndpoints = null;
            if (recoveredPackageId is not null)
            {
                recoveredOwner = _recoveryCandidatePackage;
                recoveredProviders = _recoveryCandidateProviders;
                recoveredEndpoints = _recoveryCandidateEndpoints;
                if (recoveredOwner is null
                    || recoveredProviders is null
                    || recoveredEndpoints is null
                    || !string.Equals(recoveredOwner.PackageId, recoveredPackageId, StringComparison.Ordinal)
                    || recoveredOwner.Package.RuntimeActivationId
                    != expected[recoveredPackageId].RuntimeActivationId)
                {
                    throw new InvalidOperationException(
                        "RPC package recovery publication does not match its prepared exact activation.");
                }
            }

            var nextRevision = checked(_revision + 1);
            commit?.Invoke();
            foreach (var owner in _packages.Values)
            {
                owner.AdvanceSessionGeneration(sessionGeneration);
            }
            foreach (var provider in _providers.Values.OrderBy(
                         static provider => provider.Snapshot.ProviderId,
                         StringComparer.Ordinal))
            {
                var snapshot = provider.Snapshot with
                {
                    CatalogRevision = nextRevision,
                    SessionGeneration = sessionGeneration,
                };
                provider.UpdateSnapshot(snapshot);
                Publish(nextRevision, SunderRpcCatalogEventKind.Activated, snapshot);
            }

            if (recoveredPackageId is not null)
            {
                recoveredOwner!.PublishSessionGeneration(sessionGeneration);
                foreach (var provider in recoveredProviders!.Values)
                {
                    provider.UpdateSnapshot(provider.Snapshot with
                    {
                        CatalogRevision = nextRevision,
                        SessionGeneration = sessionGeneration,
                        State = SunderRpcProviderState.Active,
                        FaultCode = null,
                    });
                }
                _packages.Add(recoveredPackageId, recoveredOwner);
                foreach (var pair in recoveredProviders) _providers.Add(pair.Key, pair.Value);
                foreach (var pair in recoveredEndpoints!) _endpoints.Add(pair.Key, pair.Value);
                ClearRecoveryCandidateUnderLock();
                foreach (var provider in recoveredProviders.Values.OrderBy(
                             static provider => provider.Snapshot.ProviderId,
                             StringComparer.Ordinal))
                {
                    Publish(nextRevision, SunderRpcCatalogEventKind.Added, provider.Snapshot);
                    Publish(nextRevision, SunderRpcCatalogEventKind.Activated, provider.Snapshot);
                }
            }

            _revision = nextRevision;
            _sessionGeneration = sessionGeneration;
        }
    }

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

    public SunderRpcCatalogSnapshot GetSnapshot(Func<RuntimeRpcProviderActivation, bool>? filter = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var providers = _providers.Values
                .Where(filter ?? AcceptAll)
                .Select(static provider => provider.Snapshot)
                .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
                .ToArray();
            return new SunderRpcCatalogSnapshot(_revision, _events.CurrentSequenceId, providers);
        }
    }

    public SunderRpcCatalogSnapshot GetSnapshot(
        RuntimeRpcPackageActivation caller,
        Func<RuntimeRpcProviderActivation, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            ThrowIfDisposed();
            var providers = TryGetCandidateProvidersUnderLock(caller, out var candidateProviders)
                ? candidateProviders
                : _providers;
            var snapshots = providers.Values
                .Where(filter ?? AcceptAll)
                .Select(static provider => provider.Snapshot)
                .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
                .ToArray();
            var revision = snapshots.Length == 0
                ? _revision
                : snapshots.Max(static provider => provider.CatalogRevision);
            return new SunderRpcCatalogSnapshot(revision, _events.CurrentSequenceId, snapshots);
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

    public bool TryGetEndpoint(
        RuntimeRpcPackageActivation caller,
        SunderRpcEndpointReference endpoint,
        out RuntimeRpcProviderActivation? provider,
        out bool stale)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TryGetCandidateEndpointsUnderLock(caller, out var candidateEndpoints))
            {
                if (candidateEndpoints.TryGetValue(endpoint.Value, out provider))
                {
                    stale = false;
                    return true;
                }
                stale = _staleEndpoints.TryGetValue(endpoint.Value, out provider);
                return false;
            }
            if (_endpoints.TryGetValue(endpoint.Value, out provider))
            {
                stale = false;
                return true;
            }
            stale = _staleEndpoints.TryGetValue(endpoint.Value, out provider);
            return false;
        }
    }

    public bool IsCandidateCaller(RuntimeRpcPackageActivation caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            ThrowIfDisposed();
            return TryGetCandidateProvidersUnderLock(caller, out _);
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
            if (_candidatePackages is not null
                && _candidatePackages.TryGetValue(packageId, out package)
                && package.Package.RuntimeActivationId == activationId)
            {
                return true;
            }
            if (_recoveryCandidatePackage is { } recoveryCandidate
                && string.Equals(recoveryCandidate.PackageId, packageId, StringComparison.Ordinal)
                && recoveryCandidate.Package.RuntimeActivationId == activationId)
            {
                package = recoveryCandidate;
                return true;
            }
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
            packages = _packages.Values
                .Concat(_candidatePackages?.Values ?? [])
                .Concat(_recoveryCandidatePackage is null ? [] : [_recoveryCandidatePackage])
                .ToArray();
            _packages.Clear();
            _providers.Clear();
            _endpoints.Clear();
            _staleEndpoints.Clear();
            _staleEndpointOrder.Clear();
            _retiredPackageCallbacks.Clear();
            ClearCandidateUnderLock();
            ClearRecoveryCandidateUnderLock();
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

    private static bool AcceptAll(RuntimeRpcProviderActivation _) => true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct RuntimeRpcPackageActivationKey(string PackageId, Guid ActivationId);

    private sealed record RuntimeRpcSessionActivation(
        IReadOnlyDictionary<string, RuntimeRpcPackageActivation> Packages,
        IReadOnlyDictionary<string, RuntimeRpcProviderActivation> Providers,
        IReadOnlyDictionary<string, RuntimeRpcProviderActivation> Endpoints);
}

internal sealed class RuntimeRpcPackageActivation : IRuntimeRpcCallerActivation
{
    private readonly IReadOnlyList<SunderPackageContractUseManifest> _contractUses;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callerCalls;
    private int _activeLeases;
    private bool _retired;
    private Task _retirementCallbacks = Task.CompletedTask;

    private long _sessionGeneration;
    private readonly long _activationGeneration;
    private int _allowsPrePublicationLease;

    public RuntimeRpcPackageActivation(
        ActiveLoadedPackage package,
        long sessionGeneration,
        bool allowsPrePublicationLease = false,
        long? activationGeneration = null)
    {
        Package = package;
        _contractUses = package.Source.Kind == PackageSourceKind.Dev
            ? Array.AsReadOnly(package.RpcContractUses.Where(use => PackageCompatibilityAuthority.DevelopmentUseEnabled(package.Source, use.Role!, use.ContractId!, use.ContractVersion!)).ToArray())
            : package.RpcContractUses;
        _sessionGeneration = sessionGeneration;
        _activationGeneration = activationGeneration ?? sessionGeneration;
        _allowsPrePublicationLease = allowsPrePublicationLease ? 1 : 0;
    }

    public ActiveLoadedPackage Package { get; }
    public long SessionGeneration => Interlocked.Read(ref _sessionGeneration);
    public long ActivationGeneration => _activationGeneration;
    public bool AllowsPrePublicationLease => Volatile.Read(ref _allowsPrePublicationLease) != 0;
    public string PackageId => Package.Descriptor.PackageId;
    public string PackageVersion => Package.Descriptor.Version;
    public Guid? ActivationId => Package.RuntimeActivationId;
    public string Role => SunderPackageFormat.RuntimeHostRole;
    public string ManifestSha256 => Package.RpcManifestSha256;
    public PackageSourceKind? SourceKind => Package.Source.Kind;
    public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts => Package.RpcContracts;
    public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses => _contractUses;
    public CancellationToken RetirementToken => _retirement.Token;
    public Task Drained => _drained.Task;

    public bool IsCurrent(PackageSessionLease sessionLease)
        => sessionLease.Generation == SessionGeneration
           && (AllowsPrePublicationLease
               || sessionLease.Session.TryGetLoadedPackage(PackageId, out var package)
               && package?.RuntimeActivationId == Package.RuntimeActivationId);

    internal void AdvanceSessionGeneration(long sessionGeneration)
    {
        var current = SessionGeneration;
        if (sessionGeneration < current)
        {
            throw new InvalidOperationException(
                "An RPC package activation cannot regress its Runtime session generation.");
        }
        Interlocked.Exchange(ref _sessionGeneration, sessionGeneration);
    }

    internal void PublishSessionGeneration(long sessionGeneration)
    {
        AdvanceSessionGeneration(sessionGeneration);
        Volatile.Write(ref _allowsPrePublicationLease, 0);
    }

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
            var callbacks = RuntimeCancellation.Signal(_retirement);
            _retirementCallbacks = drained
                ? callbacks
                : Task.WhenAll(callbacks, _drained.Task);
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
            provider.DescriptorSha256,
            provider.AbiSha256
            ?? throw new InvalidDataException("A Runtime RPC provider snapshot is missing its ABI identity."),
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
