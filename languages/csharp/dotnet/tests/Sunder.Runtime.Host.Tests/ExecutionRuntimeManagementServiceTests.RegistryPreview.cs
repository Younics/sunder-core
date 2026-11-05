using Microsoft.Extensions.DependencyInjection;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Xunit;

using static Sunder.Runtime.Host.TestSupport.ExecutionRuntimeFixture;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("rid")]
    public async Task UpdateAll_MultiOriginMissingPreviewRetainsAggregateEvidenceAndStaleRejectionPayload(string drift)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var runtime = await fixture.EnsureAndBindPackageAsync("first.package");
        await fixture.AddBoundPackageAsync("second.package", runtime);
        var packages = (await fixture.InstalledPackages.ListAsync()).Select(package => package with
        { Provenance = new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.FollowTag,
            package.PackageId == "first.package" ? "https://registry.example/" : "https://second.example/", package.PackageId,
            RequestedTag: package.PackageId == "first.package" ? "stable" : "beta", SourceIdentity: package.SourcePackageContentIdentity ?? package.ContentIdentity,
            IncludePrerelease: package.PackageId == "second.package") }).ToArray();
        await fixture.InstalledPackages.WriteAsync(packages);
        var registry = fixture.Provider.GetRequiredService<PackagePolicyHandler>();
        var first = await ConfigureReviewedPackageAsync(fixture, registry, packages[0], packages[0].Provenance.RegistryOrigin!, "1.0.1", new('e', 64));
        var second = await ConfigureReviewedPackageAsync(fixture, registry, packages[1], packages[1].Provenance.RegistryOrigin!, "1.0.1", new('f', 64));
        registry.Artifacts = first.ContentByUrl.Concat(second.ContentByUrl).ToDictionary(pair => pair.Key, pair => pair.Value);
        fixture.CorruptInstalledContent(runtime);
        var orchestrator = fixture.Provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
        var preview = await orchestrator.UpdateAsync(new(DryRun: true), default);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeRegistryErrorCode.RuntimePrerequisitesRequired, preview.ErrorCode);
        Assert.Equal(2, preview.PlanItems.Count);
        Assert.Equal(2, preview.PlanExpectations.Count);
        Assert.Equal(2, preview.RequiredCapabilityConsents.Count);
        Assert.Equal(2, preview.RuntimePrerequisites!.Requirements.Count);
        Assert.Contains(preview.PlanExpectations, plan => plan.RegistryOrigin == "https://second.example/" && plan.IncludePrerelease);
        var blocked = await orchestrator.UpdateAsync(new(ExpectedPlans: preview.PlanExpectations,
            CapabilityConsents: preview.RequiredCapabilityConsents)
        { RuntimePrerequisiteExpectation = new(preview.RuntimePrerequisites.SourceFingerprint, preview.RuntimePrerequisites.Rid) }, default);
        Assert.False(blocked.Success);
        Assert.Equal(RuntimeRegistryErrorCode.RuntimePrerequisitesRequired, blocked.ErrorCode);
        Assert.Equal(preview.PlanExpectations, blocked.PlanExpectations);
        Assert.Equal(2, blocked.RequiredCapabilityConsents.Count);
        Assert.Equal(preview.RuntimePrerequisites.SourceFingerprint, blocked.RuntimePrerequisites!.SourceFingerprint);
        var rejected = await orchestrator.UpdateAsync(new(ExpectedPlans: preview.PlanExpectations)
        { RuntimePrerequisiteExpectation = new(drift == "source" ? new('b', 64) : preview.RuntimePrerequisites.SourceFingerprint,
            drift == "rid" ? "wrong-rid" : preview.RuntimePrerequisites.Rid) }, default);
        Assert.False(rejected.Success);
        Assert.Equal(RuntimeRegistryErrorCode.StalePlan, rejected.ErrorCode);
        Assert.Equal(preview.PlanExpectations, rejected.PlanExpectations);
        Assert.Equal(preview.RuntimePrerequisites.SourceFingerprint, rejected.RuntimePrerequisites!.SourceFingerprint);
        Assert.Equal(2, rejected.RequiredCapabilityConsents.Count);
        Assert.Equal(0, registry.Downloads);
        Assert.True(PackageStorePolicy.CatalogBindingsEqual(packages, await fixture.InstalledPackages.ListAsync()));
    }

    [Fact]
    public async Task PinnedUpdate_NoEligibleSelectionHasExplicitEmptyReadyEvidenceAndMutatesNothing()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        await fixture.EnsureAndBindPackageAsync("pinned.package");
        var package = Assert.Single(await fixture.InstalledPackages.ListAsync());
        package = package with { Provenance = new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.ExplicitVersion,
            "https://registry.example/", package.PackageId, RequestedVersion: package.Version, SourceIdentity: package.ContentIdentity) };
        await fixture.InstalledPackages.WriteAsync([package]);
        var orchestrator = fixture.Provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
        var result = await orchestrator.UpdateAsync(new(PackageId: package.PackageId, DryRun: true), default);
        Assert.True(result.Success);
        Assert.Empty(result.PlanItems);
        Assert.Empty(result.PlanExpectations);
        Assert.Empty(result.RequiredCapabilityConsents);
        Assert.True(result.RuntimePrerequisites!.Success);
        Assert.True(result.RuntimePrerequisites.Satisfied);
        Assert.Empty(result.RuntimePrerequisites.Requirements);
        Assert.True(PackageSensitiveCapabilityPolicy.IsSha256(result.RuntimePrerequisites.SourceFingerprint));
        Assert.Equal([package.PackageId], result.SkippedPackageIds);
        Assert.Empty(fixture.Provider.GetRequiredService<PackagePolicyHandler>().PlansRequested);
        Assert.True(PackageStorePolicy.CatalogBindingsEqual([package], await fixture.InstalledPackages.ListAsync()));
    }

}
