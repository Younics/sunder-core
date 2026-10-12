using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    [Theory]
    [InlineData("update-one", false)]
    [InlineData("update-one", true)]
    [InlineData("update-all", false)]
    [InlineData("update-all", true)]
    [InlineData("adoption", false)]
    [InlineData("adoption", true)]
    public async Task RegistryCombinedApproval_ActualAppQueueReceivesCompleteEvidenceBeforeRuntimeOrPackageAcquisition(string operation, bool missingRuntime)
    {
        await using var fixture = await ManagementFixture.CreateAsync(timeProvider: TimeProvider.System);
        var originalRuntime = await fixture.EnsureAndBindPackageAsync("combined.package");
        const string origin = "https://registry.example/";
        var original = Assert.Single(await fixture.InstalledPackages.ListAsync());
        if (operation != "adoption")
        {
            original = original with { Provenance = new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.FollowTag,
                origin, original.PackageId, RequestedTag: "stable", SourceIdentity: original.SourcePackageContentIdentity ?? original.ContentIdentity) };
            await fixture.InstalledPackages.WriteAsync([original]);
        }
        await fixture.AddBoundPackageAsync("preserved.pin", originalRuntime);
        var pinned = (await fixture.InstalledPackages.ListAsync()).Single(package => package.PackageId == "preserved.pin");
        pinned = pinned with { Provenance = new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.ExplicitVersion,
            origin, pinned.PackageId, RequestedVersion: pinned.Version, SourceIdentity: pinned.SourcePackageContentIdentity ?? pinned.ContentIdentity) };
        await fixture.InstalledPackages.WriteAsync([original, pinned]);
        var registry = fixture.Provider.GetRequiredService<PackagePolicyHandler>();
        var published = await ConfigureReviewedPackageAsync(fixture, registry, original, origin, "1.0.1", new('e', 64));
        if (missingRuntime)
        {
            fixture.CorruptInstalledContent(originalRuntime);
            fixture.Handler.Version = "10.0.2";
            fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
        }
        fixture.Handler.Operations.Clear();
        var releaseDownload = fixture.Handler.BlockNextDownload();
        await using var app = await StartOfficialAdoptionHttpAsync(fixture);
        using var transport = new RuntimeClientTransport(() => new RuntimeConnectionInfo(new Uri(Assert.Single(app.Urls)), "management-test-token"));
        using var client = new RuntimeApiClient(transport);
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var shown = new TaskCompletionSource<RegistryPackageChangePreflight>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        using var service = new RegistryPackageInstallService((review, _) =>
        {
            prompts++;
            shown.TrySetResult(review);
            return approve.Task;
        }, queue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var execution = operation switch
        {
            "update-one" => service.UpdatePackageAsync(original.PackageId, client, cancellationToken: timeout.Token),
            "update-all" => service.UpdateAllAsync(client, cancellationToken: timeout.Token),
            _ => service.AdoptPackageSourceAsync(original.PackageId, new Uri(origin), client, cancellationToken: timeout.Token),
        };
        try
        {
            await Task.WhenAny(shown.Task, execution).WaitAsync(timeout.Token);
            if (!shown.Task.IsCompleted) Assert.Fail((await execution).Message);
            var review = await shown.Task.WaitAsync(timeout.Token);
            Assert.Empty(queue.ListProcesses());
            Assert.Equal(0, registry.Downloads);
            Assert.DoesNotContain("download", fixture.Handler.Operations);
            Assert.Equal(published.PlanItem.PackageContentIdentity, Assert.Single(review.Plan.Items).PackageContentIdentity);
            var consent = Assert.Single(review.Plan.RequiredCapabilityConsents);
            Assert.Contains(SunderSdkCapabilities.DockerEnvironmentV1, consent.Capabilities);
            Assert.Equal(new RuntimeRegistryPackagePlanExpectation(origin, false, new('e', 64)), Assert.Single(review.ExpectedPlans));
            Assert.Equal(!missingRuntime, review.Plan.RuntimePrerequisites!.Satisfied);
            Assert.Equal(!missingRuntime, Assert.Single(review.ExecutionRuntimes).AlreadyInstalled);
            var sourceFingerprint = review.Plan.RuntimePrerequisites.SourceFingerprint;
            approve.TrySetResult(true);
            if (missingRuntime)
            {
                await Task.WhenAny(fixture.Handler.DownloadEntered, execution).WaitAsync(timeout.Token);
                if (!fixture.Handler.DownloadEntered.IsCompleted) Assert.Fail((await execution).Message
                    + "; official requests: " + string.Join(", ", fixture.Handler.Operations));
                await fixture.Handler.DownloadEntered.WaitAsync(timeout.Token);
                var jobs = queue.ListProcesses();
                Assert.Equal(2, jobs.Count);
                Assert.Equal(BackgroundProcessState.Queued, Assert.Single(jobs, job => job.Metadata["sunder.installationPhase"] == "package").State);
                Assert.Equal(0, registry.Downloads);
                var waiting = (await fixture.InstalledPackages.ListAsync()).Single(package => package.PackageId == original.PackageId);
                Assert.Equal(original.Version, waiting.Version);
                Assert.Equal(original.ContentIdentity, waiting.ContentIdentity);
                Assert.Equal(original.Provenance, waiting.Provenance);
                releaseDownload.TrySetResult();
            }
            var result = await execution.WaitAsync(timeout.Token);
            Assert.True(result.Success, result.Message + string.Join("; ", result.Errors));
            Assert.Equal(1, prompts);
            Assert.Equal(missingRuntime ? 2 : 1, queue.ListProcesses().Count);
            Assert.All(queue.ListProcesses(), job => Assert.Equal(BackgroundProcessState.Completed, job.State));
            var packages = await fixture.InstalledPackages.ListAsync();
            var updated = packages.Single(package => package.PackageId == original.PackageId);
            Assert.Equal("1.0.1", updated.Version);
            Assert.Equal(published.PlanItem.PackageContentIdentity, updated.SourcePackageContentIdentity);
            Assert.Equal(origin, updated.Provenance.RegistryOrigin);
            Assert.Equal(operation == "adoption" ? "latest" : "stable", updated.Provenance.RequestedTag);
            Assert.Equal(InstalledPackageVersionPolicy.FollowTag, updated.Provenance.VersionPolicy);
            Assert.Contains(updated.CapabilityGrants, grant => grant.Capability == SunderSdkCapabilities.DockerEnvironmentV1
                && grant.PackageVersion == updated.Version && grant.ManifestSha256 == consent.ManifestSha256);
            Assert.Equal(missingRuntime ? "10.0.2" : originalRuntime.Identity.VendorVersion, Assert.Single(updated.ExecutionRuntimeBindings).Runtime.VendorVersion);
            var retainedPin = packages.Single(package => package.PackageId == pinned.PackageId);
            Assert.Equal(pinned.Version, retainedPin.Version);
            Assert.Equal(pinned.ContentIdentity, retainedPin.ContentIdentity);
            Assert.Equal(pinned.Provenance, retainedPin.Provenance);
            Assert.True(registry.Downloads > 0);
            Assert.All(registry.PlansRequested, request =>
            {
                var selected = Assert.Single(request.Packages);
                Assert.Equal(original.PackageId, selected.PackageId);
                Assert.Null(selected.Version);
                Assert.Equal(operation == "adoption" ? "latest" : "stable", selected.Tag);
                Assert.Equal(operation == "adoption", request.Reinstall);
            });
            Assert.True(PackageSensitiveCapabilityPolicy.IsSha256(sourceFingerprint));
        }
        finally
        {
            approve.TrySetResult(false);
            releaseDownload.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.PackageLifecycle.ShutdownAsync(fixture.Provider.GetRequiredService<PackageSessionLifecycleService>());
            await app.StopAsync();
        }
    }

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

    private static async Task<RegistryRuntimeArchitectureTests.RegistryArtifactFixture> ConfigureReviewedPackageAsync(
        ManagementFixture fixture, PackagePolicyHandler registry, InstalledPackageRecord original, string origin, string version, string fingerprint)
    {
        var published = await RegistryRuntimeArchitectureTests.CreateRegistryArtifactAsync(fixture.Paths.RootPath,
            original.PackageId, origin, original.PackageId, original.Version, version,
            runtimeIdentifiers: [PackageTargetSelection.GetCurrentRuntimeIdentifier()], roles: ["runtime"],
            runtimeRequiredHostCapabilities: ["core.v1", "execution-runtime.v1", "rpc.v1", "worker-protocol.v1",
                SunderSdkCapabilities.Baseline11V1, SunderSdkCapabilities.DockerEnvironmentV1]);
        var authority = ActivePolicyTestContext.Allowed(new("", [], [], null, null));
        var plan = new RegistryResolveInstallPlanResponse(true, [published.PlanItem], [], [], [], [], fingerprint,
            fixture.Provider.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(5))
        {
            PackagePolicy = authority.PackagePolicy, PolicyCheckpoint = authority.Checkpoint,
            ResolutionPolicy = new(authority.PackagePolicy, authority.Checkpoint, [], [], [], []),
        };
        registry.PlansByOrigin[new Uri(origin).GetLeftPart(UriPartial.Authority)] = plan;
        registry.Artifacts = published.ContentByUrl;
        return published;
    }
}
