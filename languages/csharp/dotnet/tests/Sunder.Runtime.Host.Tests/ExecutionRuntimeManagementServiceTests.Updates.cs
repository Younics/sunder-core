using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    [Fact]
    public async Task PreparedUpdate_DefersLeasedRuntimeThenCommitsWhenStoppedWithoutNetworkAndTransfersManualRetention()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var (original, package, update) = await PrepareOfficialUpdateAsync(fixture);
        var root = fixture.Store.Layout.GetContentRootPath(original.ContentIdentity);
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(new(package.PackageId, "held-generation", Guid.NewGuid()),
            original.ContentIdentity, root, Path.Combine(root, original.LauncherPath), default);
        await fixture.Updates.ApplyPreparedWhenStoppedAsync(default);
        Assert.Equal(RuntimeExecutionRuntimeUpdateState.Deferred, Assert.Single((await fixture.Updates.StatusAsync(default)).PreparedUpdates).State);
        Assert.Equal(original.Identity.ToContract(), Assert.Single(Assert.Single(await fixture.InstalledPackages.ListAsync()).ExecutionRuntimeBindings).Runtime);
        await lease.DisposeAsync();
        fixture.Handler.Failure = new HttpRequestException("No vendor access while applying a prepared stopped update.");
        await fixture.Updates.ApplyPreparedWhenStoppedAsync(default);
        var rebound = Assert.Single(await fixture.InstalledPackages.ListAsync());
        Assert.Equal(package.Version, rebound.Version);
        Assert.Equal(package.ContentIdentity, rebound.ContentIdentity);
        Assert.Equal(package.Provenance, rebound.Provenance);
        Assert.Equal(update.Runtimes[0].Resolution.Artifact.Identity, Assert.Single(rebound.ExecutionRuntimeBindings).Runtime);
        Assert.Equal(update.Runtimes[0].Resolution.Artifact.Identity, Assert.Single((await fixture.ManagementState.ReadAsync(default)).ManualInstalls).Runtime);
        Assert.Equal(RuntimeExecutionRuntimeUpdateState.Applied, Assert.Single((await fixture.Updates.StatusAsync(default)).PreparedUpdates).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedUpdate_PrecommitFailureDiscardsOwnedStageAndKeepsOriginalBindings(bool cancel)
    {
        var faults = new PreparedCutoverFault();
        await using var fixture = await ManagementFixture.CreateAsync(packageFault: faults);
        var (original, _, update) = await PrepareOfficialUpdateAsync(fixture);
        var preview = await fixture.Updates.PreviewAsync(new(PreparedUpdateId: update.UpdateId), default);
        using var cancellation = new CancellationTokenSource();
        faults.BeforeCommit = () => { if (cancel) cancellation.Cancel(); else throw new IOException("Simulated precommit failure."); };
        var apply = () => fixture.Updates.ApplyAsync(new(new(preview.Plan!.PlanId, preview.Plan.Fingerprint), preview.Usage.Fingerprint), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(apply);
        else Assert.False((await apply()).Success);
        Assert.False(fixture.Provider.GetRequiredService<PackageStoreCoordinator>().HasPreparedStages);
        Assert.Equal(original.Identity.ToContract(), Assert.Single(Assert.Single(await fixture.InstalledPackages.ListAsync()).ExecutionRuntimeBindings).Runtime);
        Assert.Equal(original.Identity.ToContract(), Assert.Single((await fixture.ManagementState.ReadAsync(default)).ManualInstalls).Runtime);
    }

    [Fact]
    public async Task ManualRestart_RejectsImpactDriftBeforeStoppingOrChangingBindings()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var (original, package, update) = await PrepareOfficialUpdateAsync(fixture);
        var preview = await fixture.Updates.PreviewAsync(new(PreparedUpdateId: update.UpdateId), default);
        var root = fixture.Store.Layout.GetContentRootPath(original.ContentIdentity);
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(new(package.PackageId, "new-work", Guid.NewGuid()),
            original.ContentIdentity, root, Path.Combine(root, original.LauncherPath), default);
        var result = await fixture.Updates.ApplyAsync(new(new(preview.Plan!.PlanId, preview.Plan.Fingerprint), preview.Usage.Fingerprint,
            RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages), default);
        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.False(result.StoreCommitted);
        Assert.Equal(original.Identity.ToContract(), Assert.Single(Assert.Single(await fixture.InstalledPackages.ListAsync()).ExecutionRuntimeBindings).Runtime);
        Assert.NotEmpty(Directory.EnumerateFiles(fixture.Store.Layout.LeaseRootPath, "*.json"));
    }

    [Fact]
    public async Task PreparedUpdate_PostcommitFailurePreservesDesiredBindingAndTransfersManualRetention()
    {
        var faults = new PreparedCutoverFault { Point = PackageStoreFaultPoint.CatalogCommitted };
        await using var fixture = await ManagementFixture.CreateAsync(packageFault: faults);
        var (_, package, update) = await PrepareOfficialUpdateAsync(fixture);
        var preview = await fixture.Updates.PreviewAsync(new(PreparedUpdateId: update.UpdateId), default);
        var hits = 0;
        faults.BeforeCommit = () => { if (Interlocked.Increment(ref hits) == 1) throw new IOException("Failure after durable catalog commit."); };
        var result = await fixture.Updates.ApplyAsync(new(new(preview.Plan!.PlanId, preview.Plan.Fingerprint), preview.Usage.Fingerprint), default);
        Assert.True(result.StoreCommitted, result.Message);
        var desired = update.Runtimes[0].Resolution.Artifact.Identity;
        var retainedPackage = Assert.Single(await fixture.InstalledPackages.ListAsync());
        Assert.Equal(package.ContentIdentity, retainedPackage.ContentIdentity);
        Assert.Equal(desired, Assert.Single(retainedPackage.ExecutionRuntimeBindings).Runtime);
        Assert.Equal(desired, Assert.Single((await fixture.ManagementState.ReadAsync(default)).ManualInstalls).Runtime);
        Assert.True(result.State is RuntimeExecutionRuntimeUpdateState.Applied or RuntimeExecutionRuntimeUpdateState.ReconciliationPending);
    }

    [Fact]
    public async Task BinaryUpdatesDisabled_DoesNotDisableIndependentPackagePolicyRevocation()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var runtime = await fixture.EnsureAndBindPackageAsync("policy.independent");
        await fixture.RefreshPackagePolicyAsync();
        var settings = (await fixture.ManagementState.ReadAsync(default)).Settings;
        await fixture.ManagementState.UpdateSettingsAsync(new(settings.Revision, false), default);
        fixture.Provider.GetRequiredService<PackagePolicyHandler>().Deny = true;
        await fixture.RefreshPackagePolicyAsync();
        await fixture.Provider.GetRequiredService<InstalledPackagePolicyCoordinator>().ReconcileAsync(default);
        Assert.Equal("PolicyBlocked", Assert.Single(await fixture.InstalledPackages.ListAsync()).Policy!.State);
        Assert.True((await fixture.Store.VerifyAsync(runtime.Identity, false)).Healthy);
        Assert.False((await fixture.ManagementState.ReadAsync(default)).Settings.AutomaticUpdatesEnabled);
    }

    private static async Task<(ExecutionRuntimeReceiptV1 Original, InstalledPackageRecord Package, RuntimeExecutionRuntimePreparedUpdate Update)>
        PrepareOfficialUpdateAsync(ManagementFixture fixture)
    {
        var original = await fixture.EnsureAndBindPackageAsync("official.update");
        await fixture.RefreshPackagePolicyAsync();
        var package = Assert.Single(await fixture.InstalledPackages.ListAsync());
        fixture.Handler.Version = "10.0.2";
        fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
        var preview = await fixture.Updates.PreviewAsync(new(), default);
        var prepared = await fixture.Updates.PrepareAsync(new(preview.Plan!.PlanId, preview.Plan.Fingerprint), default);
        Assert.True(prepared.Success, prepared.Message);
        return (original, package, prepared.Update!);
    }

    private sealed class PreparedCutoverFault : IPackageStoreFaultInjector
    {
        internal PackageStoreFaultPoint Point { get; init; } = PackageStoreFaultPoint.CutoverPrepared;
        internal Action? BeforeCommit { get; set; }
        public void Hit(PackageStoreFaultPoint point) { if (point == Point) BeforeCommit?.Invoke(); }
    }

    [Fact]
    public async Task UpdateSettings_DefaultOnUsesPersistedCasAndCorruptStateFailsClosed()
    {
        await using var fixture = await ManagementFixture.CreateAsync();

        var initial = await fixture.ManagementState.ReadAsync(CancellationToken.None);
        Assert.True(initial.Settings.AutomaticUpdatesEnabled);
        Assert.Equal(1, initial.Settings.Revision);
        var events = fixture.Provider.GetRequiredService<RuntimeEventStreamService>();
        var sequence = events.GetSnapshot().SequenceId;
        await fixture.ManagementState.ReadAsync(default);
        await fixture.ManagementState.ChangeAsync(value => value with { ManualInstalls = value.ManualInstalls.ToArray() }, default);
        Assert.Equal(sequence, events.GetSnapshot().SequenceId);

        var updated = await fixture.ManagementState.UpdateSettingsAsync(new(initial.Settings.Revision, false), CancellationToken.None);
        Assert.False(updated.AutomaticUpdatesEnabled);
        Assert.Equal(2, updated.Revision);
        var changed = Assert.Single(events.GetSnapshot(sequence).Events);
        Assert.Equal(RuntimeEventKind.ExecutionRuntimesChanged, changed.Kind);
        Assert.False((await fixture.ManagementState.ReadAsync(default)).Settings.AutomaticUpdatesEnabled);
        await Assert.ThrowsAsync<RuntimeConflictException>(() =>
            fixture.ManagementState.UpdateSettingsAsync(new(initial.Settings.Revision, true), CancellationToken.None));
        Assert.Equal(changed.SequenceId, events.GetSnapshot().SequenceId);

        await File.WriteAllTextAsync(fixture.ManagementState.FilePath, "{\"schemaVersion\":1,\"settings\":{\"revision\":0,\"automaticUpdatesEnabled\":true},\"manualInstalls\":[],\"updates\":[]}");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ManagementState.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PrerequisitePreview_DoesNotDownloadRuntimeForPackageMutationAndRejectsStaleSourceExpectation()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var archive = await fixture.CreatePackageArchiveAsync("needs.runtime");

        var plan = await fixture.Prerequisites.InspectLocalAsync(
            [new PackageStoreMutation(PackageStoreMutationKind.Install, "needs.runtime", archive, AllowDowngrade: false, Reinstall: false,
                InstalledPackageProvenanceRecord.LocalArchive(Sha256(await File.ReadAllBytesAsync(archive))))],
            CancellationToken.None);

        Assert.True(plan.Success);
        Assert.False(plan.Satisfied);
        var prerequisite = Assert.Single(plan.Requirements);
        Assert.Equal(ExecutionRuntimeErrorCodes.Missing, prerequisite.ErrorCode);
        Assert.NotNull(prerequisite.Installation);
        Assert.DoesNotContain("download", fixture.Handler.Operations);

        var stale = new RuntimePackageRuntimePrerequisiteExpectation(new string('f', 64), plan.Rid);
        Assert.Throws<RuntimeExecutionRuntimeException>(() => PackageRuntimePrerequisiteService.CheckExpectation(plan, stale));
    }

    [Fact]
    public async Task PrerequisitePreview_SatisfiedWhenManualRuntimeInstallExistsEvenIfPackageMutationIsCancelled()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAsync();
        var archive = await fixture.CreatePackageArchiveAsync("needs.runtime");

        var plan = await fixture.Prerequisites.InspectLocalAsync(
            [new PackageStoreMutation(PackageStoreMutationKind.Install, "needs.runtime", archive, AllowDowngrade: false, Reinstall: false,
                InstalledPackageProvenanceRecord.LocalArchive(Sha256(await File.ReadAllBytesAsync(archive))))],
            CancellationToken.None);

        Assert.True(plan.Satisfied);
        Assert.Equal(receipt.Identity.ToContract(), Assert.Single(plan.Requirements).InstalledRuntime);
        Assert.Single((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
    }

    [Fact]
    public async Task UpdatePreview_SameVersionDifferentDigestIsArtifactChangedWhileFeedTimestampChurnIsIgnored()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("update.package");
        fixture.Handler.SupportPhase = "maintenance";
        fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes(receipt.Identity.VendorVersion, 0x44);

        var error = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() =>
            fixture.Updates.PreviewAsync(new(Runtime: receipt.Identity.ToContract()), CancellationToken.None));
        Assert.Equal(ExecutionRuntimeErrorCodes.ArtifactChanged, error.Code);
    }

    [Fact]
    public async Task UpdatePrepare_ForwardVersionTransfersManualRootAndKeepsPackagePinsUnchangedUntilApply()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("update.package");
        var before = Assert.Single(await fixture.Management.GetPackageBindingsAsync("update.package", CancellationToken.None));
        fixture.Handler.Version = "10.0.2";
        fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);

        var preview = await fixture.Updates.PreviewAsync(new(Runtime: receipt.Identity.ToContract()), CancellationToken.None);
        Assert.True(preview.Success);
        Assert.Single(preview.Runtimes);
        Assert.Single(preview.BindingChanges);

        var prepared = await fixture.Updates.PrepareAsync(new(preview.Plan!.PlanId, preview.Plan.Fingerprint), CancellationToken.None);

        Assert.True(prepared.Success, fixture.Failure(prepared));
        Assert.Equal(RuntimeExecutionRuntimeUpdateState.Prepared, prepared.State);
        Assert.Equal("10.0.2", Assert.Single(prepared.Update!.Runtimes).Resolution.Artifact.Identity.VendorVersion);
        var after = Assert.Single(await fixture.Management.GetPackageBindingsAsync("update.package", CancellationToken.None));
        Assert.Equal(before.Runtime, after.Runtime);
        Assert.Equal(before.InstalledArtifact, after.InstalledArtifact);
        Assert.Single((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
    }

    [Fact]
    public async Task AutomaticUpdateOffKeepsPreparedPolicyAndOfflineInstalledRuntimeUsable()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("update.package");
        await fixture.ManagementState.UpdateSettingsAsync(new(1, false), CancellationToken.None);
        fixture.Handler.Version = "10.0.2";
        fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);

        var events = fixture.Provider.GetRequiredService<RuntimeEventStreamService>();
        var before = events.GetSnapshot().SequenceId;
        await fixture.Updates.CheckAutomaticallyAsync(CancellationToken.None);
        var status = await fixture.Updates.StatusAsync(CancellationToken.None);
        Assert.False(status.CheckInProgress);
        Assert.True(events.GetSnapshot(before).Events.Count(item => item.Kind == RuntimeEventKind.ExecutionRuntimesChanged) >= 2);
        Assert.Empty(status.PreparedUpdates);
        before = events.GetSnapshot().SequenceId;
        await fixture.Updates.CheckAutomaticallyAsync(CancellationToken.None);
        // The fixture's frozen clock makes the second durable write a no-op.
        // Both the start and completion must still invalidate readers.
        Assert.Equal(2, events.GetSnapshot(before).Events.Count(item => item.Kind == RuntimeEventKind.ExecutionRuntimesChanged));
        Assert.False((await fixture.Updates.StatusAsync(default)).CheckInProgress);
        fixture.Handler.Failure = new HttpRequestException("offline");

        var summary = await fixture.Management.GetAsync(receipt.Identity.ToContract(), CancellationToken.None);

        Assert.Equal(RuntimeExecutionRuntimeStatuses.Ready, summary.Status);
        Assert.Single(await fixture.Management.GetPackageBindingsAsync("update.package", CancellationToken.None));
    }

}
