using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed record RegistryPackageInstallExecutionResult(
    bool Success,
    string Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems)
{
    public bool AppShellApplied { get; init; }

    public RuntimePackageStamp? CommittedStamp { get; init; }

    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get; init; } = [];

    public static RegistryPackageInstallExecutionResult Empty(string message)
        => new(true, message, true, false, [], [], [], []);

    public static RegistryPackageInstallExecutionResult Failed(
        string message,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem>? planItems = null)
        => new(false, message, false, false, warnings ?? [], errors ?? [message], [], planItems ?? []);
}

public sealed record RegistryPackageInstallProgress(string StatusText, double? ProgressPercent);

public sealed class RegistryPackageInstallService
{
    private const string AnySemanticVersionRange = ">=0.0.0-0";
    private readonly Func<IReadOnlyList<PackageCapabilityConsent>, CancellationToken, Task<bool>>? _consentPrompt;

    public RegistryPackageInstallService(
        Func<IReadOnlyList<PackageCapabilityConsent>, CancellationToken, Task<bool>>? consentPrompt = null)
    {
        _consentPrompt = consentPrompt;
    }

    public async Task<RegistryPackageInstallExecutionResult> InstallPackageAsync(
        string packageId,
        string? version,
        string? tag,
        bool allowDowngrade,
        bool reinstall,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PackageCapabilityConsent>? consents = null;
        if (_consentPrompt is not null)
        {
            var batch = ToBatchRequest(
                registryClient.RegistryUrl,
                packageId,
                version,
                tag,
                allowDowngrade,
                reinstall);
            var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(batch, cancellationToken);
            if (!plan.Success)
            {
                return PlanFailed(plan);
            }
            consents = await RequestConsentAsync(plan.RequiredCapabilityConsents, cancellationToken);
            if (consents is null)
            {
                return ConsentDeclined(plan.RequiredCapabilityConsents, plan.Items);
            }
        }
        progress?.Invoke(new("Runtime is resolving and applying the package transaction...", 10));
        var result = await runtimeApiClient.InstallRegistryPackageAsync(
            new RuntimeRegistryPackageRequest(
                registryClient.RegistryUrl.AbsoluteUri,
                packageId,
                version,
                string.IsNullOrWhiteSpace(version) ? tag ?? "latest" : null,
                AllowDowngrade: allowDowngrade,
                Reinstall: reinstall,
                CapabilityConsents: consents),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveInstallPlanForPackagesAsync(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Runtime is resolving the package graph...", 10));
        return runtimeApiClient.ResolveRegistryPackagePlanAsync(ToBatchRequest(packages, registryClient.RegistryUrl), cancellationToken);
    }

    public async Task<RegistryPackageInstallExecutionResult> InstallPackagesAsync(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = ToBatchRequest(packages, registryClient.RegistryUrl);
        if (_consentPrompt is not null)
        {
            var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(request, cancellationToken);
            if (!plan.Success)
            {
                return PlanFailed(plan);
            }
            var consents = await RequestConsentAsync(plan.RequiredCapabilityConsents, cancellationToken);
            if (consents is null)
            {
                return ConsentDeclined(plan.RequiredCapabilityConsents, plan.Items);
            }
            request = request with { CapabilityConsents = consents };
        }
        progress?.Invoke(new("Runtime is applying the package transaction...", 10));
        var result = await runtimeApiClient.ApplyRegistryPackagePlanAsync(request, cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public async Task<RegistryPackageInstallExecutionResult> UpdateAllAsync(
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PackageCapabilityConsent>? consents = null;
        if (_consentPrompt is not null)
        {
            var preview = await runtimeApiClient.UpdateRegistryPackagesAsync(
                new RuntimeRegistryUpdateRequest(DryRun: true),
                cancellationToken);
            if (!preview.Success)
            {
                return ToAppResult(preview);
            }
            consents = await RequestConsentAsync(preview.RequiredCapabilityConsents, cancellationToken);
            if (consents is null)
            {
                return ConsentDeclined(preview.RequiredCapabilityConsents, preview.PlanItems);
            }
        }
        progress?.Invoke(new("Runtime is resolving and applying updates...", 10));
        var result = await runtimeApiClient.UpdateRegistryPackagesAsync(
            new RuntimeRegistryUpdateRequest(CapabilityConsents: consents),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public async Task<RegistryPackageInstallExecutionResult> UpdatePackageAsync(
        string packageId,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PackageCapabilityConsent>? consents = null;
        if (_consentPrompt is not null)
        {
            var preview = await runtimeApiClient.UpdateRegistryPackagesAsync(
                new RuntimeRegistryUpdateRequest(PackageId: packageId, DryRun: true),
                cancellationToken);
            if (!preview.Success)
            {
                return ToAppResult(preview);
            }
            consents = await RequestConsentAsync(preview.RequiredCapabilityConsents, cancellationToken);
            if (consents is null)
            {
                return ConsentDeclined(preview.RequiredCapabilityConsents, preview.PlanItems);
            }
        }
        progress?.Invoke(new($"Runtime is resolving and applying the recorded update policy for {packageId}...", 10));
        var result = await runtimeApiClient.UpdateRegistryPackagesAsync(
            new RuntimeRegistryUpdateRequest(PackageId: packageId, CapabilityConsents: consents),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public async Task<RegistryPackageInstallExecutionResult> AdoptPackageSourceAsync(
        string packageId,
        Uri registryUrl,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PackageCapabilityConsent>? consents = null;
        if (_consentPrompt is not null)
        {
            var preview = await runtimeApiClient.AdoptRegistryPackageSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    packageId,
                    registryUrl.AbsoluteUri,
                    Tag: "latest",
                    DryRun: true),
                cancellationToken);
            if (!preview.Success)
            {
                return ToAppResult(preview);
            }
            consents = await RequestConsentAsync(preview.RequiredCapabilityConsents, cancellationToken);
            if (consents is null)
            {
                return ConsentDeclined(preview.RequiredCapabilityConsents, preview.PlanItems);
            }
        }
        progress?.Invoke(new($"Runtime is adopting the Registry source and updating {packageId}...", 10));
        var result = await runtimeApiClient.AdoptRegistryPackageSourceAsync(
            new RuntimeRegistrySourceAdoptionRequest(
                packageId,
                registryUrl.AbsoluteUri,
                Tag: "latest",
                Confirm: true,
                CapabilityConsents: consents),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    private static RuntimeRegistryPackageBatchRequest ToBatchRequest(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        Uri registryUrl)
        => new RuntimeRegistryPackageBatchRequest(
            registryUrl.AbsoluteUri,
            packages
                .Where(package => !string.IsNullOrWhiteSpace(package.PackageId))
                .Select(package => new RuntimeRegistryPackageChangeRequest(
                    package.PackageId!,
                    null,
                    [],
                    string.IsNullOrWhiteSpace(package.InstallTag) ? "latest" : package.InstallTag,
                    string.IsNullOrWhiteSpace(package.MinimumVersion)
                        ? AnySemanticVersionRange
                        : $">={package.MinimumVersion}",
                    package.Required != false))
                .ToArray());

    private static RuntimeRegistryPackageBatchRequest ToBatchRequest(
        Uri registryUrl,
        string packageId,
        string? version,
        string? tag,
        bool allowDowngrade,
        bool reinstall)
        => new(
            registryUrl.AbsoluteUri,
            [new RuntimeRegistryPackageChangeRequest(
                packageId,
                version,
                [],
                version is null ? tag ?? "latest" : null)],
            AllowDowngrade: allowDowngrade,
            Reinstall: reinstall);

    private async Task<IReadOnlyList<PackageCapabilityConsent>?> RequestConsentAsync(
        IReadOnlyList<PackageCapabilityConsent> required,
        CancellationToken cancellationToken)
    {
        if (required.Count == 0)
        {
            return [];
        }
        return await _consentPrompt!(required, cancellationToken).ConfigureAwait(false)
            ? required
            : null;
    }

    private static RegistryPackageInstallExecutionResult ConsentDeclined(
        IReadOnlyList<PackageCapabilityConsent> required,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> planItems)
        => RegistryPackageInstallExecutionResult.Failed(
            "Sensitive worker capability consent was not approved; no package changes were applied.",
            planItems: planItems) with
        {
            RequiredCapabilityConsents = required,
        };

    private static RegistryPackageInstallExecutionResult PlanFailed(
        RuntimeRegistryResolveInstallPlanResponse plan)
        => RegistryPackageInstallExecutionResult.Failed(
            plan.Errors.FirstOrDefault() ?? "Registry package plan resolution failed.",
            plan.Errors,
            plan.Warnings,
            plan.Items) with
        {
            RequiredCapabilityConsents = plan.RequiredCapabilityConsents,
        };

    private static RegistryPackageInstallExecutionResult ToAppResult(RuntimeRegistryPackageChangeResult result)
        => new RegistryPackageInstallExecutionResult(
            result.Success,
            result.Message,
            result.RuntimeSessionApplied,
            result.RequiresAppRestart,
            result.Warnings,
            result.Errors,
            result.ImpactedPackageIds,
            result.PlanItems)
        {
            CommittedStamp = result.CommittedStamp,
            RequiredCapabilityConsents = result.RequiredCapabilityConsents,
        };
}
