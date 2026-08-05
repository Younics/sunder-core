using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeStackContractMapper
{
    public static StackOwnedFragment OwnExportFragment(
        string ownerPackageId,
        string contributorId,
        SunderRpcProviderSnapshot provider,
        StackRpcFragmentExport fragment)
        => new(ownerPackageId, contributorId, provider, fragment);

    public static StackFragmentImport OwnImportFragment(
        string ownerPackageId,
        string contributorId,
        StackFragmentImport fragment)
        => fragment with
        {
            OwnerPackageId = ownerPackageId,
            ContributorId = contributorId,
        };

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
        => new(
            RuntimeStackScopedKey.Create(RuntimeStackScopedKey.ActionKind, ownerPackageId, contributorId, action.ActionId),
            ownerPackageId,
            contributorId,
            action.ActionId,
            action.DisplayName,
            action.Kind.ToString(),
            action.DefaultSelected,
            action.Description);

    public static RuntimeStackRequiredInputDescriptor ToRequiredInput(string ownerPackageId, string contributorId, StackRequiredInputDescriptor input)
        => new(
            RuntimeStackScopedKey.Create(RuntimeStackScopedKey.InputKind, ownerPackageId, contributorId, input.InputId),
            ownerPackageId,
            contributorId,
            input.InputId,
            input.Label,
            input.Sensitivity == StackValueSensitivity.Secret
                ? RuntimeStackInputSensitivity.Secret
                : RuntimeStackInputSensitivity.Public,
            input.Required,
            input.Description,
            input.DefaultValue);

    public static RuntimeStackImportConflictDescriptor ToConflict(string ownerPackageId, string contributorId, StackImportConflict conflict)
        => new(
            RuntimeStackScopedKey.Create("conflict", ownerPackageId, contributorId, conflict.ConflictId),
            ownerPackageId,
            contributorId,
            conflict.Message,
            conflict.Severity.ToString(),
            conflict.FragmentId);

    public static RuntimeStackImportedItemDescriptor ToImportedItem(string ownerPackageId, string contributorId, StackImportedItem item)
        => new(item.ItemId, ownerPackageId, contributorId, item.DisplayName, item.Kind);

    public static IReadOnlyDictionary<string, string> ToContributorValues(
        IReadOnlyDictionary<string, string> values,
        string kind,
        string ownerPackageId,
        string contributorId)
    {
        var scoped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (RuntimeStackScopedKey.TryParse(kind, pair.Key, out var owner, out var contributor, out var localId)
                && string.Equals(owner, ownerPackageId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(contributor, contributorId, StringComparison.OrdinalIgnoreCase))
            {
                scoped[localId] = pair.Value;
            }
        }
        return scoped;
    }

    public static IReadOnlyDictionary<string, string> ToHostRemaps(
        IReadOnlyDictionary<string, string> values,
        string ownerPackageId,
        string contributorId)
        => values.ToDictionary(
            pair => RuntimeStackScopedKey.Create(RuntimeStackScopedKey.RemapKind, ownerPackageId, contributorId, pair.Key),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SunderStackPackageRequirement> ToPackageRequirements(IEnumerable<StackPackageRequirement> requirements)
        => requirements
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.PackageId))
            .GroupBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var installTags = group
                    .Select(requirement => string.IsNullOrWhiteSpace(requirement.InstallTag) ? "latest" : requirement.InstallTag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (installTags.Length != 1)
                {
                    throw new InvalidDataException(
                        $"Stack package '{first.PackageId}' declares conflicting install tags: {string.Join(", ", installTags)}.");
                }

                string? minimumVersion = null;
                SemanticVersion? strongestMinimum = null;
                foreach (var candidate in group.Select(requirement => requirement.MinimumVersion)
                             .Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    if (!SemanticVersion.TryParse(candidate, out var parsed))
                    {
                        throw new InvalidDataException(
                            $"Stack package '{first.PackageId}' declares invalid minimum version '{candidate}'.");
                    }
                    if (strongestMinimum is null || parsed.ComparePrecedenceTo(strongestMinimum.Value) > 0)
                    {
                        strongestMinimum = parsed;
                        minimumVersion = candidate;
                    }
                }

                return new SunderStackPackageRequirement
                {
                    PackageId = first.PackageId,
                    InstallTag = installTags[0],
                    CreatedWithVersion = first.CreatedWithVersion,
                    MinimumVersion = minimumVersion,
                    Required = group.Any(requirement => requirement.Required),
                };
            })
            .OrderBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static SunderStackFragmentPreview ToPreview(
        string sourceItemId,
        StackExportItemDescriptor item,
        StackExportItemSelection selection)
    {
        var selected = (selection.Details ?? []).ToDictionary(value => value.DetailId, StringComparer.OrdinalIgnoreCase);
        var details = new List<SunderStackFragmentDisplayDetail>();
        foreach (var detail in item.Details ?? [])
        {
            var id = string.IsNullOrWhiteSpace(detail.DetailId) ? BuildDetailId(detail.Label) : detail.DetailId;
            if (selection.Details is not null && (!selected.TryGetValue(id!, out var choice) || !choice.IsSelected)) continue;
            selected.TryGetValue(id!, out var selectedDetail);
            var sensitivity = selectedDetail?.SensitivityOverride ?? detail.Sensitivity;
            var asksOnImport = sensitivity == StackValueSensitivity.Secret;
            var value = asksOnImport ? "Importer will provide this value." : selectedDetail?.ValueOverride ?? detail.Value;
            if (!string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(value)) details.Add(new SunderStackFragmentDisplayDetail { Label = detail.Label, Value = value, Behavior = asksOnImport ? "Ask on import" : "Include value" });
        }
        return new SunderStackFragmentPreview { SourceItemId = sourceItemId, Kind = item.Kind, DisplayDetails = details };
    }

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
