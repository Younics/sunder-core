using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal static class StackDetailTreeProjector
{
    public static IReadOnlyList<LocalStackDetailPackageViewModel> ProjectLocal(
        IReadOnlyList<LocalStackDetailPackage> details,
        IReadOnlyDictionary<string, Uri?> packageIcons,
        Func<string, string, Uri> createPackageAssetUri)
        => details.Select(detail =>
        {
            packageIcons.TryGetValue(detail.PackageId, out var iconUri);
            iconUri ??= string.IsNullOrWhiteSpace(detail.IconAssetPath)
                ? null
                : createPackageAssetUri(detail.PackageId, detail.IconAssetPath!);
            return new LocalStackDetailPackageViewModel(detail, iconUri);
        }).ToArray();
}

internal static class StackInstallPlanProjector
{
    public static StackInstallPlanPresentation Project(RuntimeRegistryResolveInstallPlanResponse plan)
        => new(
            plan.Items.Select(item => new StackPackageInstallPlanItemViewModel(item)).ToArray(),
            plan.Warnings.ToArray(),
            plan.Errors.Concat(plan.Conflicts.Select(conflict => conflict.Message)).ToArray(),
            plan.Success);
}

internal sealed record StackInstallPlanPresentation(
    IReadOnlyList<StackPackageInstallPlanItemViewModel> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    bool Success);
