using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeStackContractMapper
{
    public static RuntimeStackExportItemDescriptor ToExportItem(
        string ownerPackageId,
        string contributorId,
        StackExportItemDescriptor item)
        => new(
            contributorId,
            ownerPackageId,
            item.ItemId,
            item.DisplayName,
            item.Kind,
            item.DefaultSelected,
            item.Sensitivities?.Select(value => value.ToString()).ToArray() ?? [],
            item.Description,
            item.Details?.Select(detail => new RuntimeStackExportItemDetail(
                detail.Label,
                detail.Value,
                detail.Sensitivity?.ToString(),
                detail.Description,
                detail.ValueWhenExcluded,
                string.IsNullOrWhiteSpace(detail.DetailId) ? BuildDetailId(detail.Label) : detail.DetailId,
                detail.DefaultSelected,
                detail.IsEditable,
                detail.SupportsAskOnImport)).ToArray() ?? []);

    public static RuntimeStackImportActionDescriptor ToAction(string ownerPackageId, string contributorId, StackImportAction action)
        => new(action.ActionId, contributorId, action.DisplayName, action.Kind.ToString(), action.DefaultSelected, action.Description)
        {
            OwnerPackageId = ownerPackageId,
        };

    public static RuntimeStackRequiredInputDescriptor ToRequiredInput(string ownerPackageId, string contributorId, StackRequiredInputDescriptor input)
        => new(input.InputId, contributorId, input.Label, input.Required, input.Description, input.DefaultValue)
        {
            OwnerPackageId = ownerPackageId,
        };

    public static RuntimeStackImportConflictDescriptor ToConflict(string ownerPackageId, string contributorId, StackImportConflict conflict)
        => new(conflict.ConflictId, contributorId, conflict.Message, conflict.Severity.ToString(), conflict.FragmentId)
        {
            OwnerPackageId = ownerPackageId,
        };

    public static RuntimeStackImportedItemDescriptor ToImportedItem(string ownerPackageId, string contributorId, StackImportedItem item)
        => new(item.ItemId, contributorId, item.DisplayName, item.Kind)
        {
            OwnerPackageId = ownerPackageId,
        };

    public static IReadOnlyList<SunderStackPackageRequirement> ToPackageRequirements(IEnumerable<StackPackageRequirement> requirements)
        => requirements
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.PackageId))
            .GroupBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new SunderStackPackageRequirement
                {
                    PackageId = first.PackageId,
                    InstallTag = string.IsNullOrWhiteSpace(first.InstallTag) ? "latest" : first.InstallTag,
                    CreatedWithVersion = first.CreatedWithVersion,
                    MinimumVersion = group.Select(requirement => requirement.MinimumVersion).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    Required = group.Any(requirement => requirement.Required),
                };
            })
            .OrderBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string BuildDetailId(string label)
    {
        var result = new System.Text.StringBuilder(label.Length);
        foreach (var character in label.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character)) result.Append(character);
            else if (result.Length > 0 && result[^1] != '-') result.Append('-');
        }
        var id = result.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(id) ? "detail" : id;
    }
}
