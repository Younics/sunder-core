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
            request.Tag,
            request.VersionRange,
            request.Required)).ToArray();

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
                conflict.Message)).ToArray());

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
            new RuntimeRegistryPackageArtifact(
                item.Artifact.Sha256,
                item.Artifact.Size,
                item.Artifact.DownloadUrl),
            item.Compatibility is null
                ? null
                : new RuntimeRegistryPackageCompatibility(
                    item.Compatibility.SdkApiVersion,
                    item.Compatibility.SdkPackageVersion,
                    item.Compatibility.RequiredCapabilities,
                    item.Compatibility.TargetFramework,
                    item.Compatibility.ManifestFormatVersion,
                    item.Compatibility.ArchiveFormatVersion))).ToArray();
}
