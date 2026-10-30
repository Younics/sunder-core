using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    private static IReadOnlyList<SunderRpcConsumerRequirements> ReadConsumerRequirements(JsonElement root,
        SunderPackageManifest manifest, IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts,
        IReadOnlyDictionary<string, SunderRpcModelProjection> projections)
    {
        if (!root.TryGetProperty("consumerRequirements", out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 128)
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Consumer requirements must be a nonempty bounded array when present.");
        var imported = (manifest.ContractBundles ?? []).Where(bundle => bundle?.Source == "imported" && bundle.AuthorityKind == "package")
            .ToDictionary(bundle => bundle!.ContractId!, StringComparer.Ordinal);
        var result = new List<SunderRpcConsumerRequirements>();
        string? previous = null;
        foreach (var value in array.EnumerateArray())
        {
            var id = RequirePackageId(value, "contractId", "consumer requirements");
            if (previous is not null && string.CompareOrdinal(previous, id) >= 0 || !imported.ContainsKey(id)
                || !contracts.TryGetValue(id, out var descriptor))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Consumer requirements must uniquely cover imported package contracts in ordinal order.");
            previous = id;
            var requirement = SunderRpcConsumerRequirements.Parse(value, descriptor);
            if (projections[id].ServiceReferences.Keys.Any(name => !requirement.ExactOutputDefinitions.Contains(name, StringComparer.Ordinal)))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Service-reference output schemas must retain exact protocol requirements.");
            result.Add(requirement);
        }
        if (!result.Select(value => value.Source.ContractId).SequenceEqual(imported.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Consumer requirements omit an imported package contract.");
        return Array.AsReadOnly(result.ToArray());
    }
}
