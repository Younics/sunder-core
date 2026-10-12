using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcModelProjectionEvidenceTests
{
    [Fact]
    public void MethodPolicies_RoundtripMetadataWithoutChangingDescriptorAbiOrGrants()
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-method-policy-evidence");
        var plain = Compile();
        var timedSource = Source.Replace("public ValueTask<SharedValue>", "[SunderMethod(TimeoutSeconds = 120)] public ValueTask<SharedValue>", StringComparison.Ordinal)
            .Replace("public ValueTask<PrivateValue>", "[SunderMethod(TimeoutSeconds = 900)] public ValueTask<PrivateValue>", StringComparison.Ordinal);
        var timed = Compile(timedSource);
        foreach (var (before, after) in plain.Contracts.Zip(timed.Contracts))
        {
            Assert.Equal(before.Descriptor.GetCanonicalUtf8(), after.Descriptor.GetCanonicalUtf8());
            Assert.Equal(before.Descriptor.AbiSha256, after.Descriptor.AbiSha256);
            Assert.True(before.MethodPolicies.IsEmpty); Assert.False(after.MethodPolicies.IsEmpty);
        }
        var contractLock = EmptyLock();
        var imports = SunderRpcCSharpImportPrepass.Prepare(contractLock, temporary.GetPath("cache"));
        byte[] Receipt(SunderRpcCSharpCompilationResult compiled) => SunderRpcAuthoringDocuments.WriteAuthoringReceipt(
            "1.0.0", Fingerprint, contractLock, "example.projection", "1.0.0", compiled, imports);
        var timedReceipt = Receipt(timed); var plainReceipt = Receipt(plain);
        var parsed = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(timedReceipt, contractLock.Sha256, "example.projection", "1.0.0");
        var plainParsed = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(plainReceipt, contractLock.Sha256, "example.projection", "1.0.0");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(parsed.Manifest), JsonSerializer.SerializeToElement(plainParsed.Manifest)));
        Assert.DoesNotContain("methodPolicies", Encoding.UTF8.GetString(plainReceipt), StringComparison.Ordinal);
        using var receiptJson = JsonDocument.Parse(timedReceipt);
        foreach (var contract in receiptJson.RootElement.GetProperty("contracts").EnumerateArray())
        {
            var names = contract.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(Array.IndexOf(names, "modelProjection") + 1, Array.IndexOf(names, "methodPolicies"));
        }
        var sidecar = SunderRpcModelProjectionDocument.Parse(SunderRpcAuthoringDocuments.PublicModelProjectionBytes(parsed));
        Assert.Equal(2, sidecar.Contracts.Count);
        Assert.All(sidecar.Contracts, contract => Assert.Equal(120, Assert.Single(Assert.Single(contract.MethodPolicies.Services).Value).Value.TimeoutSeconds));
        Assert.DoesNotContain("\"timeoutSeconds\": 900", Encoding.UTF8.GetString(sidecar.ToCanonicalUtf8()), StringComparison.Ordinal);
        Assert.NotEqual(sidecar.ModelProjectionSha256, SunderRpcModelProjectionDocument.Parse(SunderRpcAuthoringDocuments.PublicModelProjectionBytes(plainParsed)).ModelProjectionSha256);
        var baseline = new SunderRpcOwnedPackageBaseline("example.projection", new Dictionary<string, SunderRpcOwnedContractBaseline>(), [], Fingerprint);
        string SourceFingerprint(string text) => SunderRpcAuthoringDocuments.ComputeInputFingerprint("example.projection", "1.0.0", "Fixture", "Debug", "net10.0", true,
            [], [new("Source.cs", text)], [], baseline, contractLock);
        Assert.NotEqual(SourceFingerprint(Source), SourceFingerprint(timedSource));

        var path = temporary.GetPath(SunderRpcAuthoringDocuments.PackageExportFileName);
        File.WriteAllBytes(path, SunderRpcAuthoringDocuments.WritePackageExport("1.0.0", "Fixture", "Debug", "net10.0", Fingerprint,
            contractLock.Sha256, "example.projection", "1.0.0", timed.Contracts));
        var export = SunderRpcAuthoringDocuments.ReadPackageProjectExport(path, "1.0.0", "Fixture", "Debug", "net10.0", "example.projection", "1.0.0");
        Assert.All(export.Contracts, contract => Assert.True(SunderRpcAuthoringDocuments.SameMethodPolicies(
            timed.Contracts.Single(value => value.ContractKey == contract.ContractKey).MethodPolicies, contract.MethodPolicies)));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidatePackageProjectExport(path, "1.0.0", "Fixture", "Debug", "net10.0",
            "example.projection", "1.0.0", contractLock.Sha256, plain.Contracts));
        var app = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(SunderRpcAuthoringDocuments.WriteProjectImportReceipt("1.0.0", Fingerprint,
            contractLock.Sha256, "example.projection", "1.0.0", export.Contracts), contractLock.Sha256, "example.projection", "1.0.0");
        _ = SunderRpcAuthoringDocuments.MergeReceiptProjections([parsed, app]);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.MergeReceiptProjections([plainParsed, app]));
        Assert.Throws<InvalidDataException>(() => AggregateSunderPackageTask.MergeModelProjections(parsed.Manifest,
            [sidecar, SunderRpcModelProjectionDocument.Parse(SunderRpcAuthoringDocuments.PublicModelProjectionBytes(plainParsed))]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"first\":{\"read\":{\"timeoutSeconds\":0}}}")]
    [InlineData("{\"first\":{\"read\":{\"timeoutSeconds\":2101}}}")]
    [InlineData("{\"first\":{\"unknown\":{\"timeoutSeconds\":120}}}")]
    public void MethodPolicies_ReceiptRejectsNoncanonicalOrUnlinkedMetadata(string policy)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-method-policy-invalid");
        var compiled = Compile(Source.Replace("public ValueTask<SharedValue>", "[SunderMethod(TimeoutSeconds = 120)] public ValueTask<SharedValue>", StringComparison.Ordinal));
        var contractLock = EmptyLock();
        var receipt = JsonNode.Parse(SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, "example.projection", "1.0.0",
            compiled, SunderRpcCSharpImportPrepass.Prepare(contractLock, temporary.GetPath("cache"))))!;
        receipt["contracts"]![0]!["methodPolicies"] = JsonNode.Parse(policy);
        var bytes = Encoding.UTF8.GetBytes(receipt.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, "example.projection", "1.0.0"));
    }
}
