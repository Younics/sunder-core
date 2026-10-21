using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.RegistryActivePolicyClient;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageLifecyclePolicyCache
{
    internal static void ValidateRetainedSnapshot(PackagePolicyOriginFile state) => ValidateFile(state, Origin(state.Origin));

    internal async Task<IReadOnlyList<InstalledPackageRecord>> CaptureInstalledAsync(
        IReadOnlyList<InstalledPackageRecord> packages, CancellationToken token)
    {
        await RestoreRetainedAsync(packages.SelectMany(package => package.Policy?.Snapshots ?? []), token);
        await LoadKnownOriginsAsync(token, checkpointClock: false);
        var origins = packages.SelectMany(RequiredOrigins).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var locks = new List<IDisposable>();
        try
        {
            foreach (var origin in origins)
            {
                var path = FilePath(origin);
                locks.Add(await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token));
                var state = await ReadFileAsync(origin, path, token);
                if (state is not null)
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
                        if (!state.ClockInvalidated)
                        {
                            state = InvalidateLeases(state) with { Generation = checked(state.Generation + 1), AuthorityGeneration = checked(state.AuthorityGeneration + 1), ClockGeneration = checked(state.ClockGeneration + 1), ClockInvalidated = true };
                            await PersistAsync(path, state, token);
                        }
                    }
                    _snapshots[origin] = state;
                }
            }
            return packages.Select(package => CaptureInstalled(package)?.ApplyTo(package) ?? package with { Policy = null }).ToArray();
        }
        finally { foreach (var lease in locks.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    internal InstalledPackagePolicyRecord? CaptureInstalled(InstalledPackageRecord package)
    {
        if (IsDevelopment(package)) return null;
        var subject = InstalledPackagePolicyRecord.Subject(package);
        var origin = PackageOrigin(package);
        var snapshots = new List<PackagePolicyOriginFile>();
        foreach (var authority in RequiredOrigins(package).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!_snapshots.TryGetValue(authority, out var source)) continue;
            var selectedPackages = source.Packages.Where(pair => authority == origin
                && (pair.Key == PackageKey(subject) || pair.Value.Decision.Package.PackageId == subject.PackageId && pair.Value.Decision.PackageState == "SecurityBlocked"))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var platformKeys = selectedPackages.Values.SelectMany(value => value.Decision.PlatformDependencies).Select(PlatformKey).ToHashSet(StringComparer.Ordinal);
            var platforms = source.Platforms.Where(pair => platformKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (selectedPackages.Count + platforms.Count == 0) continue;
            snapshots.Add(source with
            {
                Packages = PackagePolicyOriginFile.Freeze(selectedPackages),
                Platforms = PackagePolicyOriginFile.Freeze(platforms),
            });
        }
        string? error = null;
        try
        {
            if (origin is null) throw Denied(PackagePolicyErrorCodes.PolicyRequired);
            RequireActivation(origin, subject);
        }
        catch (PackagePolicyDeniedException denied) { error = denied.Code; }
        var result = new InstalledPackagePolicyRecord(error is null ? "Allowed"
            : error is PackagePolicyErrorCodes.PolicyRequired ? "PolicyRequired" : "PolicyBlocked",
            error, origin, subject, snapshots);
        result.Validate(package);
        return result;
    }

    internal async Task<TResult> ExecuteUnderAuthorityFenceAsync<TResult>(
        IReadOnlyList<InstalledPackagePolicyRecord> expected, Func<CancellationToken, Task<TResult>> operation, CancellationToken token)
    {
        var locks = new List<IDisposable>();
        try
        {
            foreach (var origin in expected.SelectMany(value => value.Snapshots).Select(value => value.Origin).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var path = FilePath(origin);
                locks.Add(await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token));
                var state = await ReadFileAsync(origin, path, token) ?? throw Denied(PackagePolicyErrorCodes.PolicyRequired);
                _snapshots[origin] = state;
            }
            foreach (var policy in expected)
            {
                if (policy.State != "Allowed" || policy.RegistryOrigin is null) throw Denied(policy.ErrorCode ?? PackagePolicyErrorCodes.PolicyRequired);
                RequireActivation(policy.RegistryOrigin, policy.Package);
                foreach (var snapshot in policy.Snapshots)
                {
                    var current = _snapshots[snapshot.Origin];
                    if (!SameAuthority(current, snapshot)) throw Denied(PackagePolicyErrorCodes.PolicyStale);
                    var now = ReadClock(snapshot.Origin, current.HighWater);
                    // Renewals during a local transaction cannot extend its captured lease.
                    foreach (var entry in snapshot.Packages.Values) RequireLease(snapshot, entry.Lease, now);
                    foreach (var entry in snapshot.Platforms.Values) RequireLease(snapshot, entry.Lease, now);
                }
            }
            token.ThrowIfCancellationRequested();
            return await operation(token);
        }
        finally { foreach (var lease in locks.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    internal async Task RestoreRetainedAsync(IEnumerable<PackagePolicyOriginFile> retained, CancellationToken token)
    {
        foreach (var snapshot in retained.OrderBy(value => value.Origin, StringComparer.Ordinal).ThenBy(value => value.Generation))
        {
            ValidateRetainedSnapshot(snapshot);
            // Commit already holds these origin locks. A verified in-memory superset
            // needs no recursive lock acquisition or new persistence operation.
            if (!_persistenceFailures.ContainsKey(snapshot.Origin) && _snapshots.TryGetValue(snapshot.Origin, out var loaded) && Covers(loaded, snapshot)) continue;
            var path = FilePath(snapshot.Origin);
            using var lease = await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token);
            var current = await ReadFileAsync(snapshot.Origin, path, token);
            var next = current is null ? snapshot : MergeRetained(current, snapshot);
            if (current is null || !Same(current, next)) await PersistAsync(path, next, token);
            _snapshots[snapshot.Origin] = next;
            _persistenceFailures.TryRemove(snapshot.Origin, out _);
        }
    }

    private static PackagePolicyOriginFile MergeRetained(PackagePolicyOriginFile current, PackagePolicyOriginFile retained)
    {
        Require(current.AuthorityGeneration != retained.AuthorityGeneration || SameMetadata(current, retained), "Same-generation retained policy metadata conflicts.");
        var latest = current.AuthorityGeneration > retained.AuthorityGeneration
            || current.AuthorityGeneration == retained.AuthorityGeneration && current.HighWater.Ticks >= retained.HighWater.Ticks ? current : retained;
        var older = ReferenceEquals(latest, current) ? retained : current;
        RequireWatermarksAtLeast(latest, older);
        var original = current;
        if (current.ClockGeneration != latest.ClockGeneration || latest.ClockInvalidated) current = InvalidateLeases(current);
        if (retained.ClockGeneration != latest.ClockGeneration || latest.ClockInvalidated) retained = InvalidateLeases(retained);
        var packages = Copy(current.Packages); var platforms = Copy(current.Platforms);
        foreach (var (key, incoming) in retained.Packages)
        {
            if (packages.TryGetValue(key, out var old))
            {
                Require(old.Revision != incoming.Revision || Same(old.Decision, incoming.Decision), "Retained package policy conflicts at the same revision.");
                var winner = incoming.Revision > old.Revision || incoming.Revision == old.Revision && retained.AuthorityGeneration > current.AuthorityGeneration ? incoming : old;
                Require(old.Decision.PackageState != "SecurityBlocked" && incoming.Decision.PackageState != "SecurityBlocked" || winner.Decision.PackageState == "SecurityBlocked", "Retained package identity block cannot be erased.");
                Require(old.Decision.VersionAvailability != "SecurityBlocked" && incoming.Decision.VersionAvailability != "SecurityBlocked" || winner.Decision.VersionAvailability == "SecurityBlocked", "Retained version block cannot be erased.");
                packages[key] = winner;
            }
            else packages[key] = incoming;
        }
        foreach (var (key, incoming) in retained.Platforms)
        {
            if (platforms.TryGetValue(key, out var old))
            {
                Require(old.Revision != incoming.Revision || Same(old.Decision, incoming.Decision), "Retained platform policy conflicts at the same revision.");
                var winner = incoming.Revision > old.Revision || incoming.Revision == old.Revision && retained.AuthorityGeneration > current.AuthorityGeneration ? incoming : old;
                Require(old.Decision.LifecycleState != "Revoked" && incoming.Decision.LifecycleState != "Revoked" || winner.Decision.LifecycleState == "Revoked", "Retained platform revocation cannot be erased.");
                platforms[key] = winner;
            }
            else platforms[key] = incoming;
        }
        var merged = latest with
        {
            Packages = PackagePolicyOriginFile.Freeze(packages), Platforms = PackagePolicyOriginFile.Freeze(platforms),
        };
        if (merged.ClockInvalidated || !current.HighWater.SameEpoch(retained.HighWater))
        {
            // Preserve the latest epoch's leases; copied evidence from a different
            // boot never becomes permission on the new boot merely through replay.
            RuntimePolicyLeaseStatus Normalize(RuntimePolicyLeaseStatus value) => value.Clock is { } evidence && !merged.ClockInvalidated
                && merged.HighWater.SameEpoch(new(evidence.BootSessionId, evidence.ClockSource, evidence.TicksPerSecond, evidence.HighWaterTicks, evidence.VirtualMachineGeneration))
                    ? value : value with { State = RuntimePolicyLeaseStates.ClockInvalid, ErrorCode = PackagePolicyErrorCodes.PolicyClockInvalid };
            merged = merged with
            {
                Packages = PackagePolicyOriginFile.Freeze(packages.ToDictionary(pair => pair.Key, pair => pair.Value with { Lease = Normalize(pair.Value.Lease) })),
                Platforms = PackagePolicyOriginFile.Freeze(platforms.ToDictionary(pair => pair.Key, pair => pair.Value with { Lease = Normalize(pair.Value.Lease) })),
            };
        }
        ValidateRetainedSnapshot(merged);
        if (Same(original, merged)) return original;
        if (Same(latest, merged) && latest.Generation >= original.Generation) return latest;
        return merged with { Generation = checked(Math.Max(current.Generation, retained.Generation) + 1) };
    }

    private static bool Covers(PackagePolicyOriginFile current, PackagePolicyOriginFile retained)
    {
        if (current.AuthorityGeneration < retained.AuthorityGeneration) return false;
        if (current.AuthorityGeneration == retained.AuthorityGeneration && (!SameMetadata(current, retained) || current.HighWater.Ticks < retained.HighWater.Ticks)) return false;
        try { RequireWatermarksAtLeast(current, retained); }
        catch (InvalidDataException) { return false; }
        return retained.Packages.All(pair => current.Packages.TryGetValue(pair.Key, out var entry) && entry.Revision >= pair.Value.Revision
                && (entry.Revision > pair.Value.Revision || Same(entry.Decision, pair.Value.Decision))
                && (pair.Value.Decision.PackageState != "SecurityBlocked" || entry.Decision.PackageState == "SecurityBlocked")
                && (pair.Value.Decision.VersionAvailability != "SecurityBlocked" || entry.Decision.VersionAvailability == "SecurityBlocked")
                && (current.AuthorityGeneration != retained.AuthorityGeneration || Same(entry, pair.Value)))
            && retained.Platforms.All(pair => current.Platforms.TryGetValue(pair.Key, out var entry) && entry.Revision >= pair.Value.Revision
                && (entry.Revision > pair.Value.Revision || Same(entry.Decision, pair.Value.Decision))
                && (pair.Value.Decision.LifecycleState != "Revoked" || entry.Decision.LifecycleState == "Revoked")
                && (current.AuthorityGeneration != retained.AuthorityGeneration || Same(entry, pair.Value)));
    }

    private static bool SameMetadata(PackagePolicyOriginFile first, PackagePolicyOriginFile second)
        => first.Origin == second.Origin && first.AuthorityGeneration == second.AuthorityGeneration && first.ClockGeneration == second.ClockGeneration && first.PackagePolicy == second.PackagePolicy
            && first.HighWater.SameEpoch(second.HighWater) && first.LastRequestStarted == second.LastRequestStarted
            && first.LastNonce == second.LastNonce && first.ClockInvalidated == second.ClockInvalidated;

    private static bool SameAuthority(PackagePolicyOriginFile current, PackagePolicyOriginFile expected)
        => current.ClockGeneration == expected.ClockGeneration && current.PackagePolicy == expected.PackagePolicy
            && expected.Packages.All(pair => current.Packages.TryGetValue(pair.Key, out var entry) && Same(entry.Decision, pair.Value.Decision))
            && expected.Platforms.All(pair => current.Platforms.TryGetValue(pair.Key, out var entry) && Same(entry.Decision, pair.Value.Decision));

    private static void RequireWatermarksAtLeast(PackagePolicyOriginFile latest, PackagePolicyOriginFile older)
        => Require(latest.Origin == older.Origin && latest.AuthorityGeneration >= older.AuthorityGeneration && latest.ClockGeneration >= older.ClockGeneration
            && latest.PackagePolicy.Revision >= older.PackagePolicy.Revision
            && (latest.PackagePolicy.Revision != older.PackagePolicy.Revision || latest.PackagePolicy == older.PackagePolicy),
            "Retained policy watermarks are older than or conflict with newer cached observations.");

    private IEnumerable<string> RequiredOrigins(InstalledPackageRecord package)
    {
        if (IsDevelopment(package)) yield break;
        if (PackageOrigin(package) is { } origin) yield return origin;
    }

    private string? PackageOrigin(InstalledPackageRecord package) => package.Provenance.RegistryOrigin is { } origin
        ? Origin(origin) : package.Policy?.RegistryOrigin ?? FindPackageOrigin(InstalledPackagePolicyRecord.Subject(package));
    private static bool IsDevelopment(InstalledPackageRecord package) => package.Provenance.SourceKind == InstalledPackageSourceKind.Development;
}
