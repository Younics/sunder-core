using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryPackageInstallServiceTests
{
    private static readonly string ResolutionFingerprint = new('f', 64);

    [Fact]
    public async Task ApprovedConsentIsForwardedExactlyFromPreflightToInstall()
    {
        var consent = Consent('a');
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = SuccessfulPlan(consent),
        };
        RegistryPackageChangePreflight? prompted = null;
        var service = new RegistryPackageInstallService((preflight, _) =>
        {
            prompted = preflight;
            return Task.FromResult(true);
        });

        var result = await service.InstallPackageAsync(
            "demo",
            "1.2.3",
            null,
            allowDowngrade: false,
            reinstall: false,
            new FakeRegistryClient(),
            runtime);

        Assert.True(result.Success);
        Assert.Equal([consent], prompted!.Plan.RequiredCapabilityConsents);
        Assert.Single(runtime.PlanRequests);
        var install = Assert.Single(runtime.InstallRequests);
        Assert.Equal([consent], install.CapabilityConsents);
        Assert.Equal(ResolutionFingerprint, install.ExpectedResolutionFingerprint);
        var expectedPlan = Assert.Single(prompted.ExpectedPlans);
        Assert.Equal("https://registry.example/", expectedPlan.RegistryOrigin);
        Assert.False(expectedPlan.IncludePrerelease);
        Assert.Equal(ResolutionFingerprint, expectedPlan.ExpectedResolutionFingerprint);
    }

    [Fact]
    public async Task DeclinedConsentReturnsRequirementsWithoutApplyingInstall()
    {
        var consent = Consent('b');
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = SuccessfulPlan(consent),
        };
        var service = new RegistryPackageInstallService((_, _) => Task.FromResult(false));

        var result = await service.InstallPackageAsync(
            "demo",
            "1.2.3",
            null,
            allowDowngrade: false,
            reinstall: false,
            new FakeRegistryClient(),
            runtime);

        Assert.False(result.Success);
        Assert.Empty(runtime.InstallRequests);
        Assert.Equal([consent], result.RequiredCapabilityConsents);
        Assert.Contains("no package changes", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StalePreviewSurfacesRetryMessageWithoutHidingTheConfirmedFingerprint()
    {
        const string message =
            "The Registry package plan changed after it was previewed. Review the current plan and retry; no package changes were applied.";
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = SuccessfulPlan(RuntimeArtifact()),
            InstallResult = new RuntimeRegistryPackageChangeResult(
                false,
                RuntimeRegistryErrorCode.StalePlan,
                message,
                false,
                false,
                [],
                [message],
                [],
                []),
        };
        var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true));

        var result = await service.InstallPackageAsync(
            "demo",
            "1.2.3",
            null,
            allowDowngrade: false,
            reinstall: false,
            new FakeRegistryClient(),
            runtime);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal(ResolutionFingerprint, Assert.Single(runtime.InstallRequests).ExpectedResolutionFingerprint);
    }

    [Theory]
    [InlineData(false, 50L * 1024L * 1024L, 0L)]
    [InlineData(true, 0L, 50L * 1024L * 1024L)]
    public async Task ConfirmationSeparatesPackageBytesFromIncrementalSharedRuntimeBytes(
        bool runtimeInstalled,
        long expectedRuntimeDownload,
        long expectedRuntimeReuse)
    {
        var runtimeArtifact = RuntimeArtifact();
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = SuccessfulPlan(runtimeArtifact),
            ExecutionRuntimes = runtimeInstalled ? [InstalledRuntime(runtimeArtifact)] : [],
        };
        RegistryPackageChangePreflight? prompted = null;
        var service = new RegistryPackageInstallService((preflight, _) =>
        {
            prompted = preflight;
            return Task.FromResult(true);
        });

        var result = await service.InstallPackageAsync(
            "demo",
            "1.2.3",
            null,
            allowDowngrade: false,
            reinstall: false,
            new FakeRegistryClient(),
            runtime);

        Assert.True(result.Success);
        Assert.NotNull(prompted);
        Assert.Equal(12L * 1024L * 1024L, prompted.PackageDownloadBytes);
        Assert.Equal(expectedRuntimeDownload, prompted.SharedRuntimeDownloadBytes);
        Assert.Equal(expectedRuntimeReuse, prompted.ReusedSharedRuntimeBytes);
    }

    [Fact]
    public async Task UpdateConfirmationUsesResolvedPlanForPackageAndRuntimeBytes()
    {
        var runtimeArtifact = RuntimeArtifact();
        var plan = SuccessfulPlan(runtimeArtifact);
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = plan,
            InstalledPackages = [ManagedInstalledPackage()],
            PreviewResult = new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Preview.",
                false,
                false,
                [],
                [],
                [],
                plan.Items)
            {
                PlanExpectations =
                [
                    new RuntimeRegistryPackagePlanExpectation(
                        "https://registry.example/",
                        false,
                        ResolutionFingerprint),
                ],
                RuntimeArtifacts = plan.RuntimeArtifacts,
            },
        };
        RegistryPackageChangePreflight? prompted = null;
        var service = new RegistryPackageInstallService((preflight, _) =>
        {
            prompted = preflight;
            return Task.FromResult(true);
        });

        var result = await service.UpdatePackageAsync("demo", runtime);

        Assert.True(result.Success);
        Assert.Equal(12L * 1024L * 1024L, prompted!.PackageDownloadBytes);
        Assert.Equal(50L * 1024L * 1024L, prompted.SharedRuntimeDownloadBytes);
        Assert.Collection(
            runtime.UpdateRequests,
            request => Assert.True(request.DryRun),
            request =>
            {
                Assert.False(request.DryRun);
                var expectedPlan = Assert.Single(request.ExpectedPlans);
                Assert.Equal("https://registry.example/", expectedPlan.RegistryOrigin);
                Assert.False(expectedPlan.IncludePrerelease);
                Assert.Equal(ResolutionFingerprint, expectedPlan.ExpectedResolutionFingerprint);
            });
        Assert.Empty(runtime.PlanRequests);
    }

    [Fact]
    public async Task SourceAdoptionConfirmationUsesItsExactPreviewForDisplayConsentAndApplyFence()
    {
        var consent = Consent('a');
        var plan = SuccessfulPlan(RuntimeArtifact()) with
        {
            RequiredCapabilityConsents = [consent],
        };
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = plan,
            AdoptionPreviewResult = AdoptionPreview(plan),
        };
        RegistryPackageChangePreflight? prompted = null;
        var service = new RegistryPackageInstallService((preflight, _) =>
        {
            prompted = preflight;
            return Task.FromResult(true);
        });

        var result = await service.AdoptPackageSourceAsync(
            "demo",
            new Uri("https://registry.example/"),
            runtime);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(prompted);
        Assert.Equal(12L * 1024L * 1024L, prompted.PackageDownloadBytes);
        Assert.Equal(50L * 1024L * 1024L, prompted.SharedRuntimeDownloadBytes);
        Assert.Equal([consent], prompted.Plan.RequiredCapabilityConsents);
        var expectedPlan = Assert.Single(prompted.ExpectedPlans);
        Assert.Equal("https://registry.example/", expectedPlan.RegistryOrigin);
        Assert.Equal(ResolutionFingerprint, expectedPlan.ExpectedResolutionFingerprint);
        Assert.Equal(ResolutionFingerprint, prompted.Plan.ResolutionFingerprint);
        var displayedPlan = Assert.Single(new PackageCapabilityConsentViewModel(prompted).RegistryPlans);
        Assert.Equal("https://registry.example/", displayedPlan.RegistryOrigin);
        Assert.Equal("Stable versions only", displayedPlan.PrereleasePolicyText);
        Assert.Equal(ResolutionFingerprint, displayedPlan.ResolutionFingerprint);
        Assert.Empty(runtime.PlanRequests);
        Assert.Collection(
            runtime.AdoptionRequests,
            request =>
            {
                Assert.True(request.DryRun);
                Assert.False(request.Confirm);
                Assert.Null(request.ExpectedResolutionFingerprint);
            },
            request =>
            {
                Assert.False(request.DryRun);
                Assert.True(request.Confirm);
                Assert.Equal("https://registry.example/", request.RegistryOrigin);
                Assert.Equal("latest", request.Tag);
                Assert.Equal(ResolutionFingerprint, request.ExpectedResolutionFingerprint);
                Assert.Equal([consent], request.CapabilityConsents);
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceAdoptionRejectsInvalidFingerprintOrDifferentPreviewSourceBeforeConfirmation(
        bool invalidFingerprint)
    {
        var plan = SuccessfulPlan(RuntimeArtifact());
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = plan,
            AdoptionPreviewResult = AdoptionPreview(
                plan,
                invalidFingerprint ? "https://registry.example/" : "https://other.example/",
                invalidFingerprint ? new string('A', 64) : ResolutionFingerprint),
        };
        var promptCalls = 0;
        var service = new RegistryPackageInstallService((_, _) =>
        {
            promptCalls++;
            return Task.FromResult(true);
        });

        var result = await service.AdoptPackageSourceAsync(
            "demo",
            new Uri("https://registry.example/"),
            runtime);

        Assert.False(result.Success);
        Assert.Contains("selected Registry source", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, promptCalls);
        Assert.Single(runtime.AdoptionRequests);
    }

    [Fact]
    public async Task SourceAdoptionPreservesTypedPreviewConflictsWithoutConfirmationOrApply()
    {
        var packageConflict = new RuntimeRegistryPackageInstallPlanConflict(
            "demo",
            "1.0.0",
            ">=2.0.0",
            "dependent",
            Message: "The package requirement conflicts with the installed version.");
        var runtimeConflict = new RuntimeRegistryExecutionRuntimeResolutionConflict(
            ExecutionRuntimeResolutionConflictCodes.RidUnavailable,
            "No execution runtime is available for this RID.",
            new RuntimeRegistryExecutionRuntimeRequirement(
                "sunder.runtime.dotnet",
                ">=10.0.0 <11.0.0",
                "sunder.dotnet-worker-module.v1"),
            "osx-arm64",
            "demo",
            "runtime");
        var plan = SuccessfulPlan(RuntimeArtifact());
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = plan,
            AdoptionPreviewResult = new RuntimeRegistryPackageChangeResult(
                false,
                RuntimeRegistryErrorCode.Conflict,
                packageConflict.Message,
                false,
                false,
                [],
                [packageConflict.Message, runtimeConflict.Message],
                [],
                plan.Items)
            {
                Conflicts = [packageConflict],
                RuntimeArtifacts = plan.RuntimeArtifacts,
                RuntimeConflicts = [runtimeConflict],
            },
        };
        var promptCalls = 0;
        var service = new RegistryPackageInstallService((_, _) =>
        {
            promptCalls++;
            return Task.FromResult(true);
        });

        var result = await service.AdoptPackageSourceAsync(
            "demo",
            new Uri("https://registry.example/"),
            runtime);

        Assert.False(result.Success);
        Assert.Equal(packageConflict, Assert.Single(result.Conflicts));
        Assert.Equal(runtimeConflict, Assert.Single(result.RuntimeConflicts));
        Assert.Contains(packageConflict.Message, result.Errors);
        Assert.Contains(runtimeConflict.Message, result.Errors);
        Assert.Equal(0, promptCalls);
        Assert.Single(runtime.AdoptionRequests);
    }

    private static PackageCapabilityConsent Consent(char hashCharacter)
        => new("demo", "1.2.3", new string(hashCharacter, 64), ["docker-environment.v1"]);

    private static RuntimeRegistryResolveInstallPlanResponse SuccessfulPlan(PackageCapabilityConsent consent)
        => new(
            true,
            [],
            [],
            [],
            [],
            [],
            [consent],
            ResolutionFingerprint: ResolutionFingerprint);

    private static RuntimeRegistryResolveInstallPlanResponse SuccessfulPlan(
        RuntimeRegistryResolvedExecutionRuntimeArtifact runtimeArtifact)
        => new(
            true,
            [new RuntimeRegistryPackageInstallPlanItem(
                "demo",
                null,
                "1.2.3",
                new string('f', 64),
                RegistryPackageActionKinds.PackageInstall,
                null,
                [],
                [],
                [new RuntimeRegistryPackageProjectionArtifact(
                    "runtime",
                    "osx-arm64",
                    new string('e', 64),
                    12L * 1024L * 1024L,
                    "/package",
                    new string('d', 64),
                    new string('c', 64),
                    new string('b', 64),
                    1)])],
            [],
            [],
            [],
            [],
            RuntimeArtifacts: [runtimeArtifact],
            ResolutionFingerprint: ResolutionFingerprint);

    private static RuntimeRegistryResolvedExecutionRuntimeArtifact RuntimeArtifact()
        => new(
            new RuntimeRegistryExecutionRuntimeIdentity(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "osx-arm64",
                "sunder.dotnet-worker-module.v1"),
            new string('1', 64),
            new string('2', 64),
            50L * 1024L * 1024L,
            "release-key",
            1,
            new string('3', 64),
            "native-key",
            new RuntimeRegistryExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null),
            1,
            "/runtime");

    private static RuntimeRegistryPackageChangeResult AdoptionPreview(
        RuntimeRegistryResolveInstallPlanResponse plan,
        string registryOrigin = "https://registry.example/",
        string? resolutionFingerprint = null)
        => new RuntimeRegistryPackageChangeResult(
            true,
            RuntimeRegistryErrorCode.None,
            "Previewed source adoption.",
            false,
            false,
            plan.Warnings,
            plan.Errors,
            [],
            plan.Items,
            plan.RequiredCapabilityConsents)
        {
            PlanExpectations =
            [
                new RuntimeRegistryPackagePlanExpectation(
                    registryOrigin,
                    false,
                    resolutionFingerprint ?? ResolutionFingerprint),
            ],
            RuntimeArtifacts = plan.RuntimeArtifacts,
        };

    private static InstalledExecutionRuntimeSummary InstalledRuntime(
        RuntimeRegistryResolvedExecutionRuntimeArtifact artifact)
        => new(
            new RuntimeExecutionRuntimeIdentity(
                artifact.Identity.RuntimeId,
                artifact.Identity.Version,
                artifact.Identity.PackagingRevision,
                artifact.Identity.Rid,
                artifact.Identity.ModuleAbi,
                artifact.ManifestSha256,
                artifact.ArtifactSha256),
            RuntimeExecutionRuntimeStatuses.Ready,
            artifact.ArtifactSize,
            1,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            artifact.SigningKeyId,
            1,
            new RuntimeExecutionRuntimeHealth(
                RuntimeExecutionRuntimeStatuses.Ready,
                DateTimeOffset.UtcNow,
                false,
                null),
            new RuntimeExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null,
                1,
                DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);

    private static InstalledPackageDescriptor ManagedInstalledPackage()
        => new(
            "demo",
            "Demo",
            "1.0.0",
            PackageHostRoles.Runtime,
            null,
            null,
            true,
            [],
            DateTimeOffset.UtcNow,
            null,
            Provenance: new InstalledPackageProvenance(
                InstalledPackageSourceKind.Registry,
                InstalledPackageVersionPolicy.FollowTag,
                "https://registry.example/",
                "demo",
                RequestedTag: "latest"));

    private sealed class FakeRegistryClient : IRegistryClient
    {
        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public void Dispose() { }
    }

    private sealed class FakeRuntimeRegistryPackageClient :
        IRuntimeRegistryPackageClient,
        IRuntimeExecutionRuntimeClient,
        IRuntimePackageStoreClient
    {
        public required RuntimeRegistryResolveInstallPlanResponse PlanResult { get; init; }

        public List<RuntimeRegistryPackageBatchRequest> PlanRequests { get; } = [];

        public List<RuntimeRegistryPackageRequest> InstallRequests { get; } = [];

        public IReadOnlyList<InstalledExecutionRuntimeSummary> ExecutionRuntimes { get; init; } = [];

        public IReadOnlyList<InstalledPackageDescriptor> InstalledPackages { get; init; } = [];

        public RuntimeRegistryPackageChangeResult? PreviewResult { get; init; }

        public RuntimeRegistryPackageChangeResult? InstallResult { get; init; }

        public RuntimeRegistryPackageChangeResult? AdoptionPreviewResult { get; init; }

        public RuntimeRegistryPackageChangeResult? AdoptionResult { get; init; }

        public List<RuntimeRegistryUpdateRequest> UpdateRequests { get; } = [];

        public List<RuntimeRegistrySourceAdoptionRequest> AdoptionRequests { get; } = [];

        public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(
            RuntimeRegistryPackageBatchRequest request,
            CancellationToken cancellationToken = default)
        {
            PlanRequests.Add(request);
            return Task.FromResult(PlanResult);
        }

        public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(
            RuntimeRegistryPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            InstallRequests.Add(request);
            return Task.FromResult(InstallResult ?? new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Installed.",
                true,
                false,
                [],
                [],
                [request.PackageId],
                []));
        }

        public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(
            RuntimeRegistryPackageBatchRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(
            RuntimeRegistryUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateRequests.Add(request);
            if (request.DryRun)
            {
                return Task.FromResult(PreviewResult ?? throw new NotSupportedException());
            }
            return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Updated.",
                true,
                false,
                [],
                [],
                ["demo"],
                PlanResult.Items));
        }

        public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(
            RuntimeRegistrySourceAdoptionRequest request,
            CancellationToken cancellationToken = default)
        {
            AdoptionRequests.Add(request);
            if (request.DryRun)
            {
                return Task.FromResult(AdoptionPreviewResult ?? throw new NotSupportedException());
            }
            return Task.FromResult(AdoptionResult ?? new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Adopted.",
                true,
                false,
                [],
                [],
                [request.PackageId],
                []));
        }

        public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(
            RuntimeRegistryStarRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> GetExecutionRuntimesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(ExecutionRuntimes);

        public Task<RuntimeExecutionRuntimeImpact> GetExecutionRuntimeImpactAsync(
            RuntimeExecutionRuntimeIdentity identity,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(
            RuntimeExecutionRuntimeIdentity identity,
            RuntimeExecutionRuntimeImpact impact,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(InstalledPackages);

        public Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(
            string packageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
            PackageStoreStageRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> CommitPackageStoreStageAsync(
            string stageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RuntimePackageStageStatus> GetPackageStoreStageStatusAsync(
            string stageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DiscardPackageStoreStageAsync(
            string stageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
