using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Tests.TestSupport;

internal sealed class RegistryInstallationTestClient : RuntimeManagementTestClient, IRuntimeRegistryPackageClient
{
    public bool RuntimeReady { get; set; }
    public RuntimeExecutionRuntimeIdentity SelectedRuntime { get; set; } = Identity;
    public Func<RuntimeRegistryResolveInstallPlanResponse, RuntimeRegistryResolveInstallPlanResponse>? TransformPreview { get; set; }
    public string PackageVersion { get; set; } = "1.0.0";
    public string PackageHash { get; set; } = Hash;
    public IReadOnlyList<string> PackageIds { get; set; } = ["demo"];
    public IReadOnlyList<PackageCapabilityConsent> Consents { get; set; } = [new("demo", "1.0.0", Hash, ["docker.environment.v1"])];
    public List<RuntimeRegistryPackageRequest> Installs { get; } = [];
    public List<RuntimeRegistryPackageBatchRequest> Batches { get; } = [];
    public List<RuntimeRegistryUpdateRequest> Updates { get; } = [];
    public List<RuntimeRegistrySourceAdoptionRequest> Adoptions { get; } = [];
    public Func<Task>? BeforePackageApply { get; set; }
    public int PreviewCount { get; private set; }

    public RegistryInstallationTestClient()
    {
        Inventory = [];
        PreviewPrerequisites = (_, _) => Task.FromResult(RuntimePreview());
        InstallHandler = (_, _) =>
        {
            RuntimeReady = true;
            Inventory = [Installed()];
            return Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Runtime installed", "install", Installed(), false));
        };
    }

    public RuntimePackageRuntimePrerequisitePlan RuntimePreview()
    {
        var result = Prerequisites(RuntimeReady);
        var requirement = result.Requirements[0] with
        {
            InstalledRuntime = RuntimeReady ? SelectedRuntime : null,
            Installation = RuntimeReady ? null : InstallPlan() with
            { Resolution = Resolution() with { Artifact = Resolution().Artifact with { Identity = SelectedRuntime } } },
        };
        return new(result.Success, result.Satisfied, result.ErrorCode, result.Message, result.SourceFingerprint, result.Rid, [requirement], []);
    }
    public RuntimeRegistryResolveInstallPlanResponse PackagePreview()
    {
        PreviewCount++;
        var result = new RuntimeRegistryResolveInstallPlanResponse(true, PackageIds.Select(id => new RuntimeRegistryPackageInstallPlanItem(id, null, PackageVersion, PackageHash,
            RegistryPackageActionKinds.PackageInstall, null, [], [],
            [new("shared", null, PackageHash, 42, "package.zip", PackageHash, PackageHash, PackageHash, 1)])).ToArray(),
            [], [], [], [], Consents, new string((char)('a' + PreviewCount % 6), 64), DateTimeOffset.UtcNow.AddMinutes(5))
        { RuntimePrerequisites = RuntimePreview() };
        return TransformPreview?.Invoke(result) ?? result;
    }
    public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(PackagePreview());
    public async Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
    {
        if (BeforePackageApply is not null) await BeforePackageApply();
        Installs.Add(request);
        return Result();
    }
    public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
    { Batches.Add(request); return Task.FromResult(Result()); }
    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.DryRun) return Task.FromResult(PreviewResult());
        Updates.Add(request);
        return Task.FromResult(Result());
    }
    public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.DryRun) return Task.FromResult(PreviewResult());
        Adoptions.Add(request);
        return Task.FromResult(Result());
    }
    public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private RuntimeRegistryPackageChangeResult PreviewResult()
    {
        var preview = PackagePreview();
        return new(true, RuntimeRegistryErrorCode.None, "Preview", false, false, [], [], [], preview.Items, Consents)
        { RuntimePrerequisites = preview.RuntimePrerequisites, PlanExpectations = [new(Origin, false, preview.ResolutionFingerprint)] };
    }
    private RuntimeRegistryPackageChangeResult Result()
        => new(true, RuntimeRegistryErrorCode.None, "Installed", true, false, [], [], PackageIds, []);
}
