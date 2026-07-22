using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal static class CreateStackExportSelectionProjector
{
    public static CreateStackExportSelection Project(IReadOnlyList<CreateStackPackageGroupViewModel> groups)
    {
        var selectedGroups = groups.Where(group => group.IsSelected).ToArray();
        return new CreateStackExportSelection(
            selectedGroups.Select(group => group.PackageId).ToArray(),
            selectedGroups
                .SelectMany(group => group.Items)
                .Where(item => item.IsSelected && item.HasSelectedDetails)
                .Select(item => new RuntimeStackExportSelection(
                    item.OwnerPackageId,
                    item.ContributorId,
                    item.ItemId,
                    item.Details
                        .Select(detail => new RuntimeStackExportDetailSelection(
                            detail.DetailId,
                            detail.IsSelected,
                            detail.ValueOverride,
                            detail.SensitivityOverride))
                        .ToArray()))
                .ToArray());
    }
}

internal sealed record CreateStackExportSelection(
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<RuntimeStackExportSelection> Items);
