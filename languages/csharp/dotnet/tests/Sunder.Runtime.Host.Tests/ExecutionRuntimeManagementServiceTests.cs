using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeManagementServiceTests
{
    private const string RegistryOrigin = "https://registry.example/";
    private const string ReleaseKeyId = "sunder.release.management-test.v1";
    private const string AttestationKeyId = "sunder.ci.management-test.v1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
    private static readonly byte[] ReleaseKey =
        Enumerable.Range(1, SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
            .Select(static value => (byte)value)
            .ToArray();

    [Fact]
    public async Task EnsureAsync_ReconfirmsOriginalFingerprintAfterDownloadBeforeAssociatingPolicy()
    {
        await using var fixture = await ManagementFixture.CreateAsync();

        var result = await fixture.Service.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.True(
            result.Success,
            $"{result.Message} Operations: {string.Join(", ", fixture.Handler.Operations)}. Failure: {fixture.Logger.LastException}");
        Assert.False(result.AlreadyPresent);
        Assert.Equal(
            ["resolve", "download", "resolve"],
            fixture.Handler.Operations);
        Assert.Collection(
            fixture.Handler.ResolveRequests,
            request => Assert.Null(request.ExpectedResolutionFingerprint),
            request => Assert.Equal(
                fixture.InitialResolution.ResolutionFingerprint,
                request.ExpectedResolutionFingerprint));
        Assert.All(fixture.Handler.ResolveRequests, request =>
        {
            Assert.Equal(PackageTargetSelection.GetCurrentRuntimeIdentifier(), request.Rid);
            Assert.Equal("management-test", request.RuntimeTrust.ProfileId);
            Assert.Equal(3, request.RuntimeTrust.Generation);
        });
        var policy = Assert.Single(fixture.LifecyclePolicies.CaptureSnapshot().Entries);
        Assert.Equal(fixture.Artifact.Identity.RuntimeId, policy.Identity.RuntimeId);
        Assert.Equal(fixture.InitialResolution.RuntimePolicyRevision, policy.RuntimePolicyRevision);
        Assert.Equal(fixture.Artifact.Lifecycle, policy.Lifecycle);
    }

    [Fact]
    public async Task EnsureAsync_OfflineFailureNamesRequirementRidAndImportRecovery()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.Failure = new HttpRequestException("offline");

        var result = await fixture.Service.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.DownloadFailed, result.ErrorCode);
        Assert.Contains("offline", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.EnsureRequest.Requirement.RuntimeId, result.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.EnsureRequest.Requirement.VersionRange, result.Message, StringComparison.Ordinal);
        Assert.Contains(PackageTargetSelection.GetCurrentRuntimeIdentifier(), result.Message, StringComparison.Ordinal);
        Assert.Contains("sunder runtime import --file", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion)]
    [InlineData(ExecutionRuntimeResolutionConflictCodes.RidUnavailable)]
    [InlineData(ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch)]
    public async Task EnsureAsync_PreservesTypedRegistryResolutionConflict(string conflictCode)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.InitialStatusCode = HttpStatusCode.Conflict;
        fixture.Handler.InitialResolution = fixture.InitialResolution with
        {
            Success = false,
            RuntimeArtifact = null,
            Conflicts =
            [
                new RegistryExecutionRuntimeResolutionConflict(
                    conflictCode,
                    "No compatible execution runtime could be resolved.",
                    new RegistryExecutionRuntimeRequirement(
                        fixture.EnsureRequest.Requirement.RuntimeId,
                        fixture.EnsureRequest.Requirement.VersionRange,
                        fixture.EnsureRequest.Requirement.ModuleAbi),
                    PackageTargetSelection.GetCurrentRuntimeIdentifier()),
            ],
        };

        var result = await fixture.Service.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(conflictCode, result.ErrorCode);
        Assert.Equal(["resolve"], fixture.Handler.Operations);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("fingerprint")]
    [InlineData("policy-revision")]
    [InlineData("expired")]
    [InlineData("identity")]
    [InlineData("manifest-digest")]
    [InlineData("artifact-digest")]
    [InlineData("artifact-size")]
    [InlineData("signing-key")]
    [InlineData("minimum-trust")]
    [InlineData("attestation-digest")]
    [InlineData("attestation-key")]
    [InlineData("lifecycle")]
    public async Task EnsureAsync_StaleOrConflictingReconfirmationReturnsPlanStaleWithoutPolicyAssociation(
        string confirmationCase)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.ConfirmationStatusCode = confirmationCase == "conflict"
            ? HttpStatusCode.Conflict
            : HttpStatusCode.OK;
        fixture.Handler.Confirmation = confirmationCase switch
        {
            "conflict" => fixture.InitialResolution with
            {
                Success = false,
                ResolutionFingerprint = new string('f', 64),
                Conflicts =
                [
                    new RegistryExecutionRuntimeResolutionConflict(
                        ExecutionRuntimeErrorCodes.PlanStale,
                        "The expected execution runtime resolution fingerprint is stale.",
                        new RegistryExecutionRuntimeRequirement(
                            fixture.EnsureRequest.Requirement.RuntimeId,
                            fixture.EnsureRequest.Requirement.VersionRange,
                            fixture.EnsureRequest.Requirement.ModuleAbi),
                        fixture.Artifact.Identity.Rid),
                ],
            },
            "fingerprint" => fixture.InitialResolution with
            {
                ResolutionFingerprint = new string('f', 64),
            },
            "policy-revision" => fixture.InitialResolution with
            {
                RuntimePolicyRevision = fixture.InitialResolution.RuntimePolicyRevision + 1,
                RuntimeArtifact = fixture.Artifact with
                {
                    ObservedRuntimePolicyRevision = fixture.InitialResolution.RuntimePolicyRevision + 1,
                },
            },
            "expired" => fixture.InitialResolution with { ExpiresAtUtc = Now },
            "identity" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with
                {
                    Identity = fixture.Artifact.Identity with
                    {
                        PackagingRevision = fixture.Artifact.Identity.PackagingRevision + 1,
                    },
                },
            },
            "manifest-digest" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with { ManifestSha256 = new string('d', 64) },
            },
            "artifact-digest" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with { ArtifactSha256 = new string('e', 64) },
            },
            "artifact-size" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with { ArtifactSize = fixture.Artifact.ArtifactSize + 1 },
            },
            "signing-key" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with { SigningKeyId = "sunder.release.other.v1" },
            },
            "minimum-trust" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with { MinimumHostTrustGeneration = 2 },
            },
            "attestation-digest" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with
                {
                    NativeAttestationSha256 = new string('f', 64),
                },
            },
            "attestation-key" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with
                {
                    NativeAttestationKeyId = "sunder.ci.other.v1",
                },
            },
            "lifecycle" => fixture.InitialResolution with
            {
                RuntimeArtifact = fixture.Artifact with
                {
                    Lifecycle = fixture.Artifact.Lifecycle with
                    {
                        Message = "Lifecycle authority changed during acquisition.",
                    },
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(confirmationCase)),
        };

        var result = await fixture.Service.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.Equal(SafeMessage(ExecutionRuntimeErrorCodes.PlanStale), result.Message);
        Assert.Null(result.Runtime);
        Assert.Equal(
            ["resolve", "download", "resolve"],
            fixture.Handler.Operations);
        Assert.Equal(
            new string?[] { null, fixture.InitialResolution.ResolutionFingerprint },
            fixture.Handler.ResolveRequests
                .Select(static request => request.ExpectedResolutionFingerprint)
                .ToArray());

        await fixture.LifecyclePolicies.InitializeAsync();
        Assert.Empty(fixture.LifecyclePolicies.CaptureSnapshot().Entries);
        Assert.False(File.Exists(fixture.Paths.ExecutionRuntimeLifecyclePolicyFilePath));
        var prepared = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(prepared);
    }

    [Theory]
    [InlineData("denied", RuntimeExecutionRuntimeStatuses.Degraded, ExecutionRuntimeLifecycleStates.Supported)]
    [InlineData("elapsed", RuntimeExecutionRuntimeStatuses.Degraded, ExecutionRuntimeLifecycleStates.Deprecated)]
    [InlineData("permitted", RuntimeExecutionRuntimeStatuses.Ready, ExecutionRuntimeLifecycleStates.Deprecated)]
    public async Task BuildSummary_UsesAuthoritativeActivationPermissionAndPreservesLifecycle(
        string policyCase,
        string expectedStatus,
        string expectedLifecycleState)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var ensured = await fixture.Service.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);
        Assert.True(ensured.Success, ensured.Message);
        var receipt = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(receipt);

        var lifecycle = policyCase == "denied"
            ? fixture.Artifact.Lifecycle
            : fixture.Artifact.Lifecycle with
            {
                State = ExecutionRuntimeLifecycleStates.Deprecated,
                DeprecatedAtUtc = Now.AddMinutes(-2),
                ActivationBlockedAtUtc = policyCase == "elapsed"
                    ? Now
                    : Now.AddMinutes(2),
            };
        await fixture.LifecyclePolicies.ObserveAsync(
        [
            new ExecutionRuntimeLifecyclePolicyObservation(
                receipt,
                RegistryOrigin,
                lifecycle,
                fixture.InitialResolution.RuntimePolicyRevision + 1,
                fixture.Artifact.MinimumHostTrustGeneration,
                Now,
                policyCase == "denied"
                    ? ExecutionRuntimeTrustDispositions.Denied
                    : ExecutionRuntimeTrustDispositions.Trusted),
        ]);

        var summary = await fixture.Service.GetAsync(
            fixture.StoreIdentity.RuntimeId,
            fixture.StoreIdentity.Version,
            fixture.StoreIdentity.PackagingRevision,
            fixture.StoreIdentity.Rid,
            fixture.StoreIdentity.ModuleAbi,
            CancellationToken.None);

        Assert.Equal(expectedStatus, summary.Status);
        Assert.Equal(expectedLifecycleState, summary.Lifecycle.State);
        Assert.Equal(RuntimeExecutionRuntimeStatuses.Ready, summary.Health.Status);
        Assert.False(summary.Health.RepairRecommended);
        if (expectedStatus == RuntimeExecutionRuntimeStatuses.Degraded)
        {
            Assert.Contains(
                "Update dependent packages",
                Assert.IsType<string>(summary.Lifecycle.PolicyMessage),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BuildSummary_MapsInternalVerificationFailureToStableSafeHealthMessage()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var ensured = await fixture.Service.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);
        Assert.True(ensured.Success, ensured.Message);
        var receipt = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(receipt);
        var launcherPath = Path.Combine(
            fixture.Layout.GetContentRootPath(receipt.ContentIdentity),
            receipt.LauncherPath.Replace('/', Path.DirectorySeparatorChar));
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(
            fixture.Layout.GetContentRootPath(receipt.ContentIdentity));
        await File.WriteAllBytesAsync(launcherPath, [0x00]);
        ExecutionRuntimeRootFileSystem.Seal(
            fixture.Layout.GetContentRootPath(receipt.ContentIdentity),
            launcherPath);

        var summary = await fixture.Service.GetAsync(
            fixture.StoreIdentity.RuntimeId,
            fixture.StoreIdentity.Version,
            fixture.StoreIdentity.PackagingRevision,
            fixture.StoreIdentity.Rid,
            fixture.StoreIdentity.ModuleAbi,
            CancellationToken.None);

        Assert.Equal(RuntimeExecutionRuntimeStatuses.Unhealthy, summary.Status);
        Assert.Equal(RuntimeExecutionRuntimeStatuses.Unhealthy, summary.Health.Status);
        Assert.True(summary.Health.RepairRecommended);
        var publicMessage = Assert.IsType<string>(summary.Health.Message);
        Assert.Equal(SafeMessage(ExecutionRuntimeErrorCodes.IntegrityFailed), publicMessage);
        Assert.DoesNotContain(fixture.RootPath, publicMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(launcherPath), publicMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairAsync_MissingExactRuntime_RestoresBoundRevisionAndReturnsSharedImpact()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync(
            "test.runtime.recovery.one",
            "test.runtime.recovery.two");
        var impact = await fixture.GetImpactAsync();
        Assert.Equal(2, impact.PackageReferenceCount);
        Assert.Equal(
            ["test.runtime.recovery.one", "test.runtime.recovery.two"],
            impact.ImpactedPackageIds);
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.True(result.Success, result.Message);
        Assert.False(result.AlreadyPresent);
        Assert.Equal(impact.Fingerprint, result.Impact?.Fingerprint);
        Assert.Equal(impact.ImpactedPackageIds, result.Impact?.ImpactedPackageIds);
        Assert.Equal(
            ["resolve", "release", "download", "resolve", "release"],
            fixture.Handler.Operations);
        var restored = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(restored);
        Assert.Equal(receipt.Identity, restored.Identity);
        Assert.Equal(receipt.ManifestSha256, restored.ManifestSha256);
        Assert.Equal(receipt.ArtifactSha256, restored.ArtifactSha256);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(restored.ContentIdentity)));
        var packages = await fixture.InstalledPackages.ListAsync();
        Assert.Equal(2, packages.Count);
        Assert.All(packages, package =>
        {
            Assert.Equal("1.0.0", package.Version);
            var binding = Assert.Single(package.ExecutionRuntimeBindings);
            Assert.Equal(receipt.Identity.Version, binding.RuntimeVersion);
            Assert.Equal(receipt.Identity.PackagingRevision, binding.RuntimePackagingRevision);
            Assert.Equal(receipt.ArtifactSha256, binding.RuntimeArtifactSha256);
        });
    }

    [Fact]
    public async Task RepairAsync_MalformedReceipt_RestoresBoundRevisionAndReturnsSharedImpact()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var impact = await fixture.GetImpactAsync();
        await File.WriteAllTextAsync(fixture.Layout.GetReceiptPath(receipt.Identity), "{");
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.True(result.Success, result.Message);
        Assert.False(result.AlreadyPresent);
        Assert.Equal(impact.Fingerprint, result.Impact?.Fingerprint);
        Assert.Equal(impact.ImpactedPackageIds, result.Impact?.ImpactedPackageIds);
        Assert.Equal(
            ["resolve", "release", "resolve", "release"],
            fixture.Handler.Operations);
        var restored = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(restored);
        Assert.Equal(receipt.Identity, restored.Identity);
        Assert.Equal(receipt.ManifestSha256, restored.ManifestSha256);
        Assert.Equal(receipt.ArtifactSha256, restored.ArtifactSha256);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(restored.ContentIdentity)));
    }

    [Fact]
    public async Task RepairAsync_RejectsStaleSharedImpactBeforeRecoveryMutation()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var staleImpact = await fixture.GetImpactAsync();
        await fixture.AddBoundPackageAsync("test.runtime.recovery.two", receipt);
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(staleImpact);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.Empty(fixture.Handler.Operations);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
    }

    [Fact]
    public async Task RepairAsync_OfflineAuthorityFailsDistinctlyWithoutReplacingRuntimeState()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();
        fixture.Handler.Failure = new HttpRequestException("offline");

        var result = await fixture.RepairAsync(impact);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.Offline, result.ErrorCode);
        Assert.Equal(impact.Fingerprint, result.Impact?.Fingerprint);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task RepairAsync_ExactCachedLocalImportIsRestoredWithoutRegistryDownload()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.localimport");
        await fixture.UpdateBindingAsync(
            "test.runtime.localimport",
            binding => binding with
            {
                AcquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
            });
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: false);
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            ["resolve", "release", "resolve", "release"],
            fixture.Handler.Operations);
        var restored = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(restored);
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.LocalImport, restored.AcquisitionSource);
        Assert.Null(restored.RegistryOrigin);
    }

    [Fact]
    public async Task RepairAsync_MissingLocalImportCacheDoesNotSubstituteRegistryArtifact()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.localimport");
        await fixture.UpdateBindingAsync(
            "test.runtime.localimport",
            binding => binding with
            {
                AcquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
            });
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.Missing, result.ErrorCode);
        Assert.Contains("Import that exact trusted artifact again", result.Message, StringComparison.Ordinal);
        Assert.Equal(["resolve", "release"], fixture.Handler.Operations);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
    }

    [Fact]
    public async Task RepairAsync_BindingFromOlderTrustGenerationRequiresExplicitRebind()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.oldtrust");
        await fixture.UpdateBindingAsync(
            "test.runtime.oldtrust",
            binding => binding with { TrustStoreGeneration = binding.TrustStoreGeneration - 1 });
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, result.ErrorCode);
        Assert.Contains("explicit rebind", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["resolve", "release"], fixture.Handler.Operations);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
    }

    [Theory]
    [InlineData(ExecutionRuntimeLifecycleStates.Retired, ExecutionRuntimeErrorCodes.Retired)]
    [InlineData(ExecutionRuntimeLifecycleStates.Revoked, ExecutionRuntimeErrorCodes.Revoked)]
    public async Task RepairAsync_CurrentLifecycleBlockFailsWithoutDownloading(
        string lifecycleState,
        string expectedErrorCode)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.SetRecoveryLifecycle(lifecycleState);
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.False(result.Success);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(["resolve"], fixture.Handler.Operations);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
    }

    [Fact]
    public async Task RepairAsync_CorruptRegistryAuthorityFailsBeforeDownloading()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.CorruptRecoveryAuthority();
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.IntegrityFailed, result.ErrorCode);
        Assert.Equal(["resolve", "release"], fixture.Handler.Operations);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
    }

    [Fact]
    public async Task RepairAsync_ReloadFailureRetainsPreviousPackageGeneration()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync(
            "test.runtime.recovery.one",
            "test.runtime.recovery.two");
        var loaded = await fixture.PackageLifecycle.LoadInstalledPackagesAsync();
        Assert.True(loaded.Success, loaded.Message);
        var previousStamp = fixture.PackageLifecycle.Stamp;
        var previousSession = fixture.Sessions.GetSnapshot();
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        var corrupted = (await fixture.InstalledPackages.ListAsync())
            .Single(package => package.PackageId == "test.runtime.recovery.one");
        await File.AppendAllTextAsync(corrupted.ManifestPath, "corrupt");
        fixture.Handler.ResetOperations();

        var result = await fixture.RepairAsync(impact);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.InstallFailed, result.ErrorCode);
        Assert.Contains("previous healthy generation", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previousStamp, fixture.PackageLifecycle.Stamp);
        Assert.Equal(previousSession.SessionPackages, fixture.Sessions.GetSnapshot().SessionPackages);
        Assert.NotNull(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
    }

    [Fact]
    public async Task RepairAsync_CancellationDuringDownloadLeavesPackageGenerationAndRuntimeMissing()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var loaded = await fixture.PackageLifecycle.LoadInstalledPackagesAsync();
        Assert.True(loaded.Success, loaded.Message);
        var previousStamp = fixture.PackageLifecycle.Stamp;
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();
        fixture.Handler.BlockNextDownload();
        using var cancellation = new CancellationTokenSource();

        var repair = fixture.RepairAsync(impact, cancellation.Token);
        await fixture.Handler.DownloadEntered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repair);
        Assert.Equal(previousStamp, fixture.PackageLifecycle.Stamp);
        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.StoreIdentity));
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task RepairAsync_CancellationDuringAuthorityReconfirmationRetainsGenerationAndRestoredRuntime()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackagesAsync("test.runtime.recovery.one");
        var loaded = await fixture.PackageLifecycle.LoadInstalledPackagesAsync();
        Assert.True(loaded.Success, loaded.Message);
        var previousStamp = fixture.PackageLifecycle.Stamp;
        var impact = await fixture.GetImpactAsync();
        fixture.DeleteInstalledRuntime(receipt, deleteArchive: true);
        fixture.Handler.ResetOperations();
        fixture.Handler.BlockNextConfirmation();
        using var cancellation = new CancellationTokenSource();

        var repair = fixture.RepairAsync(impact, cancellation.Token);
        await fixture.Handler.ConfirmationEntered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repair);
        Assert.Equal(previousStamp, fixture.PackageLifecycle.Stamp);
        var restored = await fixture.Store.GetReceiptAsync(fixture.StoreIdentity);
        Assert.NotNull(restored);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(restored.ContentIdentity)));
        Assert.Equal(["resolve", "release", "download", "resolve"], fixture.Handler.Operations);
    }

    [Fact]
    public async Task CollectGarbageAsync_CollectsExecutionRuntimeAndProjectionCaches()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.RootPath, "projection-source");
        Directory.CreateDirectory(sourceRoot);
        var sourcePath = Path.Combine(sourceRoot, "entry.bin");
        byte[] content = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(sourcePath, content);
        var acquisition = await fixture.Projections.GetOrBuildAndLeaseAsync(
            new PackageProjectionRequest(
                sourceRoot,
                "test.projection.gc",
                new string('c', 64),
                PackageProjectionRole.App,
                PackageProjectionCache.DefaultRulesIdentity,
                ["app/test"],
                [new PackageProjectionSourceFile("entry.bin", "entry.bin", Hash(content), content.LongLength)]),
            new PackageProjectionOwner(
                "runtime-management-test",
                Guid.NewGuid(),
                "test.projection.gc",
                "generation-1"));
        var projectionRoot = acquisition.RootPath;
        await acquisition.DisposeAsync();

        var marked = await fixture.Service.CollectGarbageAsync(CancellationToken.None);

        Assert.True(marked.Success, marked.Message);
        Assert.Equal(RuntimeExecutionRuntimeOperationActions.GarbageCollect, marked.Action);
        Assert.Contains("Package projection GC", marked.Message, StringComparison.Ordinal);
        Assert.Contains("marked 1 entries", marked.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(projectionRoot));

        var removed = await fixture.Service.CollectGarbageAsync(CancellationToken.None);

        Assert.True(removed.Success, removed.Message);
        Assert.Contains("removed 1 entries", removed.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(projectionRoot));
    }

    private static string SafeMessage(string code)
        => ExecutionRuntimeErrorCatalog.All.Single(descriptor => descriptor.Code == code).SafeMessage;

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] CreateTestSignature(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var bytes = new byte[key.Length + input.Length];
        key.CopyTo(bytes);
        input.CopyTo(bytes.AsSpan(key.Length));
        return SHA512.HashData(bytes);
    }

    private sealed class ManagementFixture : IAsyncDisposable
    {
        private ManagementFixture(
            string rootPath,
            string archivePath,
            RuntimePackagePaths paths,
            ExecutionRuntimeStoreLayout layout,
            ExecutionRuntimeStore store,
            ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
            PackageProjectionCache projections,
            ExecutionRuntimeManagementService service,
            ScriptedRegistryHandler handler,
            RegistryResolvedExecutionRuntimeArtifact artifact,
            RegistryResolveExecutionRuntimeResponse initialResolution,
            CapturingLogger<ExecutionRuntimeManagementService> logger,
            ServiceProvider provider)
        {
            RootPath = rootPath;
            ArchivePath = archivePath;
            Paths = paths;
            Layout = layout;
            Store = store;
            LifecyclePolicies = lifecyclePolicies;
            Projections = projections;
            Service = service;
            Handler = handler;
            Artifact = artifact;
            InitialResolution = initialResolution;
            Logger = logger;
            Provider = provider;
            StoreIdentity = new ExecutionRuntimeIdentity(
                artifact.Identity.RuntimeId,
                artifact.Identity.Version,
                artifact.Identity.PackagingRevision,
                artifact.Identity.Rid,
                artifact.Identity.ModuleAbi);
            EnsureRequest = new RuntimeExecutionRuntimeEnsureRequest(
                RegistryOrigin,
                new RuntimeExecutionRuntimeRequirement(
                    artifact.Identity.RuntimeId,
                    ">=10.0.0 <11.0.0",
                    artifact.Identity.ModuleAbi));
        }

        public string RootPath { get; }
        public string ArchivePath { get; }
        public RuntimePackagePaths Paths { get; }
        public ExecutionRuntimeStoreLayout Layout { get; }
        public ExecutionRuntimeStore Store { get; }
        public ExecutionRuntimeLifecyclePolicyCache LifecyclePolicies { get; }
        public PackageProjectionCache Projections { get; }
        public ExecutionRuntimeManagementService Service { get; }
        public InstalledPackageStore InstalledPackages => Provider.GetRequiredService<InstalledPackageStore>();
        public InstalledPackageLifecycleService PackageLifecycle =>
            Provider.GetRequiredService<InstalledPackageLifecycleService>();
        public RuntimeSessionOwner Sessions => Provider.GetRequiredService<RuntimeSessionOwner>();
        public ScriptedRegistryHandler Handler { get; }
        public RegistryResolvedExecutionRuntimeArtifact Artifact { get; }
        public RegistryResolveExecutionRuntimeResponse InitialResolution { get; }
        public CapturingLogger<ExecutionRuntimeManagementService> Logger { get; }
        public ExecutionRuntimeIdentity StoreIdentity { get; }
        public RuntimeExecutionRuntimeEnsureRequest EnsureRequest { get; }
        private ServiceProvider Provider { get; }

        public static async Task<ManagementFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "SunderExecutionRuntimeManagementTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var (archivePath, archiveBytes, artifact) = await CreateArtifactAsync(root);
                var initialResolution = new RegistryResolveExecutionRuntimeResponse(
                    true,
                    artifact,
                    new string('a', 64),
                    artifact.ObservedRuntimePolicyRevision,
                    Now.AddMinutes(5),
                    [],
                    []);
                var handler = new ScriptedRegistryHandler(archiveBytes, initialResolution);
                var paths = new RuntimePackagePaths(Path.Combine(root, "state"));
                RuntimeLocalState.EnsureInitialized(paths.RootPath);
                var trust = new ExecutionRuntimeHostTrustService(
                    "management-test",
                    generation: 3,
                    developmentTrustEnabled: false,
                    productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]);
                var timeProvider = new FixedTimeProvider(Now);
                var logger = new CapturingLogger<ExecutionRuntimeManagementService>();
                var services = new ServiceCollection();
                services.AddRuntimeHostServices(
                    paths,
                    new RuntimeBearerTokenValidator("management-test-token"),
                    executionRuntimeTrust: trust);
                services.AddSingleton<TimeProvider>(timeProvider);
                services.AddSingleton<ILogger<ExecutionRuntimeManagementService>>(logger);
                services.AddSingleton(new PackageProjectionCache(
                    paths.RootPath,
                    new PackageProjectionCacheOptions
                    {
                        GarbageCollectionGracePeriod = TimeSpan.Zero,
                    },
                    timeProvider: timeProvider));
                services.AddSingleton<IExecutionRuntimeGarbageCollectionAuthority>(
                    new TestExecutionRuntimeGarbageCollectionAuthority());
                services.AddSingleton(provider => new ExecutionRuntimeStore(
                    provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                    trust,
                    provider.GetRequiredService<IExecutionRuntimeGarbageCollectionAuthority>(),
                    timeProvider,
                    signatureVerifier: new TestSignatureVerifier()));
                services.AddSingleton(provider => new ExecutionRuntimeArtifactDownloader(
                    provider.GetRequiredService<RegistryHttpClient>(),
                    provider.GetRequiredService<ExecutionRuntimeStore>(),
                    trust,
                    timeProvider,
                    Path.Combine(root, "downloads")));
                services.AddHttpClient("registry")
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                var provider = services.BuildServiceProvider();
                return new ManagementFixture(
                    root,
                    archivePath,
                    paths,
                    provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                    provider.GetRequiredService<ExecutionRuntimeStore>(),
                    provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>(),
                    provider.GetRequiredService<PackageProjectionCache>(),
                    provider.GetRequiredService<ExecutionRuntimeManagementService>(),
                    handler,
                    artifact,
                    initialResolution,
                    logger,
                    provider);
            }
            catch
            {
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async Task<ExecutionRuntimeReceiptV1> EnsureAndBindPackagesAsync(params string[] packageIds)
        {
            var ensured = await Service.EnsureAsync(EnsureRequest, CancellationToken.None);
            Assert.True(ensured.Success, ensured.Message);
            var receipt = await Store.GetReceiptAsync(StoreIdentity)
                          ?? throw new InvalidOperationException("The test runtime receipt was not installed.");
            Handler.ReleaseDetails = CreateReleaseDetails(receipt, Artifact);
            foreach (var packageId in packageIds)
            {
                await AddBoundPackageAsync(packageId, receipt);
            }
            return receipt;
        }

        public async Task AddBoundPackageAsync(string packageId, ExecutionRuntimeReceiptV1 receipt)
        {
            var installPath = Paths.GetInstalledPackagePath(packageId, "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                installPath,
                packageId,
                "1.0.0",
                typeof(ExecutionRuntimeManagementServiceTests).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [receipt.Identity.Rid]);
            var binding = new InstalledPackageExecutionRuntimeBindingRecord(
                SunderPackageFormat.RuntimeHostRole,
                receipt.Identity.Rid,
                receipt.Identity.RuntimeId,
                receipt.Identity.Version,
                receipt.Identity.PackagingRevision,
                receipt.Identity.ModuleAbi,
                receipt.ManifestSha256,
                receipt.ArtifactSha256,
                receipt.ArtifactSize,
                RegistryOrigin,
                receipt.AcquisitionSource,
                receipt.SigningKeyId,
                receipt.TrustGeneration,
                Artifact.ObservedRuntimePolicyRevision,
                Artifact.Lifecycle.State,
                Now,
                Now);
            var package = CanonicalPackageTestBuilder.CreateInstalledRecord(
                installPath,
                packageId,
                "1.0.0",
                isEnabled: false,
                executionRuntimeBindings: [binding]);
            var packages = (await InstalledPackages.ListAsync()).Append(package).ToArray();
            await InstalledPackages.WriteAsync(packages);
        }

        public async Task UpdateBindingAsync(
            string packageId,
            Func<InstalledPackageExecutionRuntimeBindingRecord, InstalledPackageExecutionRuntimeBindingRecord> update)
        {
            var packages = await InstalledPackages.ListAsync();
            var changed = packages.Select(package =>
                    string.Equals(package.PackageId, packageId, StringComparison.Ordinal)
                        ? package with
                        {
                            ExecutionRuntimeBindings = package.ExecutionRuntimeBindings
                                .Select(update)
                                .ToArray(),
                        }
                        : package)
                .ToArray();
            await InstalledPackages.WriteAsync(changed);
        }

        public Task<RuntimeExecutionRuntimeImpact> GetImpactAsync()
            => Service.GetImpactAsync(
                StoreIdentity.RuntimeId,
                StoreIdentity.Version,
                StoreIdentity.PackagingRevision,
                StoreIdentity.Rid,
                StoreIdentity.ModuleAbi,
                CancellationToken.None);

        public Task<RuntimeExecutionRuntimeOperationResult> RepairAsync(
            RuntimeExecutionRuntimeImpact impact,
            CancellationToken cancellationToken = default)
            => Service.RepairAsync(
                StoreIdentity.RuntimeId,
                StoreIdentity.Version,
                StoreIdentity.PackagingRevision,
                StoreIdentity.Rid,
                StoreIdentity.ModuleAbi,
                new RuntimeExecutionRuntimeActionRequest(impact.Fingerprint),
                cancellationToken);

        public void DeleteInstalledRuntime(ExecutionRuntimeReceiptV1 receipt, bool deleteArchive)
        {
            var contentRoot = Layout.GetContentRootPath(receipt.ContentIdentity);
            if (Directory.Exists(contentRoot))
            {
                ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(contentRoot);
                Assert.True(ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(contentRoot));
            }
            DeleteFile(Layout.GetHealthPath(receipt.ContentIdentity));
            DeleteFile(Layout.GetReceiptPath(receipt.Identity));
            if (deleteArchive)
            {
                DeleteFile(Layout.GetArchivePath(receipt.ArtifactSha256));
            }
        }

        public void SetRecoveryLifecycle(string state)
        {
            var details = Handler.ReleaseDetails
                          ?? throw new InvalidOperationException("Recovery release details are not initialized.");
            var lifecycle = details.Release.Lifecycle with
            {
                State = state,
                DeprecatedAtUtc = Now.AddHours(-2),
                InstallBlockedAtUtc = Now.AddHours(-1),
                ActivationBlockedAtUtc = Now.AddMinutes(-30),
                RevokedAtUtc = state == ExecutionRuntimeLifecycleStates.Revoked ? Now : null,
                Message = $"Runtime is {state}.",
            };
            Handler.ReleaseDetails = details with
            {
                Release = details.Release with { Lifecycle = lifecycle },
                Artifacts = details.Artifacts
                    .Select(item => item with
                    {
                        Artifact = item.Artifact with { Lifecycle = lifecycle },
                    })
                    .ToArray(),
            };
            Handler.Confirmation = Handler.Confirmation with
            {
                Success = false,
                RuntimeArtifact = null,
                Conflicts =
                [
                    new RegistryExecutionRuntimeResolutionConflict(
                        state == ExecutionRuntimeLifecycleStates.Revoked
                            ? ExecutionRuntimeErrorCodes.Revoked
                            : ExecutionRuntimeErrorCodes.Retired,
                        lifecycle.Message!,
                        new RegistryExecutionRuntimeRequirement(
                            StoreIdentity.RuntimeId,
                            StoreIdentity.Version,
                            StoreIdentity.ModuleAbi),
                        StoreIdentity.Rid,
                        CurrentRuntime: new RegistryExecutionRuntimeIdentity(
                            StoreIdentity.RuntimeId,
                            StoreIdentity.Version,
                            StoreIdentity.PackagingRevision,
                            StoreIdentity.Rid,
                            StoreIdentity.ModuleAbi)),
                ],
            };
        }

        public void CorruptRecoveryAuthority()
        {
            var details = Handler.ReleaseDetails
                          ?? throw new InvalidOperationException("Recovery release details are not initialized.");
            Handler.ReleaseDetails = details with
            {
                Artifacts = details.Artifacts
                    .Select(item => item with
                    {
                        Artifact = item.Artifact with { ArtifactSha256 = new string('e', 64) },
                    })
                    .ToArray(),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            if (Directory.Exists(Projections.RootPath))
            {
                await using var reset = await Projections.AcquireOfflineResetScopeAsync();
                await reset.ValidateAsync();
                await reset.PrepareForDeletionAsync();
                Directory.Delete(Projections.RootPath, recursive: true);
            }
            _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        }

        private static RegistryExecutionRuntimeReleaseDetails CreateReleaseDetails(
            ExecutionRuntimeReceiptV1 receipt,
            RegistryResolvedExecutionRuntimeArtifact artifact)
        {
            var publishedAtUtc = Now.AddDays(-1);
            return new RegistryExecutionRuntimeReleaseDetails(
                new RegistryExecutionRuntimeReleaseSummary(
                    receipt.Identity.RuntimeId,
                    receipt.Identity.Version,
                    receipt.Identity.PackagingRevision,
                    receipt.Identity.ModuleAbi,
                    receipt.WorkerProtocol,
                    artifact.MinimumHostTrustGeneration,
                    artifact.Lifecycle,
                    [receipt.Identity.Rid],
                    publishedAtUtc),
                [new RegistryExecutionRuntimeArtifactDetails(
                    artifact,
                    receipt.ContentIdentity,
                    publishedAtUtc)]);
        }

        private static void DeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        private static async Task<(string ArchivePath, byte[] ArchiveBytes, RegistryResolvedExecutionRuntimeArtifact Artifact)>
            CreateArtifactAsync(string root)
        {
            var artifactRoot = Path.Combine(root, "artifact");
            var sources = Path.Combine(artifactRoot, "sources");
            Directory.CreateDirectory(sources);
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var launcherPath = Path.Combine(
                sources,
                rid.StartsWith("win-", StringComparison.Ordinal) ? "sunder-runtime.exe" : "sunder-runtime");
            var runtimePath = Path.Combine(sources, "runtime.bin");
            var attestationPath = Path.Combine(sources, "native-attestation.json");
            await File.WriteAllBytesAsync(launcherPath, [0x53, 0x55, 0x4e, 0x44, 0x45, 0x52]);
            await File.WriteAllBytesAsync(runtimePath, Enumerable.Repeat((byte)0x42, 64).ToArray());

            const string runtimeVersion = "10.0.1";
            const int packagingRevision = 7;
            var upstreamSha256 = new string('b', 64);
            var nativeAttestation = new SunderExecutionRuntimeNativeAttestation
            {
                SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
                RuntimeId = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                RuntimeVersion = runtimeVersion,
                PackagingRevision = packagingRevision,
                Rid = rid,
                UpstreamSha256 = upstreamSha256,
                Workflow = new SunderExecutionRuntimeAttestationWorkflow
                {
                    Name = "sunder.runtime.release",
                    RunId = "management-test-run",
                    SourceRevision = new string('c', 40),
                },
                Toolchain = new SunderExecutionRuntimeAttestationToolchain
                {
                    Name = "dotnet-sdk",
                    Version = "10.0.100",
                },
                LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
                {
                    Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
                },
                Platform = new SunderExecutionRuntimeAttestationPlatform
                {
                    Signature = rid.StartsWith("linux-", StringComparison.Ordinal)
                        ? SunderExecutionRuntimeFormat.PlatformCheckNotApplicable
                        : SunderExecutionRuntimeFormat.PlatformCheckPassed,
                    Notarization = rid.StartsWith("osx-", StringComparison.Ordinal)
                        ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                        : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
                },
                Signing = new SunderExecutionRuntimeAttestationSigning
                {
                    Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                    KeyId = AttestationKeyId,
                    Signature = Convert.ToBase64String(
                        new byte[SunderExecutionRuntimeFormat.Ed25519SignatureBytes]),
                },
            };
            var nativeAttestationBytes = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(
                nativeAttestation);
            await File.WriteAllBytesAsync(attestationPath, nativeAttestationBytes);

            var manifest = new SunderExecutionRuntimeManifest
            {
                SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
                Id = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                Version = runtimeVersion,
                PackagingRevision = packagingRevision,
                Rid = rid,
                ModuleAbi = SunderExecutionRuntimeFormat.DotNetModuleAbi,
                WorkerProtocol = SunderExecutionRuntimeFormat.WorkerProtocol,
                Launch = new SunderExecutionRuntimeLaunch
                {
                    Executable = "payload/bin/" + Path.GetFileName(launcherPath),
                    Arguments =
                    [
                        SunderExecutionRuntimeFormat.DotNetExecArgument,
                        SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                    ],
                },
                ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
                Upstream = new SunderExecutionRuntimeUpstream
                {
                    Name = "dotnet",
                    Version = runtimeVersion,
                    SourceUri = "https://example.test/dotnet-runtime.tar.gz",
                    SourceSha256 = upstreamSha256,
                },
                Provenance = new SunderExecutionRuntimeProvenance
                {
                    AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                    AttestationSha256 = Hash(nativeAttestationBytes),
                    AttestationKeyId = AttestationKeyId,
                },
                Licenses = ["MIT"],
                Signing = new SunderExecutionRuntimeSigning
                {
                    Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                    KeyId = ReleaseKeyId,
                    Signature = SunderExecutionRuntimeFormat.SignaturePath,
                },
            };
            var archivePath = Path.Combine(artifactRoot, "runtime.sunderruntime");
            await SunderExecutionRuntimeArchiveWriter.WriteAsync(
                manifest,
                archivePath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [manifest.Launch.Executable!] = launcherPath,
                    ["payload/runtime/runtime.bin"] = runtimePath,
                    [SunderExecutionRuntimeFormat.NativeAttestationPath] = attestationPath,
                },
                new TestSigner());
            var archiveBytes = await File.ReadAllBytesAsync(archivePath);
            var lifecycle = new RegistryExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                Now.AddDays(-2),
                Now.AddDays(-1),
                DeprecatedAtUtc: null,
                InstallBlockedAtUtc: null,
                ActivationBlockedAtUtc: null,
                RevokedAtUtc: null,
                Message: null);
            var artifact = new RegistryResolvedExecutionRuntimeArtifact(
                new RegistryExecutionRuntimeIdentity(
                    manifest.Id!,
                    manifest.Version!,
                    manifest.PackagingRevision,
                    manifest.Rid!,
                    manifest.ModuleAbi!),
                Hash(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest)),
                Hash(archiveBytes),
                archiveBytes.LongLength,
                ReleaseKeyId,
                MinimumHostTrustGeneration: 1,
                manifest.Provenance.AttestationSha256!,
                AttestationKeyId,
                lifecycle,
                ObservedRuntimePolicyRevision: 7,
                "https://registry.example/artifacts/runtime.sunderruntime");
            return (archivePath, archiveBytes, artifact);
        }
    }

    private sealed class ScriptedRegistryHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly byte[] _archiveBytes;
        private int _resolveCount;
        private readonly List<string> _operations = [];
        private readonly List<RegistryResolveExecutionRuntimeRequest> _resolveRequests = [];
        private TaskCompletionSource _downloadEntered = CompletedSource();
        private TaskCompletionSource? _downloadBlock;
        private TaskCompletionSource _confirmationEntered = CompletedSource();
        private TaskCompletionSource? _confirmationBlock;

        public ScriptedRegistryHandler(
            byte[] archiveBytes,
            RegistryResolveExecutionRuntimeResponse initialResolution)
        {
            _archiveBytes = archiveBytes;
            InitialResolution = initialResolution;
            Confirmation = initialResolution;
        }

        public RegistryResolveExecutionRuntimeResponse InitialResolution { get; set; }
        public HttpStatusCode InitialStatusCode { get; set; } = HttpStatusCode.OK;
        public RegistryResolveExecutionRuntimeResponse Confirmation { get; set; }
        public HttpStatusCode ConfirmationStatusCode { get; set; } = HttpStatusCode.OK;
        public RegistryExecutionRuntimeReleaseDetails? ReleaseDetails { get; set; }
        public Exception? Failure { get; set; }

        public Task DownloadEntered
        {
            get
            {
                lock (_sync)
                {
                    return _downloadEntered.Task;
                }
            }
        }

        public Task ConfirmationEntered
        {
            get
            {
                lock (_sync)
                {
                    return _confirmationEntered.Task;
                }
            }
        }

        public IReadOnlyList<string> Operations
        {
            get
            {
                lock (_sync)
                {
                    return _operations.ToArray();
                }
            }
        }

        public IReadOnlyList<RegistryResolveExecutionRuntimeRequest> ResolveRequests
        {
            get
            {
                lock (_sync)
                {
                    return _resolveRequests.ToArray();
                }
            }
        }

        public void ResetOperations()
        {
            lock (_sync)
            {
                _operations.Clear();
            }
        }

        public void BlockNextDownload()
        {
            lock (_sync)
            {
                _downloadEntered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _downloadBlock = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void BlockNextConfirmation()
        {
            lock (_sync)
            {
                _confirmationEntered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _confirmationBlock = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath.Contains("/execution-runtimes/", StringComparison.Ordinal)
                    && ReleaseDetails is { } releaseDetails)
                {
                    lock (_sync)
                    {
                        _operations.Add("release");
                    }
                    return Response(request, HttpStatusCode.OK, JsonContent.Create(releaseDetails));
                }
                lock (_sync)
                {
                    _operations.Add("download");
                }
                TaskCompletionSource? downloadBlock;
                lock (_sync)
                {
                    _downloadEntered.TrySetResult();
                    downloadBlock = _downloadBlock;
                    _downloadBlock = null;
                }
                if (downloadBlock is not null)
                {
                    await downloadBlock.Task.WaitAsync(cancellationToken);
                }
                return Response(request, HttpStatusCode.OK, new ByteArrayContent(_archiveBytes));
            }

            var resolve = await request.Content!
                .ReadFromJsonAsync<RegistryResolveExecutionRuntimeRequest>(cancellationToken)
                .ConfigureAwait(false);
            Assert.NotNull(resolve);
            RegistryResolveExecutionRuntimeResponse result;
            HttpStatusCode status;
            TaskCompletionSource? confirmationBlock;
            lock (_sync)
            {
                _operations.Add("resolve");
                _resolveRequests.Add(resolve);
                _resolveCount++;
                result = _resolveCount == 1 ? InitialResolution : Confirmation;
                status = _resolveCount == 1 ? InitialStatusCode : ConfirmationStatusCode;
                confirmationBlock = resolve.ExpectedResolutionFingerprint is not null
                    ? _confirmationBlock
                    : null;
                if (confirmationBlock is not null)
                {
                    _confirmationEntered.TrySetResult();
                    _confirmationBlock = null;
                }
            }
            if (confirmationBlock is not null)
            {
                await confirmationBlock.Task.WaitAsync(cancellationToken);
            }
            return Response(request, status, JsonContent.Create(result));
        }

        private static HttpResponseMessage Response(
            HttpRequestMessage request,
            HttpStatusCode status,
            HttpContent content)
            => new(status)
            {
                RequestMessage = request,
                Content = content,
            };

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    private sealed class TestSigner : ISunderExecutionRuntimeArchiveSigner
    {
        public string KeyId => ReleaseKeyId;

        public byte[] Sign(ReadOnlySpan<byte> signatureInput)
            => CreateTestSignature(signatureInput, ReleaseKey);
    }

    private sealed class TestSignatureVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
    {
        public bool Verify(
            ReadOnlySpan<byte> signatureInput,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> trustedPublicKey)
            => CryptographicOperations.FixedTimeEquals(
                signature,
                CreateTestSignature(signatureInput, trustedPublicKey));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? LastException { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => LastException = exception;
    }
}
