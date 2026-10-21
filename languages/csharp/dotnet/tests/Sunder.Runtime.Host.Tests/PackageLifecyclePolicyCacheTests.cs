using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageLifecyclePolicyCacheTests
{
    private static RegistryPackageVersionIdentity Package => ActivePolicyTestContext.Package;
    [Fact]
    public async Task SameBootOfflineRestartUsesRequestStartDeadlineAndIgnoresWallTime()
    {
        using var context = new ActivePolicyTestContext();
        var started = context.Clock.Value.Ticks;
        context.Response = request => { context.Clock.Advance(TimeSpan.FromSeconds(10)); return Allowed(request); };
        await context.RefreshAsync(platforms: [Platform]);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(context.Cache.FilePath(Origin)))!.AsObject();
        Assert.Equal(1, persisted["schemaVersion"]!.GetValue<int>());
        Assert.False(persisted.ContainsKey("checkpoint"));
        var watermark = persisted["packages"]!.AsObject().Single().Value!["lease"]!["watermark"]!.AsObject();
        Assert.Equal(["packagePolicyFingerprint", "packagePolicyRevision", "registryOrigin"], watermark.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        context.Time.UtcNow = ObservedAt.AddYears(-10);
        context.Clock.Advance(TimeSpan.FromHours(23));
        var reloaded = new PackageLifecyclePolicyCache(context.Paths, context.Clock);
        await reloaded.RequireActivationAsync(Origin, Package, default);
        context.Clock.Value = context.Clock.Value with { Ticks = started + 86_400 * context.Clock.Value.Frequency };
        Assert.Equal(PackagePolicyErrorCodes.PolicyExpired,
            Assert.Throws<PackagePolicyDeniedException>(() => reloaded.RequireActivation(Origin, Package)).Code);
    }

    [Theory]
    [InlineData("regression")]
    [InlineData("boot")]
    [InlineData("source")]
    [InlineData("frequency")]
    [InlineData("vm")]
    [InlineData("unavailable")]
    public async Task DiscontinuityIsStickyUntilFreshOnlineObservation(string failure)
    {
        using var context = new ActivePolicyTestContext();
        await context.RefreshAsync();
        var original = context.Clock.Value;
        context.Clock.Value = failure switch
        {
            "regression" => original with { Ticks = original.Ticks - 1 },
            "boot" => original with { BootSessionId = "boot-2" },
            "source" => original with { Source = "another-source" },
            "frequency" => original with { Frequency = 1001 },
            "vm" => original with { VirtualMachineGeneration = "vm-2" },
            _ => original,
        };
        context.Clock.Unavailable = failure == "unavailable";
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => context.Require()).Code);
        context.Clock.Value = original with { Ticks = original.Ticks + 1 };
        context.Clock.Unavailable = false;
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => context.Require()).Code);
        await context.RefreshAsync();
        context.Require();
    }

    [Fact]
    public async Task InvalidationSurvivesRestartAndPartialRefreshDoesNotReviveUntouchedLeases()
    {
        using var context = new ActivePolicyTestContext();
        var other = Package with { PackageId = "example.other" };
        await context.RefreshAsync([Package, other]);
        await context.Cache.InvalidateClockAsync(Origin);
        var reloaded = new PackageLifecyclePolicyCache(context.Paths, context.Clock);
        var failure = await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => reloaded.RequireActivationAsync(Origin, Package, default));
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, failure.Code);
        await context.RefreshAsync();
        context.Require();
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => context.Require(other)).Code);
        var again = new PackageLifecyclePolicyCache(context.Paths, context.Clock);
        await again.RequireActivationAsync(Origin, Package, default);
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => again.RequireActivation(Origin, other)).Code);
    }

    [Fact]
    public async Task RefreshDetectsRegressionAgainstRpcHighWaterAndInvalidatesUntouchedLeases()
    {
        using var context = new ActivePolicyTestContext();
        var other = Package with { PackageId = "example.other" };
        await context.RefreshAsync([Package, other]);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        context.Require(); // Advances the in-memory RPC clock beyond the last durable checkpoint.
        var beforeReset = await context.FetchAsync();
        context.Clock.Advance(TimeSpan.FromSeconds(-30));
        await context.RefreshAsync();
        context.Require();
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => context.Require(other)).Code);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Cache.ObserveAsync(beforeReset));
    }

    [Fact]
    public async Task ResponseAlreadyInFlightAtInvalidationCannotReestablishContinuity()
    {
        using var context = new ActivePolicyTestContext();
        await context.RefreshAsync();
        var inFlight = await context.FetchAsync();
        await context.Cache.InvalidateClockAsync(Origin);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Cache.ObserveAsync(inFlight));
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => context.Require()).Code);
    }

    [Fact]
    public async Task OldAllowedResponseCannotOverwriteSecurityBlockAndBlockSurvivesRestart()
    {
        using var context = new ActivePolicyTestContext();
        var delayed = await context.FetchAsync();
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        context.Response = request => Blocked(request, 2);
        await context.RefreshAsync();
        var bytes = await File.ReadAllBytesAsync(context.Cache.FilePath(Origin));
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Cache.ObserveAsync(delayed));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(context.Cache.FilePath(Origin)));
        var reloaded = new PackageLifecyclePolicyCache(context.Paths, context.Clock);
        Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked,
            (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => reloaded.RequireActivationAsync(Origin, Package, default))).Code);
        context.Response = request => Allowed(request, 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.RefreshAsync());
        Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked, Assert.Throws<PackagePolicyDeniedException>(() => context.Require()).Code);
    }

    [Fact]
    public async Task SameRevisionConflictingDecisionAndReusedObservationDoNotRenew()
    {
        using var context = new ActivePolicyTestContext();
        var observation = await context.FetchAsync();
        await context.Cache.ObserveAsync(observation);
        var bytes = await File.ReadAllBytesAsync(context.Cache.FilePath(Origin));
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Cache.ObserveAsync(observation));
        context.Response = request => Blocked(request, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.RefreshAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(context.Cache.FilePath(Origin)));
        context.Require();
    }

    [Fact]
    public async Task YankedBindingContinuesAndRevokedPlatformStopsItWithoutPackageRefresh()
    {
        using var context = new ActivePolicyTestContext();
        context.Response = request => Allowed(request) with { Packages = [new(Package, "Active", "Yanked", "Allowed", null, null, [Platform])] };
        await context.RefreshAsync(platforms: [Platform]);
        context.Require();
        context.Response = request => Allowed(request, 2) with
        { PlatformCatalogs = [new(Platform, "Revoked", "Denied", PackagePolicyErrorCodes.ContractAuthorityRevoked, ObservedAt)] };
        await context.RefreshAsync([], [Platform]);
        Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityRevoked, Assert.Throws<PackagePolicyDeniedException>(() => context.Require()).Code);
        context.Response = request => Allowed(request, 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.RefreshAsync([], [Platform]));
    }

    [Fact]
    public async Task ConcurrentWritersKeepHighestCompleteSnapshot()
    {
        using var context = new ActivePolicyTestContext();
        var low = await context.FetchAsync();
        context.Response = request => Blocked(request, 2);
        var high = await context.FetchAsync();
        var second = new PackageLifecyclePolicyCache(context.Paths, context.Clock);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => context.Cache.ObserveAsync(low)), Record.ExceptionAsync(() => second.ObserveAsync(high)));
        Assert.Null(results[1]);
        var reloaded = new PackageLifecyclePolicyCache(context.Paths, context.Clock);
        Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked,
            (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => reloaded.RequireActivationAsync(Origin, Package, default))).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("watermark")]
    [InlineData("fingerprint")]
    [InlineData("removed-origin-authority")]
    [InlineData("removed-lease-authority")]
    [InlineData("nonce")]
    [InlineData("clock")]
    public async Task BrokenDurableEvidenceCannotUsePreviouslyLoadedPermission(string failure)
    {
        using var context = new ActivePolicyTestContext();
        await context.RefreshAsync();
        var file = context.Cache.FilePath(Origin);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        switch (failure)
        {
            case "missing": File.Delete(file); File.Delete(PackageLifecyclePolicyCache.IntentPath(file)); break;
            case "corrupt": await File.WriteAllTextAsync(file, "{"); break;
            default:
                var entry = json["packages"]!.AsObject().First().Value!;
                if (failure == "watermark") entry["lease"]!["watermark"]!["packagePolicyRevision"] = 0;
                if (failure == "fingerprint") entry["lease"]!["watermark"]!["packagePolicyFingerprint"] = Hash("conflicting-policy");
                if (failure == "removed-origin-authority") json["checkpoint"] = null;
                if (failure == "removed-lease-authority") entry["lease"]!["watermark"]!["checkpointSequence"] = null;
                if (failure == "nonce") entry["lease"]!["responseNonce"] = "bad";
                if (failure == "clock") entry["lease"]!["clock"]!["deadlineTicks"] = long.MaxValue;
                await File.WriteAllTextAsync(file, json.ToJsonString());
                break;
        }
        Assert.NotNull(await Record.ExceptionAsync(() => context.Cache.CheckpointClockAsync(Origin, default)));
        Assert.Equal(PackagePolicyErrorCodes.PolicyRequired, Assert.Throws<PackagePolicyDeniedException>(() => context.Require()).Code);
    }

    [Fact]
    public async Task RestoredLowerPrimaryRecoversTheHigherDurableHighWater()
    {
        using var context = new ActivePolicyTestContext();
        await context.RefreshAsync();
        var path = context.Cache.FilePath(Origin);
        var old = await File.ReadAllBytesAsync(path);
        context.Clock.Advance(TimeSpan.FromHours(1));
        await context.Cache.CheckpointClockAsync(Origin, default);
        var latest = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, old);
        await context.Cache.CheckpointClockAsync(Origin, default);
        Assert.Equal(latest, await File.ReadAllBytesAsync(path));
        context.Require();
    }

    private static RegistryPolicySynchronizationResponse Blocked(RegistryPolicySynchronizationRequest request, long revision)
        => Allowed(request, revision) with { Packages = [new(Package, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "Security block.", [])] };
}
