using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed class ActivePackagePolicySynchronizerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void HealthyRefreshUsesContinuousFiveMinuteScheduleWithBoundedJitter(int jitter)
    {
        var clock = new MutablePolicyClock();
        var schedule = new ActivePolicyRefreshSchedule();
        Assert.True(schedule.IsDue(clock.Read()));
        schedule.Completed(clock.Read(), clock.Read(), true, jitter);
        for (var second = 1; second < 300 + jitter; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(schedule.IsDue(clock.Read()));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(schedule.IsDue(clock.Read()));
    }

    [Fact]
    public void RetryBackoffCapsAtFifteenMinutesAndResumeSchedulesImmediateRefresh()
    {
        var clock = new MutablePolicyClock();
        var schedule = new ActivePolicyRefreshSchedule();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var started = clock.Read();
            clock.Advance(TimeSpan.FromSeconds(15));
            schedule.Completed(started, clock.Read(), false, 0);
            var seconds = Math.Min(5 * (1 << attempt), 900);
            for (var second = 1; second < seconds; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                Assert.False(schedule.IsDue(clock.Read()));
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(schedule.IsDue(clock.Read()));
        }
        schedule.Completed(clock.Read(), clock.Read(), true, 30);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(schedule.IsDue(clock.Read()));
    }

    [Fact]
    public void BatchBoundNeverSplitsAPackageFromItsPlatformDependencies()
    {
        var inputs = Enumerable.Range(0, 129).Select(index => new PackagePolicySubjects(Origin,
            [new("example.package" + index, "1.0.0", Hash("package-" + index))], [Platform])).ToArray();
        var batches = RegistryPackagePolicySynchronizer.Batches(Origin, inputs).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(128, batches[0].Packages.Count);
        Assert.Single(batches[1].Packages);
        Assert.All(batches, batch => Assert.Equal(Platform, Assert.Single(batch.Platforms)));
    }

    [Fact]
    public async Task AdmissionUsesRegistryContentIdentityInsteadOfProjectionAndHasNoNetwork()
    {
        using var policy = new ActivePolicyTestContext();
        await policy.RefreshAsync();
        policy.Send = (_, _) => throw new InvalidOperationException("Activation cannot use HTTP.");
        var source = new RuntimePackageSource(ActivePolicyTestContext.Package.PackageId, PackageSourceKind.Installed, policy.Paths.RootPath,
            ContentIdentity: Hash("projection"), Manifest: new() { Id = ActivePolicyTestContext.Package.PackageId, Version = "1.0.0" })
        { RegistryPolicyOrigin = Origin, RegistryPolicyContentIdentity = ActivePolicyTestContext.Package.PackageContentIdentity };
        var admission = new PackagePolicyAdmission(policy.Cache);
        await admission.RequireAsync(source, default);
        Assert.Equal(PackagePolicyErrorCodes.PolicyRequired, Assert.Throws<PackagePolicyDeniedException>(() => admission.Require(source with
        { RegistryPolicyContentIdentity = source.ContentIdentity })).Code);
    }

    [Fact]
    public async Task OfflineLocalImportLoadsOnlyExistingExactCacheAndMissingAuthorityFailsClosed()
    {
        using var policy = new ActivePolicyTestContext();
        await policy.RefreshAsync();
        var restarted = new PackageLifecyclePolicyCache(policy.Paths, policy.Clock);
        var admission = new PackagePolicyAdmission(restarted);
        var source = new RuntimePackageSource(ActivePolicyTestContext.Package.PackageId, PackageSourceKind.Installed, policy.Paths.RootPath,
            ContentIdentity: ActivePolicyTestContext.Package.PackageContentIdentity, Manifest: new() { Id = ActivePolicyTestContext.Package.PackageId, Version = "1.0.0" });
        await admission.RequireAsync(source, default);
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => admission.RequireAsync(source with { ContentIdentity = Hash("unknown") }, default));
    }

    [Fact]
    public async Task PlanRefreshRejectsChangedPolicyRatherThanQuietlyUsingNewerResolutionAuthority()
    {
        using var policy = new ActivePolicyTestContext();
        var sample = await policy.FetchAsync();
        using var synchronizer = new RegistryPackagePolicySynchronizer(policy.Client, policy.Cache);
        var plan = new RegistryResolveInstallPlanResponse(true,
            [new(ActivePolicyTestContext.Package.PackageId, null, "1.0.0", ActivePolicyTestContext.Package.PackageContentIdentity, RegistryPackageActionKinds.PackageInstall, null, [], [], [])], [], [], [], [])
        {
            PackagePolicy = sample.Response.PackagePolicy, PolicyCheckpoint = sample.Response.Checkpoint,
            ResolutionPolicy = new(sample.Response.PackagePolicy, sample.Response.Checkpoint, [], [], [], []),
        };
        await synchronizer.RefreshPlanAsync(Origin, plan, default);
        policy.Response = request => Allowed(request, 2);
        Assert.Equal(PackagePolicyErrorCodes.PlanStale,
            (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => synchronizer.RefreshPlanAsync(Origin, plan, default))).Code);
    }
}
