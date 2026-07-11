using Sunder.App.Services;
using Sunder.Registry.Contracts;
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
    public static StackInstallPlanPresentation Project(RegistryResolveInstallPlanResponse plan)
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

internal static class StackImportPreviewProjector
{
    public static StackImportPreviewPresentation Project(
        RuntimeStackImportPreviewResponse preview,
        IReadOnlyDictionary<string, string> existingInputValues,
        Action inputChanged)
    {
        var warnings = preview.Warnings.ToList();
        var errors = preview.Errors.ToList();
        foreach (var conflict in preview.Conflicts)
        {
            var target = string.Equals(conflict.Severity, "Error", StringComparison.OrdinalIgnoreCase)
                ? errors
                : warnings;
            target.Add($"{(ReferenceEquals(target, errors) ? "Error" : "Warning")}: {conflict.Message}");
        }

        return new StackImportPreviewPresentation(
            preview.Actions.Select(action => new StackImportActionViewModel(action)).ToArray(),
            preview.RequiredInputs.Select(input => new StackRequiredInputValueViewModel(
                input,
                existingInputValues.TryGetValue(input.InputId, out var value) ? value : null,
                inputChanged)).ToArray(),
            warnings,
            errors,
            preview.Success);
    }
}

internal sealed record StackImportPreviewPresentation(
    IReadOnlyList<StackImportActionViewModel> Actions,
    IReadOnlyList<StackRequiredInputValueViewModel> RequiredInputs,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    bool Success);
