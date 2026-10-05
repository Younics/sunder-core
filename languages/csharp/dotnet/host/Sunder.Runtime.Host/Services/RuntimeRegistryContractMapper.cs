using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Compatibility;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeRegistryContractMapper
{
    public static RegistryClientCompatibilityProfile CompatibilityProfile { get; } = new(
        [SunderPackageFormat.CurrentManifestVersion],
        [SunderPackageFormat.WorkerTargetKind],
        SunderWorkerProtocol.V1Capability,
        SunderSdkCompatibilityProfile.WorkerV1CapabilityClassification
            .Where(static capability => capability.Value)
            .Select(static capability => capability.Key)
            .Order(StringComparer.Ordinal)
            .ToArray(),
        SunderSdkCapabilities.Baseline11V1,
        PackageTargetSelection.GetCurrentRuntimeIdentifier());

    public static RegistryHostRuntimeTrustProfile RuntimeTrustProfile { get; } = new(
        "sunder.runtime-trust.v1",
        1);

    public static IReadOnlyList<RegistryPackageChangeRequest> ToRegistry(
        IReadOnlyList<RuntimeRegistryPackageChangeRequest> requests)
        => requests.Select(request => new RegistryPackageChangeRequest(
            request.PackageId,
            request.Version,
            ToRegistry(request.DesiredTargets),
            request.Tag,
            request.VersionRange,
            request.Required)).ToArray();

    public static IReadOnlyList<RegistryPackageTargetRequest> ToRegistry(
        IReadOnlyList<RuntimeRegistryPackageTargetRequest>? targets)
    {
        var selected = targets is { Count: > 0 }
            ? targets
            : DefaultTargets();
        return selected
            .Select(target => new RegistryPackageTargetRequest(target.Role, target.Rid))
            .Distinct()
            .OrderBy(static target => TargetOrder(target.Role))
            .ThenBy(static target => target.Rid, StringComparer.Ordinal)
            .ToArray();
    }

    public static RuntimeRegistryResolveInstallPlanResponse ToRuntime(
        RegistryResolveInstallPlanResponse response)
        => new(
            response.Success,
            ToRuntime(response.Items),
            response.Warnings,
            response.Errors,
            response.Conflicts.Select(conflict => new RuntimeRegistryPackageInstallPlanConflict(
                conflict.PackageId,
                conflict.CurrentVersion,
                conflict.RequestedVersionRange,
                conflict.RequiredByPackageId,
                conflict.ErrorCode,
                conflict.Message)).ToArray(),
            response.TrustedArtifactOrigins,
            RuntimeArtifacts: response.RuntimeArtifacts.Select(ToRuntime).ToArray(),
            RuntimeConflicts: response.RuntimeConflicts.Select(ToRuntime).ToArray(),
            ResolutionFingerprint: response.ResolutionFingerprint,
            RuntimePolicyRevision: response.RuntimePolicyRevision,
            ExpiresAtUtc: response.ExpiresAtUtc)
        {
            CompatibilityConflicts = response.CompatibilityConflicts.Select(conflict => new RuntimeRegistryPackageCompatibilityConflict(
                conflict.PackageId, conflict.ErrorCode, conflict.Requirements.Select(requirement => new RuntimeRegistryPackageCompatibilityRequirement(
                    requirement.PackageId, requirement.VersionRange, requirement.IncludePrerelease, requirement.Presence,
                    requirement.Contracts.Select(contract => new RuntimeRpcContractIdentity(contract.ContractId, contract.ContractVersion, contract.DescriptorSha256, contract.AbiSha256)).ToArray(),
                    requirement.RequiredByPackageId)).ToArray())).ToArray(),
        };

    public static IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> ToRuntime(
        IReadOnlyList<RegistryPackageInstallPlanItem> items)
        => items.Select(item => new RuntimeRegistryPackageInstallPlanItem(
            item.PackageId,
            item.CurrentVersion,
            item.Version,
            item.PackageContentIdentity,
            item.ActionKind,
            item.DeprecatedMessage,
            item.DependsOn.Select(dependency => new RuntimeRegistryPackageDependency(
                dependency.PackageId,
                dependency.VersionRange)).ToArray(),
            item.Targets.Select(target => new RuntimeRegistryPackageTarget(
                target.Role,
                target.Rid,
                target.Kind,
                target.EntryPoint,
                target.TargetFramework,
                target.SdkVersion,
                target.RequiredHostCapabilities,
                target.Views.Select(view => new RuntimeRegistryPackageWebView(
                    view.ViewId,
                    view.DisplayName,
                    view.Route,
                    view.Icon,
                    view.DefaultPlacement,
                    view.ShowInHotbar)).ToArray(),
                target.ExecutionRuntime is null ? null : ToRuntime(target.ExecutionRuntime))).ToArray(),
            item.Artifacts.Select(artifact => new RuntimeRegistryPackageProjectionArtifact(
                artifact.Kind,
                artifact.Rid,
                artifact.Sha256,
                artifact.Size,
                artifact.DownloadUrl,
                artifact.SourceArchiveSha256,
                artifact.ManifestSha256,
                artifact.ProjectionContentIdentity,
                artifact.ProjectionFormatVersion)).ToArray(),
            item.RuntimeBindings.Select(ToRuntime).ToArray(),
            item.RuntimeOnlyRebind is null
                ? null
                : new RuntimeRegistryRuntimeOnlyRebindDetails(
                    item.RuntimeOnlyRebind.PackageVersion,
                    item.RuntimeOnlyRebind.PackageContentIdentity,
                    item.RuntimeOnlyRebind.PreviousBindings.Select(ToRuntime).ToArray(),
                    item.RuntimeOnlyRebind.NewBindings.Select(ToRuntime).ToArray()))).ToArray();

    private static RuntimeRegistryExecutionRuntimeRequirement ToRuntime(
        RegistryExecutionRuntimeRequirement requirement)
        => new(requirement.RuntimeId, requirement.VersionRange, requirement.ModuleAbi);

    private static RuntimeRegistryExecutionRuntimeIdentity ToRuntime(
        RegistryExecutionRuntimeIdentity identity)
        => new(identity.RuntimeId, identity.Version, identity.PackagingRevision, identity.Rid, identity.ModuleAbi);

    private static RuntimeRegistryExecutionRuntimeLifecycle ToRuntime(
        RegistryExecutionRuntimeLifecycle lifecycle)
        => new(
            lifecycle.State,
            lifecycle.StagedAtUtc,
            lifecycle.SupportedFromUtc,
            lifecycle.DeprecatedAtUtc,
            lifecycle.InstallBlockedAtUtc,
            lifecycle.ActivationBlockedAtUtc,
            lifecycle.RevokedAtUtc,
            lifecycle.Message);

    private static RuntimeRegistryResolvedExecutionRuntimeArtifact ToRuntime(
        RegistryResolvedExecutionRuntimeArtifact artifact)
        => new(
            ToRuntime(artifact.Identity),
            artifact.ManifestSha256,
            artifact.ArtifactSha256,
            artifact.ArtifactSize,
            artifact.SigningKeyId,
            artifact.MinimumHostTrustGeneration,
            artifact.NativeAttestationSha256,
            artifact.NativeAttestationKeyId,
            ToRuntime(artifact.Lifecycle),
            artifact.ObservedRuntimePolicyRevision,
            artifact.DownloadUrl);

    private static RuntimeRegistryPackageTargetRuntimeBinding ToRuntime(
        RegistryPackageTargetRuntimeBinding binding)
        => new(
            binding.Role,
            binding.Rid,
            ToRuntime(binding.Requirement),
            ToRuntime(binding.Runtime));

    public static RuntimeRegistryExecutionRuntimeResolutionConflict ToRuntime(
        RegistryExecutionRuntimeResolutionConflict conflict)
        => new(
            conflict.Code,
            conflict.Message,
            ToRuntime(conflict.Requirement),
            conflict.Rid,
            conflict.PackageId,
            conflict.TargetRole,
            conflict.CurrentRuntime is null ? null : ToRuntime(conflict.CurrentRuntime),
            conflict.RequiredHostTrustGeneration);

    private static IReadOnlyList<RuntimeRegistryPackageTargetRequest> DefaultTargets()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        return
        [
            new RuntimeRegistryPackageTargetRequest("runtime", rid),
            new RuntimeRegistryPackageTargetRequest("app", rid),
        ];
    }

    private static int TargetOrder(string role)
        => role switch
        {
            "runtime" => 0,
            "app" => 1,
            _ => 2,
        };
}
