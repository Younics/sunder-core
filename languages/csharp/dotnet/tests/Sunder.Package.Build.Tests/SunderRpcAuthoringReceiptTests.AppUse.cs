using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcAuthoringReceiptTests
{
    [Theory]
    [InlineData("Public", "", null, "")]
    [InlineData("Package", "", true, "discover,invoke")]
    [InlineData("Public", "[SunderRpcAppUse]", false, "discover,invoke")]
    [InlineData("Package", "[SunderRpcAppUse]", false, "discover,invoke")]
    [InlineData("Public", "[SunderRpcAppUse(Required = true, Actions = new[] { \"invoke\", \"discover\" })]", true, "discover,invoke")]
    [InlineData("Public", "[SunderRpcAppUse(Actions = new[] { \"discover\" })]", false, "discover")]
    public void AppPolicy_RoundTripsSourceExportAndReceiptsWithoutChangingWireIdentity(string exposure, string attribute, bool? required, string actions)
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var imports = Prepare(contractLock);
        var source = OwnedSource.Replace("Exposure.Public", "Exposure." + exposure, StringComparison.Ordinal);
        var original = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", source));
        var compilation = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", source.Replace("[SunderRpcProvider", attribute + "\n[SunderRpcProvider", StringComparison.Ordinal)));
        var contract = Assert.Single(compilation.Contracts);
        Assert.Equal(original.Contracts[0].Descriptor.GetCanonicalUtf8(), contract.Descriptor.GetCanonicalUtf8());
        Assert.Equal(original.Contracts[0].Descriptor.AbiSha256, contract.Descriptor.AbiSha256);
        var workerBytes = Write(contractLock, compilation, imports);
        var worker = AssertReceipt(workerBytes);
        Assert.Empty(worker.GetProperty("uses").EnumerateArray());
        Assert.Equal(attribute.Length != 0, worker.GetProperty("contracts")[0].TryGetProperty("appUse", out _));
        Assert.Empty(SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(workerBytes, contractLock.Sha256, PackageId, PackageVersion).Manifest.UsesContracts!);

        var exportPath = _temporary.GetPath("package-export.json");
        var exportBytes = SunderRpcAuthoringDocuments.WritePackageExport("1.0.0", "Owner", "Debug", "net10.0", Fingerprint, contractLock.Sha256, PackageId, compilation.Contracts);
        File.WriteAllBytes(exportPath, exportBytes);
        using var exportJson = JsonDocument.Parse(exportBytes);
        using var exportSchema = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/package-project-export.schema.json")));
        Assert.True(JsonSchemaTestValidator.IsValid(exportSchema.RootElement, exportJson.RootElement));
        var export = SunderRpcAuthoringDocuments.ReadPackageProjectExport(exportPath, "1.0.0", "Owner", "Debug", "net10.0", PackageId);
        Assert.True(SunderRpcAuthoringDocuments.SameUsePolicy(contract.AppUse, Assert.Single(export.Contracts).AppUse));
        _ = SunderRpcAuthoringDocuments.ValidatePackageProjectExport(exportPath, "1.0.0", "Owner", "Debug", "net10.0", PackageId, contractLock.Sha256, compilation.Contracts);
        if (attribute.Length != 0)
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidatePackageProjectExport(exportPath, "1.0.0", "Owner", "Debug", "net10.0", PackageId, contractLock.Sha256, original.Contracts));

        var appBytes = SunderRpcAuthoringDocuments.WriteProjectImportReceipt("1.0.0", Fingerprint, contractLock.Sha256, PackageId, PackageVersion, export.Contracts);
        using var app = JsonDocument.Parse(appBytes);
        using var receiptSchema = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/authoring-receipt.schema.json")));
        Assert.True(JsonSchemaTestValidator.IsValid(receiptSchema.RootElement, app.RootElement));
        Assert.Equal("app", app.RootElement.GetProperty("role").GetString());
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(appBytes, contractLock.Sha256, PackageId, PackageVersion);
        Assert.Empty(projection.Manifest.Provides!);
        if (required is null) Assert.Empty(projection.Manifest.UsesContracts!);
        else
        {
            var use = Assert.Single(projection.Manifest.UsesContracts!);
            Assert.Equal(required, use!.Required);
            Assert.Equal(actions.Split(','), use.Actions);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("required")]
    [InlineData("broadened-actions")]
    [InlineData("duplicate")]
    [InlineData("runtime-role")]
    [InlineData("unknown-field")]
    [InlineData("empty-policy")]
    [InlineData("unknown-action")]
    [InlineData("noncanonical-actions")]
    [InlineData("null-policy")]
    public void AppReceipt_RejectsMissingBroadenedOrMalformedGrants(string mutation)
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var compilation = Compile(Prepare(contractLock), new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource.Replace("[SunderRpcProvider",
            "[SunderRpcAppUse(Actions = new[] { \"discover\" })]\n[SunderRpcProvider", StringComparison.Ordinal)));
        var contract = Assert.Single(compilation.Contracts);
        var exports = new[] { new SunderRpcPackageProjectExportContract(contract.ContractKey, contract.Exposure, contract.GeneratedLogicalName, contract.Descriptor) { AppUse = contract.AppUse } };
        var bytes = SunderRpcAuthoringDocuments.WriteProjectImportReceipt("1.0.0", Fingerprint, contractLock.Sha256, PackageId, PackageVersion, exports);
        _ = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
        var app = JsonNode.Parse(bytes)!.AsObject();
        var uses = app["uses"]!.AsArray();
        var policy = app["contracts"]![0]!["appUse"]!;
        switch (mutation)
        {
            case "missing": uses.Clear(); break;
            case "required": uses[0]!["required"] = true; break;
            case "broadened-actions": uses[0]!["actions"] = new JsonArray("discover", "invoke"); break;
            case "duplicate": uses.Add(uses[0]!.DeepClone()); break;
            case "runtime-role": uses[0]!["role"] = "runtime"; break;
            case "unknown-field": policy["extra"] = true; break;
            case "empty-policy": policy["actions"] = new JsonArray(); break;
            case "unknown-action": policy["actions"] = new JsonArray("write"); break;
            case "noncanonical-actions": policy["actions"] = new JsonArray("invoke", "discover"); break;
            case "null-policy": app["contracts"]![0]!["appUse"] = null; break;
        }
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            Encoding.UTF8.GetBytes(app.ToJsonString(JsonOptions) + "\n"), contractLock.Sha256, PackageId, PackageVersion));
    }
}
