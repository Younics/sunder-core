using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeRegistryContractMapper
{
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
            response.TrustedArtifactOrigins);

    public static IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> ToRuntime(
        IReadOnlyList<RegistryPackageInstallPlanItem> items)
        => items.Select(item => new RuntimeRegistryPackageInstallPlanItem(
            item.PackageId,
            item.CurrentVersion,
            item.Version,
            item.IsUpdate,
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
                    view.ShowInHotbar)).ToArray())).ToArray(),
            item.Artifacts.Select(artifact => new RuntimeRegistryPackageProjectionArtifact(
                artifact.Kind,
                artifact.Rid,
                artifact.Sha256,
                artifact.Size,
                artifact.DownloadUrl,
                artifact.SourceArchiveSha256,
                artifact.ManifestSha256,
                artifact.ProjectionContentIdentity,
                artifact.ProjectionFormatVersion)).ToArray())).ToArray();

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
