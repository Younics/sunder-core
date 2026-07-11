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
                    item.ContributorId,
                    item.ItemId,
                    item.Details
                        .Where(detail => detail.IsSelected)
                        .Select(detail => new RuntimeStackExportDetailSelection(
                            detail.DetailId,
                            IsSelected: true,
                            detail.ValueOverride,
                            detail.SensitivityOverride))
                        .ToArray())
                {
                    OwnerPackageId = item.OwnerPackageId,
                })
                .ToArray());
    }
}

internal sealed record CreateStackExportSelection(
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<RuntimeStackExportSelection> Items);
