using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Rpc.Authoring;

// Source intent is compiler evidence, not another package selection or manifest field.
internal sealed record SunderRpcRoleUse(string Role, string ContractId, string ContractVersion,
    string DescriptorSha256, string AbiSha256, string ServiceId, string ServiceType, SunderRpcOwnedUsePolicy Policy);

internal static partial class SunderRpcAuthoringDocuments
{
    internal static string RoleUseIdentity(SunderRpcRoleUse use)
        => string.Join('\0', use.Role, use.ContractId, use.ContractVersion, use.DescriptorSha256, use.AbiSha256,
            use.ServiceId, use.ServiceType, use.Policy.Required.ToString(), string.Join(';', use.Policy.Actions));

    private static JsonArray RoleUsesJson(IEnumerable<SunderRpcRoleUse> uses)
        => ReceiptArray(uses.OrderBy(use => use.Role, StringComparer.Ordinal).ThenBy(use => use.ContractId, StringComparer.Ordinal)
            .ThenBy(use => use.ServiceId, StringComparer.Ordinal).Select(use => new JsonObject
        {
            ["role"] = use.Role, ["contractId"] = use.ContractId, ["contractVersion"] = use.ContractVersion,
            ["descriptorSha256"] = use.DescriptorSha256, ["abiSha256"] = use.AbiSha256,
            ["serviceId"] = use.ServiceId, ["serviceType"] = use.ServiceType,
            ["required"] = use.Policy.Required, ["actions"] = new JsonArray(use.Policy.Actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray()),
        }));

    private static IReadOnlyList<SunderRpcRoleUse> ReadRoleUses(JsonElement root)
    {
        if (!root.TryGetProperty("roleUses", out _)) return [];
        const string label = "source role use";
        var array = RequireProperty(root, "roleUses", JsonValueKind.Array, label);
        if (array.GetArrayLength() is < 1 or > 128) throw new InvalidDataException("rpc-authoring.use-invalid: Source role uses require 1–128 entries.");
        var result = new List<SunderRpcRoleUse>();
        string? previous = null;
        foreach (var value in array.EnumerateArray())
        {
            string[] fields = ["role", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "serviceId", "serviceType", "required", "actions"];
            RequireObject(value, label, fields, fields, []);
            RequireString(value, "role", "runtime", label);
            var id = RequirePackageId(value, "contractId", label);
            var service = RequireString(value, "serviceId", label);
            var key = id + "\0" + service;
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0) throw new InvalidDataException("rpc-authoring.use-invalid: Source role targets must be unique and ordered.");
            previous = key;
            var actions = RequireProperty(value, "actions", JsonValueKind.Array, label).EnumerateArray()
                .Select(action => action.ValueKind == JsonValueKind.String ? action.GetString()! : throw new InvalidDataException("Source role actions must be strings.")).ToArray();
            string[] allowed = ["discover", "invoke", "subscribe"];
            if (actions.Length == 0 || !actions.SequenceEqual(allowed.Where(actions.Contains), StringComparer.Ordinal))
                throw new InvalidDataException("rpc-authoring.use-invalid: Source role actions must be a nonempty canonical action subset.");
            result.Add(new("runtime", id, RequireSemanticVersion(value, "contractVersion", label), RequireSha256(value, "descriptorSha256", label),
                RequireSha256(value, "abiSha256", label), service, RequireString(value, "serviceType", label),
                new(RequireBoolean(value, "required", label), actions)));
        }
        foreach (var group in result.GroupBy(use => use.ContractId, StringComparer.Ordinal))
            if (group.Any(use => !SameUsePolicy(use.Policy, group.First().Policy))) throw new InvalidDataException("rpc-authoring.use-invalid: Source intents for one contract must agree.");
        return result;
    }

    private static Dictionary<string, SunderRpcLockedArtifactContract> MaterializedImports(SunderRpcContractLock contractLock,
        SunderRpcCSharpImportPrepassResult imports)
    {
        IReadOnlyDictionary<string, SunderRpcContractCacheRequirement> required;
        try
        {
            required = SunderRpcContractCache.GetMaterializationRequirements(contractLock,
                imports.Contracts.ToDictionary(contract => contract.ContractKey, contract => contract.ModelProjection, StringComparer.Ordinal));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Imported materialization differs from the validated lock/projection closure.", exception);
        }
        return contractLock.PackageArtifacts.Values.SelectMany(artifact => artifact.Contracts.Values)
            .Concat(contractLock.PlatformImports.Values.Select(SunderRpcPlatformCatalogCache.AsArtifactContract))
            .Where(contract => required.ContainsKey(contract.DescriptorSha256)).GroupBy(contract => contract.ContractId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }
}
