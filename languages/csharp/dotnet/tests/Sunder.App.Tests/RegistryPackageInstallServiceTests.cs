using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryPackageInstallServiceTests
{
    [Fact]
    public async Task ApprovedConsentIsForwardedExactlyFromPreflightToInstall()
    {
        var consent = Consent('a');
        var runtime = new FakeRuntimeRegistryPackageClient
        {
            PlanResult = SuccessfulPlan(consent),
        };
        IReadOnlyList<PackageCapabilityConsent>? prompted = null;
        var service = new RegistryPackageInstallService((required, _) =>
        {
            prompted = required;
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
        Assert.Equal([consent], prompted);
        Assert.Single(runtime.PlanRequests);
        Assert.Equal([consent], Assert.Single(runtime.InstallRequests).CapabilityConsents);
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

    private static PackageCapabilityConsent Consent(char hashCharacter)
        => new("demo", "1.2.3", new string(hashCharacter, 64), ["docker-environment.v1"]);

    private static RuntimeRegistryResolveInstallPlanResponse SuccessfulPlan(PackageCapabilityConsent consent)
        => new(true, [], [], [], [], [], [consent]);

    private sealed class FakeRegistryClient : IRegistryClient
    {
        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public void Dispose() { }
    }

    private sealed class FakeRuntimeRegistryPackageClient : IRuntimeRegistryPackageClient
    {
        public required RuntimeRegistryResolveInstallPlanResponse PlanResult { get; init; }

        public List<RuntimeRegistryPackageBatchRequest> PlanRequests { get; } = [];

        public List<RuntimeRegistryPackageRequest> InstallRequests { get; } = [];

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
            return Task.FromResult(new RuntimeRegistryPackageChangeResult(
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
            => throw new NotSupportedException();

        public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(
            RuntimeRegistrySourceAdoptionRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(
            RuntimeRegistryStarRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
