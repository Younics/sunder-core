using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeStackContributorCatalog
{
    public static async Task<Dictionary<string, StackContributorRegistration>> GetExportersAsync(
        ISunderRpcClient client,
        ICollection<string> errors,
        CancellationToken cancellationToken)
        => await GetContributorsAsync(
            client,
            static metadata => metadata.SupportsExport,
            "exporter",
            errors,
            cancellationToken).ConfigureAwait(false);

    public static async Task<Dictionary<string, StackContributorRegistration>> GetImportersAsync(
        ISunderRpcClient client,
        ICollection<string> errors,
        CancellationToken cancellationToken)
        => await GetContributorsAsync(
            client,
            static metadata => metadata.SupportsImport,
            "importer",
            errors,
            cancellationToken).ConfigureAwait(false);

    private static async Task<Dictionary<string, StackContributorRegistration>> GetContributorsAsync(
        ISunderRpcClient client,
        Func<StackContributorMetadata, bool> include,
        string kind,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, StackContributorRegistration>(StringComparer.OrdinalIgnoreCase);
        var catalog = await client.DiscoverAsync(
            SunderStackContributorRpc.ContractId,
            cancellationToken).ConfigureAwait(false);
        foreach (var provider in catalog.Providers)
        {
            var rpc = SunderStackContributorRpc.CreateClient(client, provider.Endpoint);
            try
            {
                var metadata = await rpc.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
                if (!include(metadata)) continue;
                if (string.IsNullOrWhiteSpace(provider.PackageId)
                    || string.IsNullOrWhiteSpace(metadata.ContributorId))
                {
                    errors.Add($"An active Stack {kind} has an invalid package or contributor id.");
                    continue;
                }

                var key = Key(provider.PackageId, metadata.ContributorId);
                if (!result.TryAdd(key, new StackContributorRegistration(provider, metadata, rpc)))
                {
                    errors.Add(
                        $"Package '{provider.PackageId}' registered duplicate Stack {kind} id '{metadata.ContributorId}'.");
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Stack {kind} provider '{provider.ProviderId}' metadata failed.");
            }
        }
        return result;
    }

    public static void ValidateScopedValues(
        IReadOnlyDictionary<string, string> values,
        string kind,
        IReadOnlyDictionary<string, StackContributorRegistration> importers,
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
        string contributorId,
        ICollection<string> errors)
    {
        var valid = true;
        if (preview.Actions.Any(action => action is null)
            || preview.RequiredInputs.Any(input => input is null))
        {
            errors.Add($"Stack importer '{contributorId}' returned a null action or required input.");
            return false;
        }
        foreach (var duplicate in preview.Actions
                     .GroupBy(action => action.ActionId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Skip(1).Any()))
        {
            errors.Add($"Stack importer '{contributorId}' returned duplicate or empty action id '{duplicate.Key}'.");
            valid = false;
        }
        foreach (var duplicate in preview.RequiredInputs
                     .GroupBy(input => input.InputId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Skip(1).Any()))
        {
            errors.Add($"Stack importer '{contributorId}' returned duplicate or empty required input id '{duplicate.Key}'.");
            valid = false;
        }
        return valid;
    }

    public static bool ValidateExportItemIds(
        IReadOnlyList<StackExportItemDescriptor> items,
        string contributorId,
        ICollection<string> errors)
    {
        if (items.Any(item => item is null))
        {
            errors.Add($"Stack exporter '{contributorId}' returned a null item.");
            return false;
        }
        var duplicates = items
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Skip(1).Any())
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicate in duplicates)
        {
            errors.Add($"Stack exporter '{contributorId}' returned duplicate or empty item id '{duplicate}'.");
        }
        return duplicates.Length == 0;
    }

    public static string Key(string? packageId, string? contributorId)
        => string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(contributorId)
            ? string.Empty
            : packageId.Trim() + "\u001f" + contributorId.Trim();
}

internal sealed record StackContributorRegistration(
    SunderRpcProviderSnapshot Provider,
    StackContributorMetadata Metadata,
    StackContributorRpcClient Client)
{
    public string PackageId => Provider.PackageId;
    public string ContributorId => Metadata.ContributorId;
}
