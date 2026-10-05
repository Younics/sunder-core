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

    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; init; } = [];

    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get; init; } = [];

    public IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict> RuntimeConflicts { get; init; } = [];

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
    private readonly Func<RegistryPackageChangePreflight, CancellationToken, Task<bool>>? _confirmationPrompt;

    public RegistryPackageInstallService(
        Func<RegistryPackageChangePreflight, CancellationToken, Task<bool>>? confirmationPrompt = null)
    {
        _confirmationPrompt = confirmationPrompt;
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
        string? expectedResolutionFingerprint = null;
        if (_confirmationPrompt is not null)
        {
            var batch = ToBatchRequest(
                registryClient.RegistryUrl,
                packageId,
                version,
                tag,
                allowDowngrade,
                reinstall);
            var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(batch, cancellationToken);
            if (HasPlanFailures(plan))
            {
                return PlanFailed(plan);
            }
            if (!IsResolutionFingerprint(plan.ResolutionFingerprint))
            {
                return InvalidPreviewFingerprint(plan);
            }
            expectedResolutionFingerprint = plan.ResolutionFingerprint;
            var expectedPlans = CreateExpectedPlans(batch, plan);
            var preflight = await RegistryPackageChangePreflight.CreateAsync(
                plan,
                runtimeApiClient,
                cancellationToken,
                expectedPlans);
            consents = await RequestConfirmationAsync(preflight, cancellationToken);
            if (consents is null)
            {
                return ConfirmationDeclined(plan.RequiredCapabilityConsents, plan.Items);
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
                CapabilityConsents: consents,
                ExpectedResolutionFingerprint: expectedResolutionFingerprint),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public async Task<RuntimeRegistryResolveInstallPlanResponse> ResolveInstallPlanForPackagesAsync(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Runtime is resolving the package graph...", 10));
        var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(
            ToBatchRequest(packages, registryClient.RegistryUrl),
            cancellationToken);
        return plan.Success && !IsResolutionFingerprint(plan.ResolutionFingerprint)
            ? InvalidPreviewFingerprintPlan(plan)
            : plan;
    }

    public async Task<RegistryPackageInstallExecutionResult> InstallPackagesAsync(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? expectedResolutionFingerprint = null)
    {
        var request = ToBatchRequest(packages, registryClient.RegistryUrl);
        if (_confirmationPrompt is not null)
        {
            var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(request, cancellationToken);
            if (HasPlanFailures(plan))
            {
                return PlanFailed(plan);
            }
            if (!IsResolutionFingerprint(plan.ResolutionFingerprint))
            {
                return InvalidPreviewFingerprint(plan);
            }
            expectedResolutionFingerprint = plan.ResolutionFingerprint;
            var preflight = await RegistryPackageChangePreflight.CreateAsync(
                plan,
                runtimeApiClient,
                cancellationToken,
                CreateExpectedPlans(request, plan));
            var consents = await RequestConfirmationAsync(preflight, cancellationToken);
            if (consents is null)
            {
                return ConfirmationDeclined(plan.RequiredCapabilityConsents, plan.Items);
            }
            request = request with
            {
                CapabilityConsents = consents,
                ExpectedResolutionFingerprint = expectedResolutionFingerprint,
            };
        }
        else if (expectedResolutionFingerprint is not null)
        {
            if (!IsResolutionFingerprint(expectedResolutionFingerprint))
            {
                return InvalidPreviewFingerprint(new RuntimeRegistryResolveInstallPlanResponse(
                    true,
                    [],
                    [],
                    [],
                    [],
                    [],
                    ResolutionFingerprint: expectedResolutionFingerprint));
            }
            request = request with { ExpectedResolutionFingerprint = expectedResolutionFingerprint };
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
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? expectedPlans = null;
        if (_confirmationPrompt is not null)
        {
            var preview = await runtimeApiClient.UpdateRegistryPackagesAsync(
                new RuntimeRegistryUpdateRequest(DryRun: true),
                cancellationToken);
            if (!preview.Success)
            {
                return ToAppResult(preview);
            }
            if (preview.PlanItems.Count == 0)
            {
                return ToAppResult(preview);
            }
            var resolved = ResolveUpdatePlan(preview);
            var plan = resolved.Plan;
            if (HasPlanFailures(plan))
            {
                return PlanFailed(plan);
            }
            expectedPlans = resolved.ExpectedPlans;
            var preflight = await RegistryPackageChangePreflight.CreateAsync(
                plan,
                runtimeApiClient,
                cancellationToken,
                expectedPlans);
            consents = await RequestConfirmationAsync(preflight, cancellationToken);
            if (consents is null)
            {
                return ConfirmationDeclined(plan.RequiredCapabilityConsents, plan.Items);
            }
        }
        progress?.Invoke(new("Runtime is resolving and applying updates...", 10));
        var result = await runtimeApiClient.UpdateRegistryPackagesAsync(
            new RuntimeRegistryUpdateRequest(
                CapabilityConsents: consents,
                ExpectedPlans: expectedPlans),
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
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? expectedPlans = null;
        if (_confirmationPrompt is not null)
        {
            var preview = await runtimeApiClient.UpdateRegistryPackagesAsync(
                new RuntimeRegistryUpdateRequest(PackageId: packageId, DryRun: true),
                cancellationToken);
            if (!preview.Success)
            {
                return ToAppResult(preview);
            }
            if (preview.PlanItems.Count == 0)
            {
                return ToAppResult(preview);
            }
            var resolved = ResolveUpdatePlan(preview);
            var plan = resolved.Plan;
            if (HasPlanFailures(plan))
            {
                return PlanFailed(plan);
            }
            expectedPlans = resolved.ExpectedPlans;
            var preflight = await RegistryPackageChangePreflight.CreateAsync(
                plan,
                runtimeApiClient,
                cancellationToken,
                expectedPlans);
            consents = await RequestConfirmationAsync(preflight, cancellationToken);
            if (consents is null)
            {
                return ConfirmationDeclined(plan.RequiredCapabilityConsents, plan.Items);
            }
        }
        progress?.Invoke(new($"Runtime is resolving and applying the recorded update policy for {packageId}...", 10));
        var result = await runtimeApiClient.UpdateRegistryPackagesAsync(
            new RuntimeRegistryUpdateRequest(
                PackageId: packageId,
                CapabilityConsents: consents,
                ExpectedPlans: expectedPlans),
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
        var previewRequest = new RuntimeRegistrySourceAdoptionRequest(
            packageId,
            registryUrl.AbsoluteUri,
            Tag: "latest",
            DryRun: true);
        var preview = await runtimeApiClient.AdoptRegistryPackageSourceAsync(
            previewRequest,
            cancellationToken);
        if (!preview.Success)
        {
            return ToAppResult(preview);
        }
        var resolved = ResolveAdoptionPlan(preview, registryUrl, includePrerelease: false);
        if (HasPlanFailures(resolved.Plan))
        {
            return PlanFailed(resolved.Plan);
        }

        IReadOnlyList<PackageCapabilityConsent>? consents = null;
        if (_confirmationPrompt is not null)
        {
            var preflight = await RegistryPackageChangePreflight.CreateAsync(
                resolved.Plan,
                runtimeApiClient,
                cancellationToken,
                resolved.ExpectedPlans);
            consents = await RequestConfirmationAsync(preflight, cancellationToken);
            if (consents is null)
            {
                return ConfirmationDeclined(resolved.Plan.RequiredCapabilityConsents, resolved.Plan.Items);
            }
        }
        progress?.Invoke(new($"Runtime is adopting the Registry source and updating {packageId}...", 10));
        var result = await runtimeApiClient.AdoptRegistryPackageSourceAsync(
            previewRequest with
            {
                Confirm = true,
                DryRun = false,
                CapabilityConsents = consents,
                ExpectedResolutionFingerprint = resolved.ExpectedPlans[0].ExpectedResolutionFingerprint,
            },
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

    private static ResolvedUpdatePlan ResolveUpdatePlan(RuntimeRegistryPackageChangeResult preview)
    {
        var plan = PlanFromPreview(preview);
        if (preview.PlanExpectations.Count == 0
            || preview.PlanExpectations.Any(static expectation =>
                !IsResolutionFingerprint(expectation.ExpectedResolutionFingerprint)))
        {
            return new ResolvedUpdatePlan(InvalidPreviewFingerprintPlan(plan), []);
        }
        return new ResolvedUpdatePlan(plan, preview.PlanExpectations);
    }

    private static ResolvedUpdatePlan ResolveAdoptionPlan(
        RuntimeRegistryPackageChangeResult preview,
        Uri registryUrl,
        bool includePrerelease)
    {
        var plan = PlanFromPreview(preview);
        if (preview.PlanExpectations.Count != 1)
        {
            return new ResolvedUpdatePlan(InvalidAdoptionPreviewPlan(plan), []);
        }

        var expectation = preview.PlanExpectations[0];
        if (!IsResolutionFingerprint(expectation.ExpectedResolutionFingerprint)
            || expectation.IncludePrerelease != includePrerelease
            || !SameRegistryOrigin(registryUrl, expectation.RegistryOrigin))
        {
            return new ResolvedUpdatePlan(InvalidAdoptionPreviewPlan(plan), []);
        }
        return new ResolvedUpdatePlan(
            plan with { ResolutionFingerprint = expectation.ExpectedResolutionFingerprint },
            [expectation]);
    }

    private static RuntimeRegistryResolveInstallPlanResponse PlanFromPreview(
        RuntimeRegistryPackageChangeResult preview)
        => new(
            preview.Success,
            preview.PlanItems,
            preview.Warnings,
            preview.Errors,
            preview.Conflicts,
            [],
            preview.RequiredCapabilityConsents,
            RuntimeArtifacts: preview.RuntimeArtifacts,
            RuntimeConflicts: preview.RuntimeConflicts);

    private async Task<IReadOnlyList<PackageCapabilityConsent>?> RequestConfirmationAsync(
        RegistryPackageChangePreflight preflight,
        CancellationToken cancellationToken)
    {
        return await _confirmationPrompt!(preflight, cancellationToken).ConfigureAwait(false)
            ? preflight.Plan.RequiredCapabilityConsents
            : null;
    }

    private static RegistryPackageInstallExecutionResult ConfirmationDeclined(
        IReadOnlyList<PackageCapabilityConsent> required,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> planItems)
        => RegistryPackageInstallExecutionResult.Failed(
            "Package changes were not approved; no package changes were applied.",
            planItems: planItems) with
        {
            RequiredCapabilityConsents = required,
        };

    private static RegistryPackageInstallExecutionResult PlanFailed(
        RuntimeRegistryResolveInstallPlanResponse plan)
    {
        var errors = plan.Errors
            .Concat(plan.Conflicts.Select(static conflict => string.IsNullOrWhiteSpace(conflict.Message)
                ? $"Package '{conflict.PackageId}' conflicts with the requested change."
                : conflict.Message))
            .Concat(plan.RuntimeConflicts.Select(static conflict => conflict.Message))
            .DefaultIfEmpty("Registry package plan resolution failed.")
            .ToArray();
        return RegistryPackageInstallExecutionResult.Failed(
            errors[0],
            errors,
            plan.Warnings,
            plan.Items) with
        {
            Conflicts = plan.Conflicts,
            RequiredCapabilityConsents = plan.RequiredCapabilityConsents,
            RuntimeConflicts = plan.RuntimeConflicts,
        };
    }

    private static RegistryPackageInstallExecutionResult InvalidPreviewFingerprint(
        RuntimeRegistryResolveInstallPlanResponse plan)
        => PlanFailed(InvalidPreviewFingerprintPlan(plan));

    private static RuntimeRegistryResolveInstallPlanResponse InvalidPreviewFingerprintPlan(
        RuntimeRegistryResolveInstallPlanResponse plan)
        => new(
            false,
            plan.Items,
            plan.Warnings,
            ["Registry package preview did not return a valid resolution fingerprint. Refresh the preview and retry."],
            plan.Conflicts,
            plan.TrustedArtifactOrigins,
            plan.RequiredCapabilityConsents,
            plan.RuntimeArtifacts,
            plan.RuntimeConflicts,
            plan.ResolutionFingerprint,
            plan.RuntimePolicyRevision,
            plan.ExpiresAtUtc);

    private static RuntimeRegistryResolveInstallPlanResponse InvalidAdoptionPreviewPlan(
        RuntimeRegistryResolveInstallPlanResponse plan)
        => new(
            false,
            plan.Items,
            plan.Warnings,
            plan.Errors.Concat([
                "Registry source adoption preview did not return one valid plan fingerprint for the selected Registry source. Refresh the preview and retry.",
            ]).ToArray(),
            plan.Conflicts,
            plan.TrustedArtifactOrigins,
            plan.RequiredCapabilityConsents,
            plan.RuntimeArtifacts,
            plan.RuntimeConflicts,
            plan.ResolutionFingerprint,
            plan.RuntimePolicyRevision,
            plan.ExpiresAtUtc);

    private static IReadOnlyList<RuntimeRegistryPackagePlanExpectation> CreateExpectedPlans(
        RuntimeRegistryPackageBatchRequest request,
        RuntimeRegistryResolveInstallPlanResponse plan)
        =>
        [
            new RuntimeRegistryPackagePlanExpectation(
                request.RegistryOrigin,
                request.IncludePrerelease,
                plan.ResolutionFingerprint),
        ];

    private static bool IsResolutionFingerprint(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasPlanFailures(RuntimeRegistryResolveInstallPlanResponse plan)
        => !plan.Success
           || plan.Errors.Count != 0
           || plan.Conflicts.Count != 0
           || plan.RuntimeConflicts.Count != 0;

    private static bool SameRegistryOrigin(Uri expected, string actual)
        => Uri.TryCreate(actual, UriKind.Absolute, out var actualUri)
           && Uri.Compare(
               expected,
               actualUri,
               UriComponents.SchemeAndServer | UriComponents.Path,
               UriFormat.SafeUnescaped,
               StringComparison.Ordinal) == 0;

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
            Conflicts = result.Conflicts,
            RequiredCapabilityConsents = result.RequiredCapabilityConsents,
            RuntimeConflicts = result.RuntimeConflicts,
        };

    private sealed record ResolvedUpdatePlan(
        RuntimeRegistryResolveInstallPlanResponse Plan,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation> ExpectedPlans);
}
