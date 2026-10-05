using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.RegistryActivePolicyClient;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageLifecyclePolicyCache
{
    private static void ValidateFile(PackagePolicyOriginFile state, string origin)
    {
        Require(state.SchemaVersion == 1 && state.Generation > 0 && state.AuthorityGeneration > 0 && state.AuthorityGeneration <= state.Generation
            && state.ClockGeneration >= 0 && state.ClockGeneration <= state.AuthorityGeneration
            && state.Origin == origin && state.PackagePolicy.Revision > 0 && Hash(state.PackagePolicy.Fingerprint)
            && state.RuntimeRevision > 0
            && state.Checkpoint.Sequence > 0 && Hash(state.Checkpoint.Digest) && state.Checkpoint.TopologyGeneration > 0 && Hash(state.Checkpoint.TopologyFingerprint)
            && state.Packages.Count + state.Platforms.Count + state.Runtimes.Count is > 0 and <= MaximumSubjects, "Policy cache metadata is invalid.");
        ValidateClock(state.HighWater);
        Require(state.LastRequestStarted >= 0 && state.LastRequestStarted <= state.HighWater.Ticks
            && Hex(state.LastNonce, RegistryPolicyLimits.NonceBytes * 2), "Invalid last policy observation.");
        foreach (var (key, entry) in state.Packages)
        {
            ValidateLease(state, entry.Lease);
            ValidatePackage(entry.Decision);
            Require(key == PackageKey(entry.Decision.Package) && entry.Revision > 0 && entry.Revision <= state.PackagePolicy.Revision
                && entry.Revision == entry.Lease.Watermark!.PackagePolicyRevision, "Invalid cached package identity/revision.");
            Require(entry.Decision.PlatformDependencies.All(subject => state.Platforms.ContainsKey(PlatformKey(subject))), "Cached package platform evidence is missing.");
        }
        foreach (var (key, entry) in state.Platforms)
        {
            ValidateLease(state, entry.Lease);
            ValidatePlatform(entry.Decision, entry.Lease.ObservedAtUtc!.Value);
            Require(key == PlatformKey(entry.Decision.Catalog) && entry.Revision > 0 && entry.Revision <= state.PackagePolicy.Revision
                && entry.Revision == entry.Lease.Watermark!.PackagePolicyRevision, "Invalid cached platform identity/revision.");
        }
        foreach (var (key, entry) in state.Runtimes)
        {
            ValidateLease(state, entry.Lease);
            ValidateRuntime(entry.Decision, entry.Lease.ObservedAtUtc!.Value);
            Require(key == RuntimeKey(entry.Decision.Subject) && entry.Revision > 0 && entry.Revision <= state.RuntimeRevision
                && entry.Revision == entry.Lease.Watermark!.RuntimePolicyRevision, "Invalid cached runtime identity/revision.");
        }
        Require(state.Packages.Values.GroupBy(value => (value.Decision.Package.PackageId, value.Decision.Package.Version)).All(group => group.Count() == 1)
            && state.Platforms.Values.GroupBy(value => (value.Decision.Catalog.AuthorityId, value.Decision.Catalog.ReleaseVersion)).All(group => group.Count() == 1)
            && state.Runtimes.Values.GroupBy(value => value.Decision.Subject.Runtime).All(group => group.Count() == 1), "Conflicting immutable cached identities.");
    }

    private static void ValidateLease(PackagePolicyOriginFile state, RuntimePolicyLeaseStatus lease)
    {
        Require(lease.State == RuntimePolicyLeaseStates.Valid && lease.ErrorCode is null
            || lease.State == RuntimePolicyLeaseStates.ClockInvalid && lease.ErrorCode == PackagePolicyErrorCodes.PolicyClockInvalid, "Invalid cached lease state.");
        Require(lease.Watermark is not null && lease.Clock is not null && lease.ObservedAtUtc is not null && lease.ValidUntilUtc is not null, "Missing cached lease evidence.");
        var watermark = lease.Watermark!;
        var clock = lease.Clock!;
        Require(watermark.RegistryOrigin == state.Origin && watermark.PackagePolicyRevision > 0 && watermark.PackagePolicyRevision <= state.PackagePolicy.Revision
            && Hash(watermark.PackagePolicyFingerprint) && (watermark.PackagePolicyRevision != state.PackagePolicy.Revision || watermark.PackagePolicyFingerprint == state.PackagePolicy.Fingerprint)
            && watermark.RuntimePolicyRevision > 0 && watermark.RuntimePolicyRevision <= state.RuntimeRevision
            && watermark.CheckpointSequence > 0 && watermark.CheckpointSequence <= state.Checkpoint.Sequence && Hash(watermark.CheckpointDigest)
            && (watermark.CheckpointSequence != state.Checkpoint.Sequence || watermark.CheckpointDigest == state.Checkpoint.Digest)
            && watermark.TopologyGeneration > 0 && watermark.TopologyGeneration <= state.Checkpoint.TopologyGeneration && Hash(watermark.TopologyFingerprint)
            && (watermark.TopologyGeneration != state.Checkpoint.TopologyGeneration || watermark.TopologyFingerprint == state.Checkpoint.TopologyFingerprint), "Invalid cached lease watermarks.");
        ValidateClock(new(clock.BootSessionId, clock.ClockSource, clock.TicksPerSecond, clock.RequestStartedTicks, clock.VirtualMachineGeneration));
        Require((Int128)clock.DeadlineTicks - clock.RequestStartedTicks == (Int128)clock.TicksPerSecond * RuntimePolicyTiming.LeaseSeconds
            && clock.HighWaterTicks >= clock.RequestStartedTicks && clock.HighWaterTicks < clock.DeadlineTicks
            && lease.ObservedAtUtc!.Value.Offset == TimeSpan.Zero && lease.ValidUntilUtc!.Value.Offset == TimeSpan.Zero
            && lease.ValidUntilUtc - lease.ObservedAtUtc == TimeSpan.FromSeconds(RuntimePolicyTiming.LeaseSeconds)
            && Hash(lease.ResponseSha256) && Hex(lease.ResponseNonce, RegistryPolicyLimits.NonceBytes * 2), "Invalid cached lease evidence.");
        if (lease.State == RuntimePolicyLeaseStates.Valid)
            Require(!state.ClockInvalidated && state.HighWater.SameEpoch(new(clock.BootSessionId, clock.ClockSource, clock.TicksPerSecond, clock.HighWaterTicks, clock.VirtualMachineGeneration))
                && state.HighWater.Ticks >= clock.HighWaterTicks && state.LastRequestStarted >= clock.RequestStartedTicks, "Cached lease clock conflicts with its origin snapshot.");
    }
}
