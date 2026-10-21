using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackagePolicyEntry(long Revision, RegistryPackagePolicyDecision Decision, RuntimePolicyLeaseStatus Lease);
internal sealed record PlatformPolicyEntry(long Revision, RegistryPlatformPolicyDecision Decision, RuntimePolicyLeaseStatus Lease);
internal sealed record PackagePolicyOriginFile(int SchemaVersion, long Generation, long AuthorityGeneration, long ClockGeneration, string Origin, RegistryPolicyIdentity PackagePolicy,
    PolicyClockReading HighWater, long LastRequestStarted, string LastNonce, bool ClockInvalidated,
    IReadOnlyDictionary<string, PackagePolicyEntry> Packages, IReadOnlyDictionary<string, PlatformPolicyEntry> Platforms)
{
    private IReadOnlyDictionary<string, PackagePolicyEntry> _packages = Freeze(Packages);
    private IReadOnlyDictionary<string, PlatformPolicyEntry> _platforms = Freeze(Platforms);
    public IReadOnlyDictionary<string, PackagePolicyEntry> Packages { get => _packages; init => _packages = Freeze(value); }
    public IReadOnlyDictionary<string, PlatformPolicyEntry> Platforms { get => _platforms; init => _platforms = Freeze(value); }
    internal static IReadOnlyDictionary<string, T> Freeze<T>(IReadOnlyDictionary<string, T> entries)
        => new ReadOnlyDictionary<string, T>(entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

internal sealed class PackagePolicyDeniedException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

internal enum PackagePolicyPersistencePoint { IntentDurable, SnapshotDurable }
internal interface IPackagePolicyFaultInjector { void Hit(PackagePolicyPersistencePoint point); }

internal sealed partial class PackageLifecyclePolicyCache(RuntimePackagePaths paths, IPolicyContinuousClock clock, IPackagePolicyFaultInjector? faultInjector = null)
{
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    internal const int MaximumSubjects = 8192;
    private readonly ConcurrentDictionary<string, PackagePolicyOriginFile> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _persistenceFailures = new(StringComparer.Ordinal);
    private readonly object _clockGate = new();
    private readonly Dictionary<string, PolicyClockReading> _observedClocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, bool Invalid)> _clockStates = new(StringComparer.Ordinal);
    internal event Action? PolicyChanged;

    internal long ClockGeneration(string origin)
    {
        lock (_clockGate) return _clockStates.GetValueOrDefault(RegistryActivePolicyClient.Origin(origin)).Generation;
    }

    internal long BeginRefresh(string origin, PolicyClockReading started)
    {
        lock (_clockGate)
        {
            if (ClockRegressed(origin, started)) MarkClockInvalid(origin);
            return _clockStates.GetValueOrDefault(origin).Generation;
        }
    }

    private bool ClockRegressed(string origin, PolicyClockReading now)
        => _snapshots.TryGetValue(origin, out var state) && (!now.SameEpoch(state.HighWater) || now.Ticks < state.HighWater.Ticks)
            || _observedClocks.TryGetValue(origin, out var observed) && (!now.SameEpoch(observed) || now.Ticks < observed.Ticks);

    internal string? FindPackageOrigin(RegistryPackageVersionIdentity package)
    {
        var key = RegistryActivePolicyClient.PackageKey(package);
        var origins = _snapshots.Where(pair => pair.Value.Packages.ContainsKey(key)).Select(pair => pair.Key).Take(2).ToArray();
        return origins.Length == 1 ? origins[0] : null;
    }

    internal RuntimePolicyLeaseStatus PackageLease(string origin, RegistryPackageVersionIdentity package)
    {
        RequireActivation(origin, package);
        return _snapshots[RegistryActivePolicyClient.Origin(origin)].Packages[RegistryActivePolicyClient.PackageKey(package)].Lease;
    }

    internal PackagePolicySubjects PackageSubjects(string origin, RegistryPackageVersionIdentity package)
    {
        origin = RegistryActivePolicyClient.Origin(origin);
        if (!_snapshots.TryGetValue(origin, out var state)
            || !state.Packages.TryGetValue(RegistryActivePolicyClient.PackageKey(package), out var entry))
            throw Denied(PackagePolicyErrorCodes.PolicyRequired);
        return new(origin, [package], entry.Decision.PlatformDependencies.ToArray());
    }

    internal void RequirePlan(string origin, RegistryPolicyIdentity packagePolicy)
    {
        origin = RegistryActivePolicyClient.Origin(origin);
        if (_persistenceFailures.ContainsKey(origin) || !_snapshots.TryGetValue(origin, out var state)
            || state.PackagePolicy != packagePolicy)
            throw Denied(PackagePolicyErrorCodes.PlanStale);
    }

    internal async Task PersistClockInvalidationsAsync(CancellationToken token)
    {
        string[] origins;
        lock (_clockGate) origins = _clockStates.Where(pair => pair.Value.Invalid).Select(pair => pair.Key).ToArray();
        foreach (var origin in origins)
        {
            if (!_snapshots.TryGetValue(origin, out var state) || state.ClockInvalidated) continue;
            await PersistInvalidationAsync(origin, token);
        }
    }

    internal async Task LoadKnownOriginsAsync(CancellationToken token, bool checkpointClock = true)
    {
        var root = Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory);
        PackageStorePathGuard.EnsureSafe(root, paths.RootPath);
        if (!Directory.Exists(root)) return;
        var files = Directory.EnumerateFiles(root, "*.json").Take(257).ToArray();
        RegistryActivePolicyClient.Require(files.Length <= 256, "Policy origin count exceeds its bound.");
        foreach (var file in files)
        {
            PackageStorePathGuard.EnsureSafe(file, paths.RootPath);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            RegistryActivePolicyClient.Require(stream.Length is > 0 and <= MaximumFileBytes, "Policy cache exceeds its byte bound.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
            RegistryActivePolicyClient.Require(stream.ReadByte() == -1, "Policy cache changed during origin discovery.");
            var state = JsonSerializer.Deserialize<PackagePolicyOriginFile>(bytes, RegistryActivePolicyClient.Json)
                ?? throw new InvalidDataException("Policy cache is empty.");
            RegistryActivePolicyClient.Require(file == FilePath(state.Origin) || file == IntentPath(FilePath(state.Origin)), "Policy cache filename does not match its origin.");
            // Discovery grants nothing. The authoritative read is under that origin's
            // lock and includes the durable generation and continuous-clock fence.
            try { await LoadAsync(state.Origin, checkpointClock, token); }
            catch (PackagePolicyDeniedException) { }
        }
    }

    public async Task<RegistryPolicySynchronizationRequest> CreateRequestAsync(string origin, IReadOnlyList<RegistryPackageVersionIdentity> packages,
        IReadOnlyList<RegistryPlatformCatalogIdentity> platforms, CancellationToken token)
    {
        var state = await LoadAsync(origin, checkpointClock: false, token);
        return new("", packages, platforms, state?.PackagePolicy);
    }

    public async Task ObserveAsync(RegistryPolicyObservation observation, CancellationToken token = default)
    {
        if (!observation.TryConsume()) throw new InvalidDataException("A policy response cannot be accepted twice.");
        var response = observation.Response; var origin = RegistryActivePolicyClient.Origin(response.RegistryOrigin);
        RegistryActivePolicyClient.ValidateResponse(origin, observation.Request, response);
        var filePath = FilePath(origin);
        using var lease = await RuntimeFileLock.AcquireExclusiveAsync(filePath + ".lock", token);
        var previous = await ReadFileAsync(origin, filePath, token);
        var now = clock.Read();
        RegistryActivePolicyClient.ValidateClock(now);
        bool invalidateOldLeases;
        lock (_clockGate)
        {
            if (ClockRegressed(origin, now)) MarkClockInvalid(origin);
            RegistryActivePolicyClient.Require(observation.ClockGeneration == _clockStates.GetValueOrDefault(origin).Generation,
                "Policy refresh predates a clock discontinuity.");
            invalidateOldLeases = _clockStates.GetValueOrDefault(origin).Invalid || previous?.ClockInvalidated == true
                || previous is not null && (!now.SameEpoch(previous.HighWater) || now.Ticks < previous.HighWater.Ticks);
        }
        RegistryActivePolicyClient.Require(now.SameEpoch(observation.Started) && now.SameEpoch(observation.Completed)
            && now.Ticks >= observation.Completed.Ticks && observation.Completed.Ticks >= observation.Started.Ticks, "Clock continuity was lost before policy acceptance.");
        RegistryActivePolicyClient.Require((Int128)observation.Completed.Ticks - observation.Started.Ticks <= (Int128)now.Frequency * RuntimePolicyTiming.RequestDeadlineSeconds
            && RegistryActivePolicyClient.Hash(observation.ResponseSha256), "Policy response lacks timely bounded observation evidence.");
        if (previous is not null)
        {
            RegistryActivePolicyClient.Require(response.Nonce != previous.LastNonce, "The policy nonce was already accepted.");
            RegistryActivePolicyClient.Require(invalidateOldLeases || observation.Started.Ticks >= previous.LastRequestStarted, "A reordered policy response cannot replace a newer observation.");
            // Compare with this client's retained database observations, not an
            // independent authority capable of proving absence of database rollback.
            RegistryActivePolicyClient.Require(response.PackagePolicy.Revision >= previous.PackagePolicy.Revision
                && (response.PackagePolicy.Revision != previous.PackagePolicy.Revision || response.PackagePolicy == previous.PackagePolicy),
                "Policy observation is older than or conflicts with the cached identity.");
        }
        var deadline = checked(observation.Started.Ticks + checked(observation.Started.Frequency * RuntimePolicyTiming.LeaseSeconds));
        RegistryActivePolicyClient.Require(now.Ticks < deadline, "Policy response arrived after its local lease deadline.");
        var watermark = new RuntimePolicyWatermark(origin, response.PackagePolicy.Revision, response.PackagePolicy.Fingerprint);
        var status = new RuntimePolicyLeaseStatus(RuntimePolicyLeaseStates.Valid, null, watermark, response.Nonce, observation.ResponseSha256,
            response.ObservedAtUtc, response.ValidUntilUtc, new(now.BootSessionId, now.Source, now.Frequency, observation.Started.Ticks, deadline, now.Ticks, now.VirtualMachineGeneration));
        if (invalidateOldLeases && previous is not null) previous = InvalidateLeases(previous);
        var packages = Copy(previous?.Packages); var platforms = Copy(previous?.Platforms);
        foreach (var decision in response.Packages)
        {
            var key = RegistryActivePolicyClient.PackageKey(decision.Package);
            foreach (var old in packages.Values.Where(value => value.Decision.Package.PackageId == decision.Package.PackageId))
            {
                RegistryActivePolicyClient.Require(old.Decision.Package.Version != decision.Package.Version || old.Decision.Package == decision.Package, "Immutable package subject identity changed.");
                RegistryActivePolicyClient.Require(old.Decision.PackageState != "SecurityBlocked" || decision.PackageState == "SecurityBlocked", "A terminal package block cannot be broadened.");
            }
            if (packages.TryGetValue(key, out var existing))
            {
                RegistryActivePolicyClient.Require(existing.Decision.VersionAvailability != "SecurityBlocked" || decision.VersionAvailability == "SecurityBlocked", "A terminal version block cannot be broadened.");
                RegistryActivePolicyClient.Require(existing.Revision != response.PackagePolicy.Revision || Same(existing.Decision, decision), "Same-revision package decisions conflict.");
            }
            packages[key] = new(response.PackagePolicy.Revision, decision, status);
        }
        foreach (var decision in response.PlatformCatalogs)
        {
            var key = RegistryActivePolicyClient.PlatformKey(decision.Catalog);
            RegistryActivePolicyClient.Require(platforms.Values.All(value => value.Decision.Catalog.AuthorityId != decision.Catalog.AuthorityId
                || value.Decision.Catalog.ReleaseVersion != decision.Catalog.ReleaseVersion || value.Decision.Catalog == decision.Catalog), "Immutable platform catalog identity changed.");
            if (platforms.TryGetValue(key, out var existing))
            {
                RegistryActivePolicyClient.Require(existing.Decision.LifecycleState != "Revoked" || Same(existing.Decision, decision), "A revoked platform catalog cannot change or broaden.");
                RegistryActivePolicyClient.Require(existing.Revision != response.PackagePolicy.Revision || Same(existing.Decision, decision), "Same-revision platform decisions conflict.");
            }
            platforms[key] = new(response.PackagePolicy.Revision, decision, status);
        }
        var next = new PackagePolicyOriginFile(1, checked((previous?.Generation ?? 0) + 1), checked((previous?.AuthorityGeneration ?? 0) + 1),
            checked((previous?.ClockGeneration ?? 0) + (invalidateOldLeases && previous?.ClockInvalidated != true ? 1 : 0)), origin, response.PackagePolicy,
            now, observation.Started.Ticks, response.Nonce, false, Sorted(packages), Sorted(platforms));
        await PersistAsync(filePath, next, token);
        var accepted = false;
        lock (_clockGate)
        {
            if (observation.ClockGeneration == _clockStates.GetValueOrDefault(origin).Generation)
            {
                _clockStates[origin] = (observation.ClockGeneration, false);
                if (!_observedClocks.TryGetValue(origin, out var last) || invalidateOldLeases || !now.SameEpoch(last) || now.Ticks > last.Ticks)
                    _observedClocks[origin] = now;
                _persistenceFailures.TryRemove(origin, out _);
                _snapshots[origin] = next;
                accepted = true;
            }
        }
        if (accepted)
        {
            PolicyChanged?.Invoke();
            return;
        }
        // A concurrent endpoint gate observed discontinuity while the replace was in flight.
        next = InvalidateLeases(next) with { Generation = checked(next.Generation + 1), AuthorityGeneration = checked(next.AuthorityGeneration + 1), ClockGeneration = checked(next.ClockGeneration + 1), ClockInvalidated = true };
        await PersistAsync(filePath, next, token);
        _snapshots[origin] = next;
        throw Denied(PackagePolicyErrorCodes.PolicyClockInvalid);

    }

    public async Task RequireActivationAsync(string origin, RegistryPackageVersionIdentity package, CancellationToken token)
    {
        await LoadAsync(origin, checkpointClock: true, token);
        RequireActivation(origin, package);
    }

    internal Task CheckpointClockAsync(string origin, CancellationToken token) => LoadAsync(origin, checkpointClock: true, token);

    public void RequireActivation(string origin, RegistryPackageVersionIdentity package)
    {
        origin = RegistryActivePolicyClient.Origin(origin);
        if (_persistenceFailures.ContainsKey(origin) || !_snapshots.TryGetValue(origin, out var state)) throw Denied(PackagePolicyErrorCodes.PolicyRequired);
        if (state.Packages.Values.Any(value => value.Decision.Package.PackageId == package.PackageId && value.Decision.PackageState == "SecurityBlocked")) throw Denied(PackagePolicyErrorCodes.SecurityBlocked);
        if (!state.Packages.TryGetValue(RegistryActivePolicyClient.PackageKey(package), out var entry)) throw Denied(PackagePolicyErrorCodes.PolicyRequired);
        if (entry.Decision.VersionAvailability == "SecurityBlocked") throw Denied(PackagePolicyErrorCodes.SecurityBlocked);
        if (entry.Decision.Disposition != "Allowed") throw Denied(entry.Decision.ErrorCode ?? PackagePolicyErrorCodes.PolicyRequired);
        var now = ReadClock(origin, state.HighWater);
        RequireLease(state, entry.Lease, now);
        foreach (var subject in entry.Decision.PlatformDependencies)
        {
            if (!state.Platforms.TryGetValue(RegistryActivePolicyClient.PlatformKey(subject), out var platform)) throw Denied(PackagePolicyErrorCodes.PolicyRequired);
            if (platform.Decision.Disposition != "Allowed") throw Denied(platform.Decision.ErrorCode ?? PackagePolicyErrorCodes.PolicyRequired);
            RequireLease(state, platform.Lease, now);
        }
    }

    internal void RequirePlatformActivation(string origin, IReadOnlyList<RegistryPlatformCatalogIdentity> subjects)
    {
        origin = RegistryActivePolicyClient.Origin(origin);
        if (_persistenceFailures.ContainsKey(origin) || !_snapshots.TryGetValue(origin, out var state)) throw Denied(PackagePolicyErrorCodes.PolicyRequired);
        var now = ReadClock(origin, state.HighWater);
        foreach (var subject in subjects)
        {
            if (!state.Platforms.TryGetValue(RegistryActivePolicyClient.PlatformKey(subject), out var entry)) throw Denied(PackagePolicyErrorCodes.PolicyRequired);
            if (entry.Decision.Disposition != "Allowed") throw Denied(entry.Decision.ErrorCode ?? PackagePolicyErrorCodes.PolicyRequired);
            RequireLease(state, entry.Lease, now);
        }
    }

    internal RuntimePolicyLeaseStatus PlatformLease(string origin, IReadOnlyList<RegistryPlatformCatalogIdentity> subjects)
    {
        RequirePlatformActivation(origin, subjects);
        return subjects.Select(subject => _snapshots[RegistryActivePolicyClient.Origin(origin)].Platforms[RegistryActivePolicyClient.PlatformKey(subject)].Lease)
            .OrderBy(lease => lease.Clock!.DeadlineTicks).First();
    }

    public async Task InvalidateClockAsync(string origin, CancellationToken token = default)
    {
        origin = RegistryActivePolicyClient.Origin(origin);
        MarkClockInvalid(origin, force: true);
        await PersistInvalidationAsync(origin, token);
    }

    private async Task PersistInvalidationAsync(string origin, CancellationToken token)
    {
        origin = RegistryActivePolicyClient.Origin(origin); var path = FilePath(origin);
        using var lease = await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token);
        lock (_clockGate) if (!_clockStates.GetValueOrDefault(origin).Invalid) return;
        var state = await ReadFileAsync(origin, path, token);
        if (state is null) return;
        state = InvalidateLeases(state) with { Generation = checked(state.Generation + 1), AuthorityGeneration = checked(state.AuthorityGeneration + 1), ClockGeneration = checked(state.ClockGeneration + 1), ClockInvalidated = true };
        await PersistAsync(path, state, token); _snapshots[origin] = state;
        PolicyChanged?.Invoke();
    }

    private async Task<PackagePolicyOriginFile?> LoadAsync(string origin, bool checkpointClock, CancellationToken token)
    {
        origin = RegistryActivePolicyClient.Origin(origin); var path = FilePath(origin);
        using var lease = await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token);
        var state = await ReadFileAsync(origin, path, token);
        if (state is null) { _snapshots.TryRemove(origin, out _); return null; }
        if (checkpointClock)
        {
            try
            {
                if (state.ClockInvalidated) { MarkClockInvalid(origin); throw Denied(PackagePolicyErrorCodes.PolicyClockInvalid); }
                var now = ReadClock(origin, state.HighWater);
                if (now.Ticks > state.HighWater.Ticks)
                {
                    state = state with { Generation = checked(state.Generation + 1), HighWater = now };
                    await PersistAsync(path, state, token);
                }
            }
            catch (PackagePolicyDeniedException)
            {
                state = InvalidateLeases(state) with { Generation = checked(state.Generation + 1),
                    AuthorityGeneration = checked(state.AuthorityGeneration + (state.ClockInvalidated ? 0 : 1)),
                    ClockGeneration = checked(state.ClockGeneration + (state.ClockInvalidated ? 0 : 1)), ClockInvalidated = true };
                await PersistAsync(path, state, token);
                _snapshots[origin] = state;
                throw;
            }
        }
        _snapshots[origin] = state;
        return state;
    }

    private PolicyClockReading ReadClock(string origin, PolicyClockReading persisted)
    {
        lock (_clockGate)
        {
            PolicyClockReading now;
            if (_clockStates.GetValueOrDefault(origin).Invalid) throw Denied(PackagePolicyErrorCodes.PolicyClockInvalid);
            try { now = clock.Read(); RegistryActivePolicyClient.ValidateClock(now); }
            catch (Exception error) when (error is not OperationCanceledException) { MarkClockInvalid(origin); throw Denied(PackagePolicyErrorCodes.PolicyClockInvalid); }
            if (!now.SameEpoch(persisted) || now.Ticks < persisted.Ticks
                || _observedClocks.TryGetValue(origin, out var last) && (!now.SameEpoch(last) || now.Ticks < last.Ticks))
            {
                MarkClockInvalid(origin);
                throw Denied(PackagePolicyErrorCodes.PolicyClockInvalid);
            }
            _observedClocks[origin] = now; return now;
        }
    }

    private void MarkClockInvalid(string origin, bool force = false)
    {
        lock (_clockGate)
        {
            var state = _clockStates.GetValueOrDefault(origin);
            if (force || !state.Invalid) _clockStates[origin] = (checked(state.Generation + 1), true);
        }
    }

    private static PackagePolicyOriginFile InvalidateLeases(PackagePolicyOriginFile state)
    {
        static RuntimePolicyLeaseStatus Invalid(RuntimePolicyLeaseStatus lease) => lease with
        { State = RuntimePolicyLeaseStates.ClockInvalid, ErrorCode = PackagePolicyErrorCodes.PolicyClockInvalid };
        return state with
            {
                Packages = PackagePolicyOriginFile.Freeze(state.Packages.ToDictionary(pair => pair.Key, pair => pair.Value with { Lease = Invalid(pair.Value.Lease) })),
                Platforms = PackagePolicyOriginFile.Freeze(state.Platforms.ToDictionary(pair => pair.Key, pair => pair.Value with { Lease = Invalid(pair.Value.Lease) })),
            };
    }

    private static void RequireLease(PackagePolicyOriginFile state, RuntimePolicyLeaseStatus lease, PolicyClockReading now)
    {
        var value = lease.Clock;
        if (state.ClockInvalidated || lease.State == RuntimePolicyLeaseStates.ClockInvalid || value is null || value.BootSessionId != now.BootSessionId || value.ClockSource != now.Source || value.TicksPerSecond != now.Frequency
            || value.VirtualMachineGeneration != now.VirtualMachineGeneration || now.Ticks < value.RequestStartedTicks || now.Ticks < value.HighWaterTicks) throw Denied(PackagePolicyErrorCodes.PolicyClockInvalid);
        if (now.Ticks >= value.DeadlineTicks) throw Denied(PackagePolicyErrorCodes.PolicyExpired);
    }

    internal string FilePath(string origin)
    {
        origin = RegistryActivePolicyClient.Origin(origin);
        var root = Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory);
        PackageStorePathGuard.EnsureSafe(root, paths.RootPath);
        Directory.CreateDirectory(root);
        PackageStorePathGuard.EnsureSafe(root, paths.RootPath);
        return Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin))) + ".json");
    }

    private async Task<PackagePolicyOriginFile?> ReadFileAsync(string origin, string path, CancellationToken token)
    {
        try { return await ReadCoreAsync(origin, path, token); }
        catch (Exception error) when (error is not OperationCanceledException) { _persistenceFailures[origin] = 0; throw; }
    }

    private async Task<PackagePolicyOriginFile?> ReadCoreAsync(string origin, string path, CancellationToken token)
    {
        var current = await ReadDocumentAsync(origin, path, token);
        var intent = await ReadDocumentAsync(origin, IntentPath(path), token);
        PackagePolicyOriginFile? state;
        if (current is null) state = intent;
        else if (intent is null) state = current;
        else
        {
            RegistryActivePolicyClient.Require(current.Generation != intent.Generation || Same(current, intent), "Same-generation policy recovery copies conflict.");
            state = current.Generation >= intent.Generation ? current : intent;
            RequireWatermarksAtLeast(state, ReferenceEquals(state, current) ? intent : current);
            // Both documents are full snapshots. A newer recovery copy must retain
            // every older decision and every terminal restriction, not just its floor.
            var merged = MergeRetained(current, intent);
            RegistryActivePolicyClient.Require(Covers(merged, current) && Covers(merged, intent), "Policy recovery copies lack complete history.");
            state = merged;
        }
        if (state is null)
        {
            RegistryActivePolicyClient.Require(!_snapshots.ContainsKey(origin), "The retained policy cache disappeared.");
            return null;
        }
        if (_snapshots.TryGetValue(origin, out var known))
        {
            RegistryActivePolicyClient.Require(state.Generation >= known.Generation
                && (state.Generation != known.Generation || Same(state, known)), "Durable policy cache rolled back or conflicted.");
            RequireWatermarksAtLeast(state, known);
        }
        if (current is null || intent is null || !Same(current, state) || !Same(intent, state))
            await PersistAsync(path, state, token);
        return state;
    }

    private static async Task<PackagePolicyOriginFile?> ReadDocumentAsync(string origin, string path, CancellationToken token)
    {
        PackageStorePathGuard.EnsureSafe(path, Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path); RegistryActivePolicyClient.Require(info.Length is > 0 and <= MaximumFileBytes, "Policy cache exceeds its byte bound.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var bytes = new byte[checked((int)info.Length)]; await stream.ReadExactlyAsync(bytes, token);
        RegistryActivePolicyClient.Require(stream.ReadByte() == -1, "Policy cache changed while reading.");
        var state = JsonSerializer.Deserialize<PackagePolicyOriginFile>(bytes, RegistryActivePolicyClient.Json) ?? throw new InvalidDataException("Policy cache is empty.");
        ValidateFile(state, origin);
        return state;
    }

    private async Task PersistAsync(string path, PackagePolicyOriginFile state, CancellationToken token)
    {
        ValidateFile(state, state.Origin);
        RegistryActivePolicyClient.Require(JsonSerializer.SerializeToUtf8Bytes(state, RegistryActivePolicyClient.Json).Length < MaximumFileBytes, "Policy cache exceeds its byte bound.");
        try
        {
            // The recovery copy is durable before visibility of the primary snapshot.
            // Retain it until superseded by a complete successor, including when the
            // installed-catalog metadata transaction has not run yet.
            await DurableJsonDocument.WriteAsync(IntentPath(path), state, RegistryActivePolicyClient.Json, token);
            faultInjector?.Hit(PackagePolicyPersistencePoint.IntentDurable);
            await DurableJsonDocument.WriteAsync(path, state, RegistryActivePolicyClient.Json, token);
            faultInjector?.Hit(PackagePolicyPersistencePoint.SnapshotDurable);
        }
        catch { _persistenceFailures[state.Origin] = 0; throw; }
    }

    internal static string IntentPath(string path) => Path.ChangeExtension(path, ".intent.json");
    private static Dictionary<string, T> Copy<T>(IReadOnlyDictionary<string, T>? source) => source is null ? new(StringComparer.Ordinal) : new(source, StringComparer.Ordinal);
    private static Dictionary<string, T> Sorted<T>(Dictionary<string, T> source) => source.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static bool Same<T>(T first, T second) => JsonSerializer.SerializeToUtf8Bytes(first, RegistryActivePolicyClient.Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second, RegistryActivePolicyClient.Json));
    private static PackagePolicyDeniedException Denied(string code) => new(code);
}
