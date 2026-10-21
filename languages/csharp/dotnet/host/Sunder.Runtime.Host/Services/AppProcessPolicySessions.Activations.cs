using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class AppProcessPolicySessions
{
    private int _publicationFences;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<RuntimeAppActivationRetirementRequirement>> _pendingRetirements = new(StringComparer.Ordinal);

    private static bool HasLiveObligation(AppPolicySessionFile record, AppPolicyLoadedPackage loaded)
        => !loaded.TrackedLiveness || record.Activations.Any(activation => activation.State != "retired"
            && activation.Permits.Any(permit => permit.Package == loaded.Package));

    private async Task RetireUnavailableActivationsAsync(Session entry)
    {
        var unavailable = entry.Record.Activations.Where(activation => activation.State == "active"
            && activation.Permits.Any(permit =>
            {
                var loaded = entry.Record.Loaded.Single(value => value.Package == permit.Package);
                return !PredicatesSatisfied(loaded.Package.Version, loaded.Development, loaded.ManifestSha256,
                    loaded.RequiredProviders, loaded.OwnerOnlyContracts);
            })).ToArray();
        if (unavailable.Length == 0) return;
        foreach (var activation in unavailable)
        {
            var permit = activation.Permits[^1];
            entry.UnavailableActivations.TryAdd(activation.ActivationId,
                new(entry.Record.SessionId, activation.ActivationId, permit.PermitId, permit.Package));
            // Fence immediately, without making heartbeat proof depend on package
            // cancellation callbacks. The shell must still prove its local cleanup
            // and obtain the independent exact retirement acknowledgement.
            if (entry.Activations.TryGetValue(activation.ActivationId, out var authority))
                _ = ObserveUnavailableRetirementAsync(authority, permit.Package.PackageId);
            logger.LogWarning("App activation {ActivationId} for package {PackageId} is retiring because a required provider is unavailable; the App process remains active.",
                activation.ActivationId, permit.Package.PackageId);
        }
        var ids = unavailable.Select(value => value.ActivationId).ToHashSet();
        var next = entry.Record with { Activations = entry.Record.Activations.Select(value => ids.Contains(value.ActivationId)
            ? value with { State = "retiring" } : value).ToArray() };
        await PersistAsync(next);
        entry.Record = next;
    }

    private async Task ObserveUnavailableRetirementAsync(AppPolicyActivationAuthority authority, string packageId)
    {
        try { await authority.RetireAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            logger.LogWarning("Unavailable App activation for package {PackageId} remains fenced because RPC cleanup was not proven ({FailureType}).",
                packageId, error.GetType().Name);
        }
    }

    private IReadOnlyList<RuntimeAppActivationRetirementRequirement> UnavailableRetirements(AppPolicySessionFile record)
        => _sessions.TryGetValue(record.SessionId, out var entry)
            ? entry.UnavailableActivations.Values.Where(value => record.Activations.Any(activation => activation.ActivationId == value.AppActivationId
                && activation.State != "retired")).ToArray() : [];

    private static void ValidateTrackedRequest(RuntimeAppPackageLoadPermitRequest request, RuntimePackageSource source, Session entry)
    {
        if (request.AppActivationId is null && request.AppTarget is null)
        {
            RequireObservationCapabilities(request, source, selected: null);
            return;
        }
        if (request.AppActivationId is not { } id || id == Guid.Empty || request.AppTarget is not { } target
            || !SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out var key)
            || target.Role != SunderPackageFormat.AppHostRole
            || !InstalledPackageProjection.Includes(source.AcquiredTargets, target.Role, target.Rid)
            || !SunderPackageTargetResolver.TryResolveTarget(source.Manifest!, key, out var selected)
            || selected is null || selected.Kind is not (SunderPackageFormat.AvaloniaTargetKind or SunderPackageFormat.WebTargetKind)
            || !RuntimeRpcPackageMetadata.TargetEquals(target, PackageTargetSelection.ToDescriptor(selected))) throw Required();
        RequireObservationCapabilities(request, source, selected);
        if (entry.Record.Activations.SingleOrDefault(value => value.ActivationId == id) is { } existing
            && (existing.State != "active" || existing.Permits.Any(permit => permit.Package.PackageId != request.Package.PackageId
                || !RuntimeRpcPackageMetadata.TargetEquals(permit.Target, target)))) throw Required();
    }

    private async Task<string> RecordTrackedPermitAsync(Session entry, RuntimeAppPackageLoadPermitRequest request)
    {
        if (request.AppActivationId is not { } id) return Guid.NewGuid().ToString("N");
        var activation = entry.Record.Activations.SingleOrDefault(value => value.ActivationId == id);
        if (activation?.Permits.SingleOrDefault(value => value.Package == request.Package) is { } existing) return existing.PermitId;
        if (entry.Record.Activations.Count >= 4096 || activation?.Permits.Count >= MaximumLoaded) throw Required();
        var permit = new AppPolicyActivationPermit(Guid.NewGuid().ToString("N"), request.Package, request.AppTarget!);
        var nextActivation = activation is null ? new(id, "active", (IReadOnlyList<AppPolicyActivationPermit>)[permit])
            : activation with { Permits = [.. activation.Permits, permit] };
        var next = entry.Record with { Activations = [.. entry.Record.Activations.Where(value => value.ActivationId != id), nextActivation] };
        await PersistAsync(next);
        entry.Activations.GetOrAdd(id, _ => new());
        entry.Record = next;
        return permit.PermitId;
    }

    internal AppPolicyActivationAuthority? RequireCallerPermit(AppPolicyPeer peer, RuntimePackageSource source,
        Guid activationId, string? permitId, PackageTargetDescriptor target)
    {
        RequireLoaded(peer, source);
        var entry = _sessions.Values.Single(value => PeerMatches(value.Record.Peer, peer));
        var subject = PackageUiSnapshotStore.PolicySubject(source)!;
        if (permitId is null)
        {
            if (entry.Record.Loaded.Single(value => value.Package == subject).TrackedLiveness) throw Required();
            return null;
        }
        var activation = entry.Record.Activations.SingleOrDefault(value => value.ActivationId == activationId);
        if (activation?.State != "active" || !activation.Permits.Any(value => value.PermitId == permitId && value.Package == subject
            && RuntimeRpcPackageMetadata.TargetEquals(value.Target, target))
            || !entry.Activations.TryGetValue(activationId, out var authority) || !authority.IsActive) throw Required();
        return authority;
    }

    internal async Task<RuntimeAppActivationRetirementResult> RetireActivationAsync(
        RuntimeAppActivationRetirementRequest request, AppPolicyPeer peer, CancellationToken token)
    {
        Session entry;
        Task drain;
        await _gate.WaitAsync(token);
        try
        {
            entry = Get(request.Session, peer);
            await RequireLoadedAllowedAsync(entry);
            if (entry.Record.State != RuntimeAppPolicySessionStates.Active || entry.Process?.Probe() != AppPolicyProcessState.Alive) throw Required();
            var activation = entry.Record.Activations.SingleOrDefault(value => value.ActivationId == request.AppActivationId);
            if (activation is null || !activation.Permits.Any(value => value.PermitId == request.LoadPermitId)) throw Required();
            if (activation.State == "retired") return new(request.Session, request.AppActivationId, request.LoadPermitId, "retired");
            if (!entry.Activations.TryGetValue(request.AppActivationId, out var authority)) throw Required();
            drain = authority.RetireAsync();
            if (activation.State != "retiring")
            {
                var next = entry.Record with { Activations = entry.Record.Activations.Select(value => value == activation ? value with { State = "retiring" } : value).ToArray() };
                await PersistAsync(next);
                entry.Record = next;
            }
        }
        finally { _gate.Release(); }
        // No policy lock is held while a provider, cancellation callback, stream,
        // opening transport or scope teardown is still running.
        try { await drain.WaitAsync(TimeSpan.FromSeconds(5), time, token); }
        catch (TimeoutException) { throw new RuntimeUnavailableException("App activation work did not drain within the five-second retirement budget."); }
        await _gate.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(entry, Get(request.Session, peer))) throw Required();
            await RequireLoadedAllowedAsync(entry);
            if (entry.Record.State != RuntimeAppPolicySessionStates.Active || entry.Process?.Probe() != AppPolicyProcessState.Alive) throw Required();
            var next = entry.Record with { Activations = entry.Record.Activations.Select(value => value.ActivationId == request.AppActivationId ? value with { State = "retired" } : value).ToArray() };
            await PersistAsync(next);
            entry.Record = next;
            return new(request.Session, request.AppActivationId, request.LoadPermitId, "retired");
        }
        finally { _gate.Release(); }
    }

    internal IReadOnlyList<RuntimeAppActivationRetirementRequirement> RetirementRequirements(ActivePackageSession candidate, bool sourceWithdrawal = false)
        => _sessions.Values.Select(entry => entry.Record).SelectMany(record => record.Activations.Where(activation => activation.State != "retired")
            .Where(activation => activation.Permits.Any(permit => !ObligationSatisfied(candidate, record.Loaded.Single(loaded => loaded.Package == permit.Package)))
                || sourceWithdrawal && !SourceRetained(candidate, record.Loaded.Single(loaded => loaded.Package == activation.Permits[^1].Package)))
            .Select(activation => new RuntimeAppActivationRetirementRequirement(record.SessionId, activation.ActivationId,
                activation.Permits[^1].PermitId, activation.Permits[^1].Package))).ToArray();

    private bool ObligationSatisfied(ActivePackageSession candidate, AppPolicyLoadedPackage loaded)
        => loaded.RequiredProviders.Count == 0 || readiness is not null && readiness.AppPredicatesSatisfied(candidate,
            loaded.Package.Version, loaded.Development, loaded.ManifestSha256, loaded.RequiredProviders, loaded.OwnerOnlyContracts);

    private static bool SourceRetained(ActivePackageSession candidate, AppPolicyLoadedPackage loaded)
        => candidate.TryGetPackageSource(loaded.Package.PackageId, out var source) && source is not null
            && PackageUiSnapshotStore.PolicySubject(source) == loaded.Package && (source.Kind == PackageSourceKind.Dev) == loaded.Development
            && (source.RegistryPolicyOrigin is null || source.RegistryPolicyOrigin == loaded.RegistryOrigin);

    internal void RegisterRetirementStage(string stageId, ActivePackageSession candidate, bool sourceWithdrawal = false)
        => _pendingRetirements[stageId] = RetirementRequirements(candidate, sourceWithdrawal).Select(value => value with { StageId = stageId }).ToArray();

    internal void ForgetRetirementStage(string stageId) => _pendingRetirements.TryRemove(stageId, out _);

    private IReadOnlyList<RuntimeAppActivationRetirementRequirement> PendingRetirements(string sessionId)
        => _pendingRetirements.Values.SelectMany(value => value).Where(value => value.PolicySessionId == sessionId
            && _sessions.TryGetValue(sessionId, out var session)
            && session.Record.Activations.Any(activation => activation.ActivationId == value.AppActivationId && activation.State != "retired")).ToArray();

    internal async Task<IDisposable> FencePublicationAsync(ActivePackageSession candidate, CancellationToken token, bool sourceWithdrawal = false)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var entry in _sessions.Values)
            {
                await RequireLoadedAllowedAsync(entry);
                var missing = entry.Record.Loaded.Where(loaded => HasLiveObligation(entry.Record, loaded) && !ObligationSatisfied(candidate, loaded)).ToArray();
                if (missing.Length > 0) throw new RuntimeRequiredProviderException(Sunder.Registry.Contracts.RpcReadinessErrorCodes.RequiredProviderUnsatisfied,
                    missing.Select(value => value.Package.PackageId).Distinct().ToArray(), missing.SelectMany(value => value.RequiredProviders).ToArray());
            }
            if (sourceWithdrawal && RetirementRequirements(candidate, true).Count > 0)
                throw new RuntimeConflictException("The owner release still requires exact App activation retirement.");
            Interlocked.Increment(ref _publicationFences);
            return new PublicationFence(this);
        }
        finally { _gate.Release(); }
    }

    private sealed class PublicationFence(AppProcessPolicySessions owner) : IDisposable
    {
        private AppProcessPolicySessions? _owner = owner;
        public void Dispose() { if (Interlocked.Exchange(ref _owner, null) is { } value) Interlocked.Decrement(ref value._publicationFences); }
    }

    private static void ValidateActivations(AppPolicySessionFile record)
    {
        if (record.Activations.Count > 4096 || record.Activations.DistinctBy(value => value.ActivationId).Count() != record.Activations.Count) throw Required();
        foreach (var activation in record.Activations)
            if (activation.ActivationId == Guid.Empty || activation.State is not ("active" or "retiring" or "retired")
                || activation.Permits.Count is 0 or > MaximumLoaded || activation.Permits.Any(permit => !Guid.TryParseExact(permit.PermitId, "N", out _)
                    || !record.Loaded.Any(loaded => loaded.Package == permit.Package))) throw Required();
    }
}
