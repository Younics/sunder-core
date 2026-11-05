using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;
using static Sunder.Runtime.Host.TestSupport.ExecutionRuntimeFixture;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    [Fact]
    [Trait("Capability", "Process")]
    public async Task ManagementState_FirstReadCreatesOwnedDirectoryAndFiles()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-management-ownership");
        var paths = new RuntimePackagePaths(scope.GetPath("runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var state = new ExecutionRuntimeManagementState(paths);

        await state.ReadAsync(CancellationToken.None);

        var directory = Path.GetDirectoryName(state.FilePath)!;
        RuntimeOwnedFileSystem.ValidateMutableDirectory(paths.RootPath, directory);
        RuntimeOwnedFileSystem.ValidateMutableRegularFile(paths.RootPath, state.FilePath);
        RuntimeOwnedFileSystem.ValidateMutableRegularFile(paths.RootPath, state.FilePath + ".lock");
        var identity = RuntimeOwnedFileSystem.GetMutableDirectoryIdentity(paths.RootPath, directory);
        await state.ReadAsync(CancellationToken.None);
        Assert.Equal(identity, RuntimeOwnedFileSystem.GetMutableDirectoryIdentity(paths.RootPath, directory));

        Assert.True(File.Exists(state.FilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureAsync_FinalMetadataConfirmationPinsExactRuntimeAndHoldsOperationGate(bool retain)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var confirming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.BeforeThirdMetadataAsync = async token =>
        {
            confirming.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ensure = fixture.Management.EnsureAsync(fixture.EnsureRequest with { RetainUntilUninstall = retain }, timeout.Token);
        try
        {
            await confirming.Task.WaitAsync(timeout.Token);
            Assert.Empty((await fixture.ManagementState.ReadAsync(timeout.Token)).ManualInstalls);
            using var waiting = new CancellationTokenSource();
            var enter = fixture.Provider.GetRequiredService<RuntimeOperationGate>().EnterAsync(waiting.Token);
            var gateWasHeld = !enter.IsCompleted;
            await waiting.CancelAsync();
            try { await using var unexpected = await enter; }
            catch (OperationCanceledException) { }
            Assert.True(gateWasHeld, "GC must not capture a pre-handoff manual-retention snapshot.");

            // The fixture authority deliberately does not take the operation gate: these
            // sweeps separately exercise the outer exact store pin during final HTTP work.
            for (var pass = 0; pass < 2; pass++)
            {
                var collected = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero, timeout.Token);
                Assert.Equal(0, collected.RootsRemoved);
                Assert.Equal(0, collected.ArchivesRemoved);
            }
            var identity = ExecutionRuntimeIdentity.FromContract(fixture.Artifact.Identity);
            Assert.True((await fixture.Store.VerifyAsync(identity, false, timeout.Token)).Healthy);
            release.TrySetResult();
            var result = await ensure;
            Assert.True(result.Success, fixture.Failure(result));
            Assert.Equal(RuntimeExecutionRuntimeStatuses.Ready, result.Runtime!.Health.Status);
            Assert.Equal(retain, result.Runtime.RetainedUntilUninstall);
            Assert.Equal(retain ? 1 : 0, (await fixture.ManagementState.ReadAsync(timeout.Token)).ManualInstalls.Count);
            Assert.NotNull(await fixture.Store.GetReceiptAsync(identity, timeout.Token));
            var collector = new ExecutionRuntimeStore(fixture.Store.Layout,
                fixture.Provider.GetRequiredService<PackageExecutionRuntimeGarbageCollectionAuthority>(),
                fixture.Provider.GetRequiredService<TimeProvider>());
            await collector.CollectGarbageAsync(TimeSpan.Zero, timeout.Token);
            await collector.CollectGarbageAsync(TimeSpan.Zero, timeout.Token);
            Assert.Equal(retain, await collector.GetReceiptAsync(identity, timeout.Token) is not null);
        }
        finally
        {
            release.TrySetResult();
            await ensure.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task EnsureAsync_UsesOfficialMetadataReconfirmsBeforeAndAfterDownloadAndPersistsManualRetention()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var events = fixture.Provider.GetRequiredService<RuntimeEventStreamService>();
        var sequence = events.GetSnapshot().SequenceId;

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.False(result.AlreadyPresent);
        Assert.Equal(["metadata", "metadata", "download", "metadata"], fixture.Handler.Operations);
        Assert.Equal(fixture.Artifact.Identity, result.Runtime!.Identity);
        Assert.Equal(fixture.Artifact.Distribution.MetadataSha256, result.Runtime.InstalledArtifact.Provenance.Distribution.MetadataSha256);
        Assert.Equal(fixture.Artifact.Distribution.MaterializationProfile, result.Runtime.InstalledArtifact.Provenance.Distribution.MaterializationProfile);
        Assert.Equal(fixture.ArchiveSize, result.Runtime.InstalledArtifact.ArchiveSize);
        Assert.True(result.Runtime.RetainedUntilUninstall);

        var state = await fixture.ManagementState.ReadAsync(CancellationToken.None);
        var retained = Assert.Single(state.ManualInstalls);
        Assert.Equal(fixture.Artifact.Identity, retained.Runtime);
        Assert.Equal(fixture.EnsureRequest.Requirement, retained.Requirement);
        Assert.Contains(events.GetSnapshot(sequence).Events, item => item.Kind == RuntimeEventKind.ExecutionRuntimesChanged);
    }

    [Theory]
    [InlineData("metadata-hash")]
    [InlineData("observed-at")]
    public async Task EnsureAsync_AcceptsSameSubjectMetadataAndTimestampChurn(string churn)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        if (churn == "metadata-hash")
        {
            fixture.Handler.MetadataSuffix = "\n  ";
        }
        else
        {
            fixture.Handler.SupportPhase = "maintenance";
        }

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.Equal(fixture.Artifact.Identity, result.Runtime!.Identity);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("digest")]
    public async Task EnsureAsync_RejectsMaterialSelectionDriftBeforeDownload(string drift)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.BeforeSecondMetadata = () =>
        {
            if (drift == "version")
            {
                fixture.Handler.Version = "10.0.2";
                fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
            }
            else
            {
                fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.1", 0x44);
            }
        };

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.DoesNotContain("download", fixture.Handler.Operations);
        Assert.Empty((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
    }

    [Fact]
    public async Task EnsureAsync_RejectsMaterialSelectionDriftAfterDownloadAndLeavesPreparedBytesUnbound()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.BeforeThirdMetadata = () =>
        {
            fixture.Handler.Version = "10.0.2";
            fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
        };

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.Equal(["metadata", "metadata", "download", "metadata"], fixture.Handler.Operations);
        Assert.Empty((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
        var prepared = Assert.Single(await fixture.Management.ListAsync(CancellationToken.None));
        Assert.False(prepared.RetainedUntilUninstall);
        Assert.Equal(0, prepared.PackageReferenceCount);
        Assert.Empty(await fixture.InstalledPackages.ListAsync());
    }

    [Fact]
    public async Task EnsureAsync_OfflineFailureNamesRequirementAndRidWithoutImportRecoveryText()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.Failure = new HttpRequestException("official metadata offline");

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.DownloadFailed, result.ErrorCode);
        Assert.Contains(fixture.EnsureRequest.Requirement.RuntimeId, result.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.EnsureRequest.Requirement.VersionRange, result.Message, StringComparison.Ordinal);
        Assert.Contains(PackageTargetSelection.GetCurrentRuntimeIdentifier(), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime import", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureAsync_CancellationDuringDownloadDoesNotRetainManualInstall()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.BlockNextDownload();
        using var cancellation = new CancellationTokenSource();

        var task = fixture.Management.EnsureAsync(fixture.EnsureRequest, cancellation.Token);
        await fixture.Handler.DownloadEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Empty((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
        Assert.Empty(await fixture.Management.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_UsesSafeHealthMessageForCorruptInstalledRuntime()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAsync();
        fixture.CorruptInstalledContent(receipt);

        var result = await fixture.Management.VerifyAsync(receipt.Identity.ToContract(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.IntegrityFailed, result.ErrorCode);
        Assert.Equal("Execution runtime failed deep verification and requires repair.", result.Message);
        Assert.Equal(RuntimeExecutionRuntimeStatuses.Unhealthy, result.Runtime!.Health.Status);
        Assert.Equal(SafeMessage(ExecutionRuntimeErrorCodes.IntegrityFailed), result.Runtime.Health.Message);
    }

    [Fact]
    public async Task RepairAsync_UsesCachedOfficialArchiveOfflineAndPreservesPackageBindingProvenance()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("repair.package");
        fixture.CorruptInstalledContent(receipt);
        fixture.Handler.Failure = new HttpRequestException("metadata offline");
        var before = Assert.Single(await fixture.Management.GetPackageBindingsAsync("repair.package", CancellationToken.None));
        var impact = await fixture.Management.GetImpactAsync(receipt.Identity.ToContract(), CancellationToken.None);

        var result = await fixture.Management.RepairAsync(receipt.Identity.ToContract(), new(impact.Fingerprint), CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.Equal("Execution runtime repaired and dependent packages reloaded.", result.Message);
        var after = Assert.Single(await fixture.Management.GetPackageBindingsAsync("repair.package", CancellationToken.None));
        Assert.Equal(before.Runtime, after.Runtime);
        Assert.Equal(before.InstalledArtifact, after.InstalledArtifact);
    }

    [Fact]
    public async Task RepairAsync_RejectsStaleImpactBeforeMutation()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("repair.package");
        fixture.CorruptInstalledContent(receipt);
        var impact = await fixture.Management.GetImpactAsync(receipt.Identity.ToContract(), CancellationToken.None);
        await fixture.AddBoundPackageAsync("late.package", receipt);

        var result = await fixture.Management.RepairAsync(receipt.Identity.ToContract(), new(impact.Fingerprint), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
    }

    [Fact]
    public async Task CollectGarbageAsync_RunsRuntimeAndProjectionCleanupTogether()
    {
        await using var fixture = await ManagementFixture.CreateAsync();

        var result = await fixture.Management.CollectGarbageAsync(CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.Contains("Execution runtime GC removed", result.Message, StringComparison.Ordinal);
        Assert.Contains("Package projection GC", result.Message, StringComparison.Ordinal);
    }

    private static string SafeMessage(string code)
        => ExecutionRuntimeErrorCatalog.All.Single(value => value.Code == code).SafeMessage;
}
