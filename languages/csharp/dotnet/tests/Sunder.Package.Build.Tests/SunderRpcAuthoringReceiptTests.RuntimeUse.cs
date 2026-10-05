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
    [InlineData("Public", false, false, "", "discover,invoke")]
    [InlineData("Public", true, false, "new[] { \"invoke\" }", "invoke")]
    [InlineData("Public", true, true, "new[] { \"invoke\", \"discover\" }", "discover,invoke")]
    [InlineData("Package", false, true, "new[] { \"discover\" }", "discover")]
    [InlineData("Package", true, false, "", "discover,invoke")]
    public void RuntimePolicy_GrantsExactlyTheOwnedConsumerUseAndDoesNotLeakToApp(string exposure, bool provider, bool required, string actions, string expectedActions)
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var imports = Prepare(contractLock);
        var source = OwnedSource.Replace("Exposure.Public", "Exposure." + exposure, StringComparison.Ordinal);
        if (!provider) source = source
            .Replace("[SunderRpcProvider(ProviderId = \"example.consumer.owned.provider\")]", "", StringComparison.Ordinal)
            .Replace("public sealed partial class OwnedService", "public interface OwnedService", StringComparison.Ordinal)
            .Replace("public ValueTask<Response> ReadAsync() => throw new System.NotImplementedException();", "ValueTask<Response> ReadAsync();", StringComparison.Ordinal);
        source = source.Replace("[SunderRpcService", "[SunderRpcAppUse(Required = true, Actions = new[] { \"discover\" })]\n[SunderRpcService", StringComparison.Ordinal);
        var original = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", source));
        var attribute = $"[SunderRpcRuntimeUse(Required = {required.ToString().ToLowerInvariant()}{(actions.Length == 0 ? "" : ", Actions = " + actions)})]";
        var compiled = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", source.Replace("[SunderRpcService", attribute + "\n[SunderRpcService", StringComparison.Ordinal)));
        var contract = Assert.Single(compiled.Contracts);
        Assert.Equal(original.Contracts[0].Descriptor.GetCanonicalUtf8(), contract.Descriptor.GetCanonicalUtf8());
        Assert.Equal(original.Contracts[0].Descriptor.AbiSha256, contract.Descriptor.AbiSha256);
        var bytes = Write(contractLock, compiled, imports);
        var receipt = AssertReceipt(bytes);
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
        Assert.Equal(provider ? 1 : 0, projection.Manifest.Provides!.Count);
        var use = Assert.Single(projection.Manifest.UsesContracts!);
        Assert.Equal("runtime", use!.Role);
        Assert.Equal(required, use.Required);
        Assert.Equal(expectedActions.Split(','), use.Actions);
        Assert.Equal(contract.Descriptor.ContractId, use.ContractId);
        Assert.Equal(contract.Descriptor.AbiSha256, use.AbiSha256);
        Assert.Equal(required, receipt.GetProperty("contracts")[0].GetProperty("runtimeUse").GetProperty("required").GetBoolean());

        var exportPath = _temporary.GetPath("runtime-policy-export.json");
        File.WriteAllBytes(exportPath, SunderRpcAuthoringDocuments.WritePackageExport("1.0.0", "Owner", "Debug", "net10.0", Fingerprint, contractLock.Sha256, PackageId, compiled.Contracts));
        var export = SunderRpcAuthoringDocuments.ReadPackageProjectExport(exportPath, "1.0.0", "Owner", "Debug", "net10.0", PackageId);
        Assert.True(SunderRpcAuthoringDocuments.SameUsePolicy(contract.RuntimeUse, Assert.Single(export.Contracts).RuntimeUse));
        _ = SunderRpcAuthoringDocuments.ValidatePackageProjectExport(exportPath, "1.0.0", "Owner", "Debug", "net10.0", PackageId, contractLock.Sha256, compiled.Contracts);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidatePackageProjectExport(exportPath, "1.0.0", "Owner", "Debug", "net10.0", PackageId, contractLock.Sha256, original.Contracts));
        using var exportJson = JsonDocument.Parse(File.ReadAllBytes(exportPath));
        using var exportSchema = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/package-project-export.schema.json")));
        Assert.True(JsonSchemaTestValidator.IsValid(exportSchema.RootElement, exportJson.RootElement));
        var appBytes = SunderRpcAuthoringDocuments.WriteProjectImportReceipt("1.0.0", Fingerprint, contractLock.Sha256, PackageId, PackageVersion, export.Contracts);
        var app = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(appBytes, contractLock.Sha256, PackageId, PackageVersion);
        var appUse = Assert.Single(app.Manifest.UsesContracts!);
        Assert.Equal("app", appUse!.Role);
        Assert.True(appUse.Required);
        Assert.Equal(new[] { "discover" }, appUse.Actions);
        Assert.DoesNotContain("runtimeUse", Encoding.UTF8.GetString(appBytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("required")]
    [InlineData("actions")]
    [InlineData("duplicate")]
    [InlineData("wrong-role")]
    [InlineData("missing-policy")]
    [InlineData("null-policy")]
    [InlineData("empty-actions")]
    [InlineData("noncanonical-actions")]
    [InlineData("unknown-member")]
    public void RuntimeReceipt_RejectsGrantAndPolicyDrift(string mutation)
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var imports = Prepare(contractLock);
        var compiled = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource.Replace("[SunderRpcService",
            "[SunderRpcRuntimeUse(Actions = new[] { \"discover\" })]\n[SunderRpcService", StringComparison.Ordinal)));
        var receipt = JsonNode.Parse(Write(contractLock, compiled, imports))!;
        var use = receipt["uses"]![0]!;
        var contract = receipt["contracts"]![0]!.AsObject();
        switch (mutation)
        {
            case "missing": receipt["uses"]!.AsArray().Clear(); break;
            case "required": use["required"] = true; break;
            case "actions": use["actions"] = new JsonArray("discover", "invoke"); break;
            case "duplicate": receipt["uses"]!.AsArray().Add(use.DeepClone()); break;
            case "wrong-role": use["role"] = "app"; break;
            case "missing-policy": contract.Remove("runtimeUse"); break;
            case "null-policy": contract["runtimeUse"] = null; break;
            case "empty-actions": contract["runtimeUse"]!["actions"] = new JsonArray(); break;
            case "noncanonical-actions": contract["runtimeUse"]!["actions"] = new JsonArray("invoke", "discover"); break;
            case "unknown-member": contract["runtimeUse"]!["extra"] = true; break;
        }
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            Encoding.UTF8.GetBytes(receipt.ToJsonString(JsonOptions) + "\n"), contractLock.Sha256, PackageId, PackageVersion));
    }

    [Fact]
    public void RuntimePolicy_ComposesWithLockedUsesButCannotOverrideImportedAuthority()
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var compilation = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource.Replace("[SunderRpcService",
            "[SunderRpcRuntimeUse(Actions = new[] { \"discover\" })]\n[SunderRpcService", StringComparison.Ordinal)));
        var bytes = Write(contractLock, compilation, imports);
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
        Assert.Equal(2, projection.Manifest.UsesContracts!.Count);
        Assert.Single(projection.Manifest.PackageApiImports!);
        Assert.Equal(new[] { "discover" }, Assert.Single(projection.Manifest.UsesContracts, use => use!.ContractId == compilation.Contracts[0].Descriptor.ContractId)!.Actions);
        var receipt = JsonNode.Parse(bytes)!;
        var imported = receipt["contracts"]!.AsArray().Single(contract => contract!["contractId"]!.GetValue<string>() == ContractId)!;
        imported["runtimeUse"] = new JsonObject { ["required"] = false, ["actions"] = new JsonArray("discover", "invoke") };
        var error = Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            Encoding.UTF8.GetBytes(receipt.ToJsonString(JsonOptions) + "\n"), contractLock.Sha256, PackageId, PackageVersion));
        Assert.Contains("Imported contracts cannot declare owned consumer policy", error.Message, StringComparison.Ordinal);
    }
}
