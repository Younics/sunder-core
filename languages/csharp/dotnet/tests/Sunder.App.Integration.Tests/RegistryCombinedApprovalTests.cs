using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Xunit;
using static Sunder.Runtime.Host.Tests.ExecutionRuntimeManagementServiceTests;

namespace Sunder.App.Integration.Tests;

public sealed class RegistryCombinedApprovalTests
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
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
            Assert.Empty(queue.ListTasks());
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
                var jobs = queue.ListTasks();
                Assert.Equal(2, jobs.Count);
                Assert.Equal(BackgroundTaskState.Queued, Assert.Single(jobs, job => job.Metadata["sunder.installationPhase"] == "package").State);
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
            Assert.Equal(missingRuntime ? 2 : 1, queue.ListTasks().Count);
            Assert.All(queue.ListTasks(), job => Assert.Equal(BackgroundTaskState.Completed, job.State));
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
}
