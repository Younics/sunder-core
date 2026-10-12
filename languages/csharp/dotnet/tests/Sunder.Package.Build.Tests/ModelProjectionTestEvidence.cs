using System.Text.Json.Nodes;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

// Descriptor-only conformance fixtures deliberately have no named source values.
// Real source tests use the compiler's ModelProjection instead of this fixture.
internal static class ModelProjectionTestEvidence
{
    internal static bool MatchesAuthoringSchema(JsonElement schema, JsonElement instance, string projectionSchemaPath)
    {
        using var projection = JsonDocument.Parse(File.ReadAllBytes(projectionSchemaPath));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(projectionSchemaPath)!, "authoring-receipt.schema.json")));
        return JsonSchemaTestValidator.IsValid(schema, instance, schema, new Dictionary<string, JsonElement>
        {
            ["rpc-model-projection.schema.json#/$defs/modelProjection"] = projection.RootElement.GetProperty("$defs").GetProperty("modelProjection"),
            ["authoring-receipt.schema.json#/$defs/roleUse"] = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["$ref"] = "#/$defs/roleUse", ["$defs"] = JsonNode.Parse(receipt.RootElement.GetProperty("$defs").GetRawText()),
            }),
        });
    }

    internal static SunderRpcModelProjectionDocument EmptySourceProjection(SunderRpcLockedPackageArtifact artifact)
        => new(1, artifact.PackageId, artifact.SelectedPackageVersion, artifact.Contracts.Values.Select(contract =>
            new SunderRpcModelProjectionContract(contract.ContractId, contract.ResolvedContractVersion,
                contract.DescriptorSha256, contract.AbiSha256, SunderRpcModelProjection.Empty)).ToArray());

    internal static JsonElement CurrentImportReceiptExpectation(JsonElement historical, SunderRpcContractLock contractLock)
    {
        // Preserve frozen source/descriptor/ABI vectors. The current receipt adds
        // mandatory projection evidence and the coordinated C# namespace cut.
        var expected = JsonNode.Parse(historical.GetRawText())!;
        foreach (var contract in expected["contracts"]!.AsArray()) contract!["modelProjection"] = SunderRpcModelProjection.Empty.ToJson();
        foreach (var artifact in expected["packageArtifacts"]!.AsArray())
        {
            var selected = contractLock.PackageArtifacts[artifact!["packageId"]!.GetValue<string>()];
            var projection = EmptySourceProjection(selected);
            artifact["modelProjectionBytes"] = projection.ModelProjectionBytes;
            artifact["modelProjectionSha256"] = projection.ModelProjectionSha256;
        }
        foreach (var binding in expected["generatedBindings"]!.AsArray())
        {
            var id = binding!["contractKey"]!.GetValue<string>();
            var origin = contractLock.PackageArtifacts.Values.SelectMany(artifact => artifact.Contracts.Values).First(contract => contract.ContractId == id);
            binding["logicalName"] = Sunder.Package.Build.Editor.GeneratedPackageNames.Transport(origin.AuthorityId, id);
        }
        foreach (var facade in expected["generatedPackageFacades"]!.AsArray())
        {
            var local = facade!["localName"]!.GetValue<string>();
            facade["logicalName"] = Sunder.Package.Build.Editor.GeneratedPackageNames.Internal(facade["packageId"]!.GetValue<string>())
                + "." + char.ToUpperInvariant(local[0]) + local[1..] + "PackageApi";
        }
        return JsonSerializer.SerializeToElement(expected);
    }

    internal static SunderRpcModelProjectionDocument PinEmptySourceProjection(string packageId, JsonNode artifact)
    {
        var document = new SunderRpcModelProjectionDocument(1, packageId, artifact["selectedPackageVersion"]!.GetValue<string>(),
            artifact["contracts"]!.AsObject().Select(pair => new SunderRpcModelProjectionContract(pair.Key,
                pair.Value!["resolvedContractVersion"]!.GetValue<string>(), pair.Value["descriptorSha256"]!.GetValue<string>(),
                pair.Value["abiSha256"]!.GetValue<string>(), SunderRpcModelProjection.Empty)).ToArray());
        artifact["modelProjectionBytes"] = document.ModelProjectionBytes;
        artifact["modelProjectionSha256"] = document.ModelProjectionSha256;
        return document;
    }

    internal static void Seed(string cacheRoot, SunderRpcLockedPackageArtifact artifact)
    {
        var document = EmptySourceProjection(artifact);
        Assert.Equal(document.ModelProjectionBytes, artifact.ModelProjectionBytes);
        Assert.Equal(document.ModelProjectionSha256, artifact.ModelProjectionSha256);
        var path = SunderRpcContractCache.GetModelProjectionPath(cacheRoot, artifact.ModelProjectionSha256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, document.ToCanonicalUtf8());
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, artifact.ModelProjectionSha256 + SunderRpcContractCache.ReceiptSuffix),
            SunderRpcContractCache.ModelProjectionReceiptBytes(artifact.ModelProjectionSha256, artifact.ModelProjectionBytes));
    }
}
