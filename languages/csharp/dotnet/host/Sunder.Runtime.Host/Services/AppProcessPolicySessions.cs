using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal sealed record AppPolicyLoadedPackage(RuntimePackagePolicySubject Package, string? RegistryOrigin, bool Development,
    string? ManifestSha256, IReadOnlyList<RuntimeRequiredProviderPredicate> RequiredProviders, IReadOnlyList<string> OwnerOnlyContracts,
    IReadOnlyList<RegistryPlatformCatalogIdentity> Platforms)
{
    public bool TrackedLiveness { get; init; }
}
internal sealed record AppPolicySessionFile(int SchemaVersion, string SessionId, RuntimeAppProcessIdentity Process, AppPolicyPeer Peer,
    string State, IReadOnlyList<AppPolicyLoadedPackage> Loaded, PolicyClockReading? ShutdownStarted, string? Cause)
{
    public IReadOnlyList<AppPolicyActivationRecord> Activations { get; init; } = [];
}
internal sealed record AppPolicyActivationPermit(string PermitId, RuntimePackagePolicySubject Package, PackageTargetDescriptor Target);
internal sealed record AppPolicyActivationRecord(Guid ActivationId, string State, IReadOnlyList<AppPolicyActivationPermit> Permits);

internal sealed partial class AppProcessPolicySessions(RuntimePackagePaths paths, RuntimeProtocolDescriptor protocol, IAppPolicySnapshotSource snapshots,
    PackagePolicyAdmission admission, PackageLifecyclePolicyCache policies, IPolicyContinuousClock clock, TimeProvider time,
    IAppPolicyProcessController processes, ILogger<AppProcessPolicySessions> logger,
    PackageSessionState? runtimeSessions = null, RuntimeRpcReadinessEvaluator? readiness = null) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(RegistryActivePolicyClient.Json) { IncludeFields = true };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private volatile bool _initialized;
    private volatile bool _failed;
    private const int MaximumSessions = 32;
    private const int MaximumLoaded = 128;

    public override async Task StartAsync(CancellationToken token)
    {
        await InitializeAsync(token);
        await base.StartAsync(token);
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_initialized) return;
            var root = Root();
            var files = Directory.EnumerateFiles(root, "*.json").Take(MaximumSessions + 1).ToArray();
            if (files.Length > MaximumSessions) throw Required();
            foreach (var file in files)
            {
                PackageStorePathGuard.EnsureSafe(file, paths.RootPath);
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length is <= 0 or > 1024 * 1024) throw Required();
                var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token);
                if (stream.ReadByte() != -1) throw Required();
                var record = JsonSerializer.Deserialize<AppPolicySessionFile>(bytes, Json) ?? throw Required();
                ValidateRecord(record);
                if (file != PathFor(record.SessionId)) throw Required();
                IAppPolicyProcessHandle? process = null;
                try { process = processes.Attach(record.Process, record.Peer, recovery: true); }
                catch (Exception error) { logger.LogWarning("App supervision recovery cannot attach a process ({FailureType}).", error.GetType().Name); }
                var entry = new Session(record, process);
                if (!_sessions.TryAdd(record.SessionId, entry)) throw Required();
                if (process?.Probe() == AppPolicyProcessState.ExitProven) await RecordExitAsync(entry);
                else if (record.Loaded.Count > 0) await RequestShutdownAsync(entry, PackagePolicyErrorCodes.AppProcessSupervisionRequired);
                else if (record.Process.RuntimeInstanceId != protocol.RuntimeInstanceId)
                {
                    process?.Dispose();
                    _sessions.TryRemove(record.SessionId, out _);
                    DurableJsonDocument.Delete(file);
                }
            }
            _initialized = true;
        }
        catch { _failed = true; throw; }
        finally { _gate.Release(); }
    }

    internal async Task<RuntimeAppPolicySessionDescriptor> OpenAsync(RuntimeAppPolicySessionOpenRequest request, AppPolicyPeer peer, CancellationToken token)
    {
        await InitializeAsync(token);
        await _gate.WaitAsync(token);
        try
        {
            EnsureAvailable();
            if (request.Process.RuntimeInstanceId != protocol.RuntimeInstanceId || request.Process.ProcessId != peer.ProcessId) throw Required();
            if (_sessions.Values.Any(entry => NeedsExit(entry.Record))) throw TerminationRequired();
            foreach (var existing in _sessions.Values.Where(value => value.Record.Process.ProcessId == peer.ProcessId))
            {
                if (existing.Record.Process != request.Process || !PeerMatches(existing.Record.Peer, peer)) throw Required();
                if (existing.Record.State != RuntimeAppPolicySessionStates.Active || existing.Process?.Probe() != AppPolicyProcessState.Alive) throw Required();
                return new(existing.Record.SessionId, existing.Record.Process, existing.Record.State);
            }
            if (_sessions.Count >= MaximumSessions) throw Required();
            var process = processes.Attach(request.Process, peer, recovery: false);
            try
            {
                var record = new AppPolicySessionFile(1, Guid.NewGuid().ToString("N"), request.Process, peer, RuntimeAppPolicySessionStates.Active, [], null, null);
                await PersistAsync(record);
                if (!_sessions.TryAdd(record.SessionId, new(record, process))) throw Required();
                return new(record.SessionId, record.Process, record.State);
            }
            catch { process.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }

    internal async Task<RuntimeAppPolicySessionStatus> HeartbeatAsync(RuntimeAppPolicySessionRequest request, AppPolicyPeer peer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ObserveActiveStatus(Get(request, peer)) is { } active) { token.ThrowIfCancellationRequested(); return active; }
        await _gate.WaitAsync(token);
        try
        {
            var entry = Get(request, peer);
            return await ObserveStatusAsync(entry);
        }
        finally { _gate.Release(); }
    }

    internal async Task<RuntimeAppPolicySessionStatus> GetStatusAsync(string sessionId, AppPolicyPeer peer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureAvailable();
        if (!_sessions.TryGetValue(sessionId, out var observed) || !PeerMatches(observed.Record.Peer, peer)) throw Required();
        if (ObserveActiveStatus(observed) is { } active) { token.ThrowIfCancellationRequested(); return active; }
        await _gate.WaitAsync(token);
        try
        {
            EnsureAvailable();
            if (!_sessions.TryGetValue(sessionId, out var entry) || !PeerMatches(entry.Record.Peer, peer)) throw Required();
            return await ObserveStatusAsync(entry);
        }
        finally { _gate.Release(); }
    }

    private RuntimeAppPolicySessionStatus? ObserveActiveStatus(Session entry)
    {
        // Fresh process identity and current package/platform security are the
        // authority for proof, not provider readiness or a cached "active" reply.
        // Those availability/retirement checks run in EnforceAsync. A healthy
        // heartbeat must not queue behind publication, persistence or cleanup.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var record = entry.Record;
            if (record.State != RuntimeAppPolicySessionStates.Active
                || entry.Process?.Probe() != AppPolicyProcessState.Alive || LoadedDenial(record) is not null) return null;
            var status = Status(record);
            EnsureAvailable();
            if (ReferenceEquals(record, entry.Record)
                && _sessions.TryGetValue(record.SessionId, out var current) && ReferenceEquals(entry, current)) return status;
        }
        // Never grant proof against a record superseded during validation, nor
        // spin indefinitely under lifecycle pressure. The caller may retry.
        throw new RuntimeUnavailableException("The App policy session changed during heartbeat validation. Retry with the current session.");
    }

    private async Task<RuntimeAppPolicySessionStatus> ObserveStatusAsync(Session entry)
    {
        var probe = entry.Process?.Probe() ?? AppPolicyProcessState.Unproven;
        if (probe == AppPolicyProcessState.ExitProven) await RecordExitAsync(entry);
        else if (probe == AppPolicyProcessState.Unproven && entry.Record.Loaded.Count > 0)
            await RequestShutdownAsync(entry, PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        else if (LoadedDenial(entry.Record, out var deniedPackage) is { } denial) await RequestShutdownAsync(entry, denial, deniedPackage);
        return Status(entry.Record);
    }

    internal async Task<RuntimeAppPackageLoadPermitResult> PermitAsync(RuntimeAppPackageLoadPermitRequest request, AppPolicyPeer peer, CancellationToken token)
    {
        if (request.SnapshotId is not { Length: 32 } id || snapshots.PolicySource(id) is not { } source
            || PackageUiSnapshotStore.PolicySubject(source) != request.Package) throw Required();
        await admission.RequireAsync(source, token);
        await _gate.WaitAsync(token);
        try
        {
            var entry = Get(request.Session, peer);
            if (_sessions.Values.Any(value => NeedsExit(value.Record))) throw TerminationRequired();
            await RequireLoadedAllowedAsync(entry);
            if (entry.Record.State != RuntimeAppPolicySessionStates.Active || entry.Process?.Probe() != AppPolicyProcessState.Alive
                || !ReferenceEquals(source, snapshots.PolicySource(id))) throw Required();
            admission.Require(source);
            if (Volatile.Read(ref _publicationFences) > 0) throw new RuntimeUnavailableException("App permit admission is fenced during Runtime publication.");
            ValidateTrackedRequest(request, source, entry);
            var development = source.Kind == PackageSourceKind.Dev;
            var subject = new RegistryPackageVersionIdentity(request.Package.PackageId, request.Package.Version, request.Package.PackageContentIdentity);
            var developmentPlatforms = development && source.DevelopmentImports is not { IsLocal: true }
                ? DevelopmentImportAuthorityStore.Platforms(source.Manifest!) : [];
            var origin = development ? developmentPlatforms.Length == 0 ? null : source.DevelopmentImports?.RegistryOrigin ?? throw Required()
                : source.RegistryPolicyOrigin ?? policies.FindPackageOrigin(subject) ?? throw Required();
            var requiredProviders = RuntimeRpcReadinessEvaluator.AppPredicates(source);
            var ownerOnly = RuntimeRpcReadinessEvaluator.AppOwnerOnlyContracts(source);
            var manifestHash = source.ContentIndex is null ? null : RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex);
            // Loaded security survives snapshot removal. Tracked activation
            // liveness ends only after the independent Host drain acknowledgement.
            if (requiredProviders.Count > 0 && (!snapshots.IsPublished(id)
                || development && requiredProviders.Any(predicate => !PackageCompatibilityAuthority.DevelopmentUseEnabled(source,
                    predicate.ConsumerRole, predicate.Contract.ContractId, predicate.Contract.ContractVersion))
                || !PredicatesSatisfied(request.Package.Version, development, manifestHash, requiredProviders, ownerOnly)))
                throw new RuntimeRequiredProviderException(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, [request.Package.PackageId], requiredProviders);
            var existing = entry.Record.Loaded.SingleOrDefault(value => value.Package == request.Package);
            if (existing is not null && (existing.Development != development || existing.RegistryOrigin != origin || existing.ManifestSha256 != manifestHash))
                throw Required();
            if (existing is null)
            {
                if (entry.Record.Loaded.Count >= MaximumLoaded) throw Required();
                var platforms = development ? developmentPlatforms : policies.PackageSubjects(origin!, subject).Platforms;
                var next = entry.Record with { Loaded = [.. entry.Record.Loaded, new(request.Package, origin, development, manifestHash, requiredProviders, ownerOnly, platforms)
                    { TrackedLiveness = request.AppActivationId.HasValue }] };
                await PersistAsync(next);
                entry.Record = next;
            }
            else if (existing.TrackedLiveness && request.AppActivationId is null)
            {
                // An untracked load can never borrow a later tracked retirement.
                var next = entry.Record with { Loaded = entry.Record.Loaded.Select(value => value == existing ? value with { TrackedLiveness = false } : value).ToArray() };
                await PersistAsync(next);
                entry.Record = next;
            }
            var permitId = await RecordTrackedPermitAsync(entry, request);
            // Recording may yield to a policy update or snapshot retirement. Keep the
            // conservative loaded record, but never return an obsolete permission.
            await RequireLoadedAllowedAsync(entry);
            if (entry.Process?.Probe() != AppPolicyProcessState.Alive || !ReferenceEquals(source, snapshots.PolicySource(id))) throw Required();
            admission.Require(source);
            var lease = development ? developmentPlatforms.Length == 0 ? null : policies.PlatformLease(origin!, developmentPlatforms) : policies.PackageLease(origin!, subject);
            return new(true, null, permitId, request.Session, request.Package, lease) { Development = development, AppActivationId = request.AppActivationId };
        }
        finally { _gate.Release(); }
    }

    internal async Task<RuntimeAppPolicySessionStatus> ShutdownAsync(RuntimeAppPolicySessionRequest request, AppPolicyPeer peer, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var entry = Get(request, peer);
            await RequestShutdownAsync(entry, PackagePolicyErrorCodes.AppProcessTerminationRequired);
            return Status(entry.Record);
        }
        finally { _gate.Release(); }
    }

    internal async Task EnforceAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var entry in _sessions.Values.ToArray())
            {
                var probe = entry.Process?.Probe() ?? AppPolicyProcessState.Unproven;
                if (probe == AppPolicyProcessState.ExitProven) { await RecordExitAsync(entry); continue; }
                if (probe == AppPolicyProcessState.Unproven && entry.Record.Loaded.Count > 0)
                    await RequestShutdownAsync(entry, PackagePolicyErrorCodes.AppProcessSupervisionRequired);
                if (entry.Record.State == RuntimeAppPolicySessionStates.Active)
                {
                    if (LoadedDenial(entry.Record, out var deniedPackage) is { } denial) await RequestShutdownAsync(entry, denial, deniedPackage);
                    else await RetireUnavailableActivationsAsync(entry);
                }
                if (!NeedsExit(entry.Record)) continue;
                PolicyClockReading? now = null;
                try { now = clock.Read(); } catch { }
                var since = entry.Record.ShutdownStarted;
                if (now is not null && since is not null && now.SameEpoch(since) && now.Ticks >= since.Ticks
                    && now.Ticks - since.Ticks < (Int128)now.Frequency * RuntimePolicyTiming.AppGracefulShutdownSeconds) continue;
                if (entry.Record.State != RuntimeAppPolicySessionStates.TerminationRequired)
                {
                    entry.Record = entry.Record with { State = RuntimeAppPolicySessionStates.TerminationRequired };
                    await PersistEnforcementAsync(entry.Record);
                }
                if (now is not null && entry.NextAttempt is { } next && now.SameEpoch(next) && now.Ticks < next.Ticks) continue;
                try { entry.Process?.Terminate(); }
                catch (Exception error) { logger.LogWarning("App process termination remains unproven ({FailureType}).", error.GetType().Name); }
                var delay = entry.Attempted ? RuntimePolicyTiming.AppForcedExitRetrySeconds
                    : RuntimePolicyTiming.AppForcedExitVerificationSeconds + RuntimePolicyTiming.AppForcedExitRetrySeconds;
                entry.Attempted = true;
                entry.NextAttempt = now is null ? null : now with { Ticks = checked(now.Ticks + now.Frequency * delay) };
                if (entry.Process?.Probe() == AppPolicyProcessState.ExitProven) await RecordExitAsync(entry);
            }
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnforceAsync(stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            { _failed = true; logger.LogError("App policy supervision failed closed ({FailureType}).", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
        }
    }

    private Session Get(RuntimeAppPolicySessionRequest request, AppPolicyPeer peer)
    {
        EnsureAvailable();
        if (request.RuntimeInstanceId != protocol.RuntimeInstanceId || !_sessions.TryGetValue(request.SessionId, out var entry)
            || request.AppGenerationId != entry.Record.Process.AppGenerationId || !PeerMatches(entry.Record.Peer, peer)) throw Required();
        return entry;
    }
    private static bool PeerMatches(AppPolicyPeer expected, AppPolicyPeer actual) => expected.ProcessId == actual.ProcessId
        && (expected.AuditToken is null && actual.AuditToken is null || expected.AuditToken is { } left && actual.AuditToken is { } right
            && left.ProcessId == right.ProcessId && left.ProcessVersion == right.ProcessVersion && left.EffectiveUserId == right.EffectiveUserId);
    private async Task RequestShutdownAsync(Session entry, string cause, string? deniedPackage = null)
    {
        if (NeedsExit(entry.Record)) return;
        PolicyClockReading? now = null;
        try { now = clock.Read(); } catch { }
        entry.Record = entry.Record with { State = RuntimeAppPolicySessionStates.ShutdownRequested, ShutdownStarted = now, Cause = cause };
        await PersistEnforcementAsync(entry.Record);
        logger.LogWarning("App process {ProcessId} shutdown requested: {Cause}; affected package {PackageId}; policy session {PolicySessionId}; Runtime instance {RuntimeInstanceId}.",
            entry.Record.Process.ProcessId, cause, deniedPackage, entry.Record.SessionId, protocol.RuntimeInstanceId);
    }
    private async Task RecordExitAsync(Session entry)
    {
        entry.Record = entry.Record with { State = RuntimeAppPolicySessionStates.ExitProven };
        await PersistAsync(entry.Record);
        entry.Process?.Dispose();
        _sessions.TryRemove(entry.Record.SessionId, out _);
        DurableJsonDocument.Delete(PathFor(entry.Record.SessionId));
    }
    private static bool NeedsExit(AppPolicySessionFile record) => record.State is RuntimeAppPolicySessionStates.ShutdownRequested or RuntimeAppPolicySessionStates.TerminationRequired;
    internal IReadOnlyList<PackagePolicySubjects> CapturePolicySubjects() => _sessions.Values.SelectMany(entry => entry.Record.Loaded)
        .Where(loaded => loaded.RegistryOrigin is not null)
        .Select(loaded => new PackagePolicySubjects(loaded.RegistryOrigin!,
            loaded.Development ? [] : [new(loaded.Package.PackageId, loaded.Package.Version, loaded.Package.PackageContentIdentity)], loaded.Platforms)).ToArray();

    private string? LoadedDenial(AppPolicySessionFile record) => LoadedDenial(record, out _);

    private string? LoadedDenial(AppPolicySessionFile record, out string? deniedPackage)
    {
        deniedPackage = null;
        foreach (var loaded in record.Loaded)
        {
            if (!loaded.Development)
            {
                try { policies.RequireActivation(loaded.RegistryOrigin!, new(loaded.Package.PackageId, loaded.Package.Version, loaded.Package.PackageContentIdentity)); }
                catch (PackagePolicyDeniedException error) { deniedPackage = loaded.Package.PackageId; return error.Code; }
            }
            else if (loaded.Platforms.Count > 0)
            {
                try { policies.RequirePlatformActivation(loaded.RegistryOrigin!, loaded.Platforms); }
                catch (PackagePolicyDeniedException error) { deniedPackage = loaded.Package.PackageId; return error.Code; }
            }
            // Provider liveness is availability, not permission to keep this OS
            // process alive. Exact affected activations are fenced/retired separately;
            // durable package/platform security denial still requires containment.
        }
        return null;
    }

    private async Task RequireLoadedAllowedAsync(Session entry)
    {
        if (LoadedDenial(entry.Record, out var deniedPackage) is not { } denial) return;
        await RequestShutdownAsync(entry, denial, deniedPackage);
        throw TerminationRequired();
    }

    internal void RequireLoaded(AppPolicyPeer peer, RuntimePackageSource source)
    {
        EnsureAvailable();
        var entry = _sessions.Values.SingleOrDefault(value => PeerMatches(value.Record.Peer, peer));
        if (entry is null) throw Required();
        var record = entry.Record;
        if (record.State != RuntimeAppPolicySessionStates.Active || LoadedDenial(record) is not null) throw TerminationRequired();
        if (entry.Process?.Probe() != AppPolicyProcessState.Alive) throw Required();
        var package = PackageUiSnapshotStore.PolicySubject(source) ?? throw Required();
        var loaded = record.Loaded.SingleOrDefault(value => value.Package == package) ?? throw Required();
        var development = source.Kind == PackageSourceKind.Dev;
        var origin = development ? source.DevelopmentImports is not { IsLocal: true } && source.Manifest is not null && DevelopmentImportAuthorityStore.Platforms(source.Manifest).Length > 0 ? source.DevelopmentImports?.RegistryOrigin : null
            : source.RegistryPolicyOrigin ?? policies.FindPackageOrigin(new(package.PackageId, package.Version, package.PackageContentIdentity));
        var manifestHash = source.ContentIndex is null ? null : RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex);
        if (loaded.Development != development || loaded.RegistryOrigin != origin || loaded.ManifestSha256 != manifestHash) throw Required();
        if (!PredicatesSatisfied(loaded.Package.Version, loaded.Development, loaded.ManifestSha256, loaded.RequiredProviders, loaded.OwnerOnlyContracts))
            throw new RuntimeUnavailableException($"Package '{loaded.Package.PackageId}' is unavailable because a required RPC provider is not ready. Other packages remain available; retry after the provider recovers.");
    }
    private bool PredicatesSatisfied(string version, bool development, string? manifestHash, IReadOnlyList<RuntimeRequiredProviderPredicate> predicates, IReadOnlyList<string> ownerOnly)
        => predicates.Count == 0 || readiness is not null && runtimeSessions is not null
            && runtimeSessions.AppPredicatesSatisfied(readiness, version, development, manifestHash, predicates, ownerOnly);
    private RuntimeAppPolicySessionStatus Status(AppPolicySessionFile record) => new(record.SessionId, record.State,
        NeedsExit(record) ? PackagePolicyErrorCodes.AppProcessTerminationRequired : null, NeedsExit(record), record.Loaded.Select(value => value.Package).ToArray())
        { Retirements = PendingRetirements(record.SessionId).Concat(UnavailableRetirements(record)).DistinctBy(value => value.AppActivationId).ToArray() };
    private void EnsureAvailable() { if (!_initialized || _failed) throw Required(); }
    private string Root()
    {
        var root = Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.AppPolicySessionsDirectory);
        PackageStorePathGuard.EnsureSafe(root, paths.RootPath); Directory.CreateDirectory(root); return root;
    }
    private string PathFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(Root(), id + ".json") : throw Required();
    private async Task PersistAsync(AppPolicySessionFile record)
    {
        ValidateRecord(record);
        if (JsonSerializer.SerializeToUtf8Bytes(record, Json).Length > 1024 * 1024 - 2) throw Required();
        try { await DurableJsonDocument.WriteAsync(PathFor(record.SessionId), record, Json, CancellationToken.None); }
        catch { _failed = true; throw; }
    }
    private async Task PersistEnforcementAsync(AppPolicySessionFile record)
    {
        try { await PersistAsync(record); }
        catch (Exception error) { logger.LogError("App shutdown evidence could not be persisted ({FailureType}); termination continues.", error.GetType().Name); }
    }
    private static void ValidateRecord(AppPolicySessionFile record)
    {
        if (record.SchemaVersion != 1 || !Guid.TryParseExact(record.SessionId, "N", out _) || record.Process.ProcessId != record.Peer.ProcessId
            || record.Process.ProcessId <= 0 || record.Process.AppGenerationId == Guid.Empty || record.Process.RuntimeInstanceId == Guid.Empty
            || !RegistryActivePolicyClient.Hash(record.Process.ExecutableSha256) || record.Loaded.Count > MaximumLoaded
            || record.Loaded.DistinctBy(value => value.Package).Count() != record.Loaded.Count
            || record.State is not (RuntimeAppPolicySessionStates.Active or RuntimeAppPolicySessionStates.ShutdownRequested or RuntimeAppPolicySessionStates.TerminationRequired or RuntimeAppPolicySessionStates.ExitProven)) throw Required();
        foreach (var loaded in record.Loaded)
        {
            if (!RegistryActivePolicyClient.Hash(loaded.Package.PackageContentIdentity)
                || (!loaded.Development || loaded.Platforms.Count > 0) && loaded.RegistryOrigin is null
                || loaded.RegistryOrigin is not null && loaded.RegistryOrigin != RegistryActivePolicyClient.Origin(loaded.RegistryOrigin)
                || loaded.Platforms.Count > RegistryPolicyLimits.MaximumPlatformSubjects
                || loaded.Platforms.Any(platform => platform.AuthorityId != "sunder.core" || !Sunder.Sdk.Packaging.SemanticVersion.TryParse(platform.ReleaseVersion, out _) || !RegistryActivePolicyClient.Hash(platform.CatalogSha256))
                || loaded.RequiredProviders.Count > 128 || loaded.OwnerOnlyContracts.Count > 128 || loaded.RequiredProviders.Any(predicate => predicate.ConsumerPackageId != loaded.Package.PackageId
                    || predicate.ConsumerRole != "app" || !RegistryActivePolicyClient.Hash(predicate.Contract.DescriptorSha256) || !RegistryActivePolicyClient.Hash(predicate.Contract.AbiSha256))) throw Required();
        }
        ValidateActivations(record);
    }
    private static PackagePolicyDeniedException Required() => new(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
    private static PackagePolicyDeniedException TerminationRequired() => new(PackagePolicyErrorCodes.AppProcessTerminationRequired);
    public override void Dispose() { _failed = true; foreach (var entry in _sessions.Values) entry.Process?.Dispose(); _gate.Dispose(); base.Dispose(); }
    private sealed class Session(AppPolicySessionFile record, IAppPolicyProcessHandle? process)
    {
        internal volatile AppPolicySessionFile Record = record;
        internal IAppPolicyProcessHandle? Process { get; } = process;
        internal PolicyClockReading? NextAttempt;
        internal bool Attempted;
        internal ConcurrentDictionary<Guid, AppPolicyActivationAuthority> Activations { get; } = new();
        internal ConcurrentDictionary<Guid, RuntimeAppActivationRetirementRequirement> UnavailableActivations { get; } = new();
    }
}
