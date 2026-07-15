using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeStackContributorCatalog
{
    public static Dictionary<string, StackExporterRegistration> GetExporters(
        RuntimeSessionOwner sessions,
        PackageSessionLease lease,
        ICollection<string> errors)
    {
        var result = new Dictionary<string, StackExporterRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in sessions.State
                     .GetExtensionContributions(lease, SunderStackExtensionPoints.StackExporters)
                     .GroupBy(value => Key(value.PackageId, value.Contribution.ContributorId), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (string.IsNullOrWhiteSpace(first.PackageId) || string.IsNullOrWhiteSpace(first.Contribution.ContributorId))
            {
                errors.Add("An active Stack exporter has an invalid package or contributor id.");
            }
            else if (group.Skip(1).Any())
            {
                errors.Add($"Package '{first.PackageId}' registered duplicate Stack exporter id '{first.Contribution.ContributorId}'.");
            }
            else
            {
                result.Add(group.Key, new StackExporterRegistration(first.PackageId, first.Contribution));
            }
        }
        return result;
    }

    public static Dictionary<string, StackImporterRegistration> GetImporters(
        RuntimeSessionOwner sessions,
        PackageSessionLease lease,
        ICollection<string> errors)
    {
        var result = new Dictionary<string, StackImporterRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in sessions.State
                     .GetExtensionContributions(lease, SunderStackExtensionPoints.StackImporters)
                     .GroupBy(value => Key(value.PackageId, value.Contribution.ContributorId), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (string.IsNullOrWhiteSpace(first.PackageId) || string.IsNullOrWhiteSpace(first.Contribution.ContributorId))
            {
                errors.Add("An active Stack importer has an invalid package or contributor id.");
            }
            else if (group.Skip(1).Any())
            {
                errors.Add($"Package '{first.PackageId}' registered duplicate Stack importer id '{first.Contribution.ContributorId}'.");
            }
            else
            {
                result.Add(group.Key, new StackImporterRegistration(first.PackageId, first.Contribution));
            }
        }
        return result;
    }

    public static void ValidateScopedValues(
        IReadOnlyDictionary<string, string> values,
        string kind,
        IReadOnlyDictionary<string, StackImporterRegistration> importers,
        string label,
        ICollection<string> errors)
    {
        foreach (var key in values.Keys)
        {
            if (!RuntimeStackScopedKey.TryParse(kind, key, out var owner, out var contributor, out _)
                || !importers.ContainsKey(Key(owner, contributor)))
            {
                errors.Add($"Stack import contains an invalid or unavailable host-scoped {label} id.");
            }
        }
    }

    public static bool ValidatePreviewIds(
        StackImportPreview preview,
        StackImporterRegistration registration,
        ICollection<string> errors)
    {
        var valid = true;
        if (preview.Actions.Any(action => action is null)
            || preview.RequiredInputs.Any(input => input is null))
        {
            errors.Add($"Stack importer '{registration.Importer.ContributorId}' returned a null action or required input.");
            return false;
        }
        foreach (var duplicate in preview.Actions
                     .GroupBy(action => action.ActionId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Skip(1).Any()))
        {
            errors.Add($"Stack importer '{registration.Importer.ContributorId}' returned duplicate or empty action id '{duplicate.Key}'.");
            valid = false;
        }
        foreach (var duplicate in preview.RequiredInputs
                     .GroupBy(input => input.InputId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Skip(1).Any()))
        {
            errors.Add($"Stack importer '{registration.Importer.ContributorId}' returned duplicate or empty required input id '{duplicate.Key}'.");
            valid = false;
        }
        return valid;
    }

    public static bool ValidateExportItemIds(
        IReadOnlyList<StackExportItemDescriptor> items,
        StackExporterRegistration registration,
        ICollection<string> errors)
    {
        if (items.Any(item => item is null))
        {
            errors.Add($"Stack exporter '{registration.Exporter.ContributorId}' returned a null item.");
            return false;
        }
        var duplicates = items
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Skip(1).Any())
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicate in duplicates)
        {
            errors.Add($"Stack exporter '{registration.Exporter.ContributorId}' returned duplicate or empty item id '{duplicate}'.");
        }
        return duplicates.Length == 0;
    }

    public static string Key(string? packageId, string? contributorId)
        => string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(contributorId)
            ? string.Empty
            : packageId.Trim() + "\u001f" + contributorId.Trim();
}

internal sealed record StackExporterRegistration(string PackageId, IPackageStackExporter Exporter);
