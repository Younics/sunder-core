using System.Text.Json.Nodes;

namespace Sunder.Package.Format.Tests;

internal static class ModelProjectionFixture
{
    // The archive fixtures author transport-only descriptors (Empty/compiler envelopes).
    // Construct their explicit empty source projection once at the owning archive builder.
    // Use raw manifest claims here so deliberately invalid manifests still reach validation.
    internal static byte[] EmptyDocument(SunderPackageManifest manifest)
    {
        var contracts = new JsonArray();
        foreach (var bundle in (manifest.ContractBundles ?? []).Where(b => b?.Exposure == "public")
                     .OrderBy(b => b!.ContractId, StringComparer.Ordinal).ThenBy(b => b!.Version, StringComparer.Ordinal))
            contracts.Add(new JsonObject
            {
                ["contractId"] = bundle!.ContractId, ["contractVersion"] = bundle.Version,
                ["descriptorSha256"] = bundle.DescriptorSha256, ["abiSha256"] = bundle.AbiSha256,
                ["modelProjection"] = new JsonObject { ["models"] = new JsonObject(), ["serviceReferences"] = new JsonObject() },
            });
        return SunderRpcModelProjectionJson.Write(new JsonObject
        {
            ["schemaVersion"] = 1, ["packageId"] = manifest.Id, ["packageVersion"] = manifest.Version, ["contracts"] = contracts,
        });
    }
}
