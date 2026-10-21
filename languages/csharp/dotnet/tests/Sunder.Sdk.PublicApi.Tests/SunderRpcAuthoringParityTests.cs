using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcAuthoringParityTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "current-parity");

    [Fact]
    public void CurrentSourceCorpus_IndexesExactlyTheReviewedSourcesAndDiagnosticLocations()
    {
        using var document = Open("csharp/expectations.json");
        var expectations = document.RootElement;
        Assert.Equal(1, expectations.GetProperty("formatVersion").GetInt32());
        Assert.Equal("current-source", Text(expectations, "status"));
        Assert.Equal("14.0", Text(expectations, "languageVersion"));
        var valid = expectations.GetProperty("validFixtures").EnumerateArray().ToArray();
        var invalid = expectations.GetProperty("invalidFixtures").EnumerateArray().ToArray();
        Assert.Equal(4, valid.Length);
        Assert.Equal(16, invalid.Length);
        Assert.Equal(7, valid.Sum(value => value.GetProperty("contracts").GetArrayLength()));
        var paths = valid.Concat(invalid).Select(value => Text(value, "path")).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        var sourceRoot = Resolve("csharp");
        Assert.Equal(paths, Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
        foreach (var fixture in valid.Concat(invalid))
        {
            var bytes = File.ReadAllBytes(Path.Combine(sourceRoot, Text(fixture, "path")));
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            var source = Encoding.UTF8.GetString(bytes);
            Assert.Contains("using Sunder.Sdk.Services;", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SunderRpcService(", source, StringComparison.Ordinal);
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
            Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (!fixture.TryGetProperty("spans", out var spans)) continue;
            foreach (var span in spans.EnumerateArray())
                Assert.Contains(Text(span, "text"), source.Split('\n')[span.GetProperty("line").GetInt32() - 1], StringComparison.Ordinal);
            if (spans.GetArrayLength() == 0) Assert.Equal("rpc-authoring.model-projection-invalid", Text(fixture, "diagnosticId"));
        }
    }

    [Fact]
    public void CurrentMetadata_PreservesCompleteLockReceiptInventoryAndProjectionPins()
    {
        using var expectations = Open("metadata-expectations.json");
        Assert.Equal(1, expectations.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("current-metadata", Text(expectations.RootElement, "status"));
        var expected = expectations.RootElement.GetProperty("mixed");
        var lockBytes = File.ReadAllBytes(Resolve(Text(expected, "lock")));
        using var lockDocument = JsonDocument.Parse(lockBytes);
        using var receiptDocument = Open(Text(expected, "receipt"));
        using var inventoryDocument = Open(Text(expected, "inventory"));
        using var receiptSchema = Open("../authoring-receipt.schema.json");
        var contractLock = lockDocument.RootElement;
        var receipt = receiptDocument.RootElement;
        AssertClosedRecord(receiptSchema.RootElement, receipt);
        Assert.Equal(Text(expected, "lockSha256"), Hash(lockBytes));
        Assert.Equal(Hash(lockBytes), Text(receipt, "lockSha256"));
        Assert.Equal(Text(expected, "packageId"), Text(receipt, "packageId"));
        AssertJson(expected.GetProperty("providers"), receipt.GetProperty("providers"));
        AssertJson(expected.GetProperty("uses"), receipt.GetProperty("uses"));

        var packageId = Text(expected, "selectedPackageId");
        var artifact = contractLock.GetProperty("packageArtifacts").GetProperty(packageId);
        var receiptArtifact = Find(receipt.GetProperty("packageArtifacts"), "packageId", packageId);
        AssertClosedRecord(receiptSchema.RootElement.GetProperty("$defs").GetProperty("packageArtifact"), receiptArtifact);
        foreach (var property in artifact.EnumerateObject().Where(property => property.Name != "contracts"))
            AssertJson(property.Value, receiptArtifact.GetProperty(property.Name));
        Assert.Equal(artifact.GetProperty("contracts").EnumerateObject().Count(), receiptArtifact.GetProperty("contracts").GetArrayLength());
        foreach (var contract in artifact.GetProperty("contracts").EnumerateObject())
        {
            var record = Find(receiptArtifact.GetProperty("contracts"), "contractId", contract.Name);
            foreach (var property in contract.Value.EnumerateObject()) AssertJson(property.Value, record.GetProperty(property.Name));
        }

        var projectionBytes = File.ReadAllBytes(Resolve(Text(expected, "projection")));
        using var projectionDocument = JsonDocument.Parse(projectionBytes);
        var projection = projectionDocument.RootElement;
        Assert.Equal(packageId, Text(projection, "packageId"));
        Assert.Equal(Text(artifact, "selectedPackageVersion"), Text(projection, "packageVersion"));
        Assert.Equal(expected.GetProperty("modelProjectionBytes").GetInt32(), projectionBytes.Length);
        Assert.Equal(Text(expected, "modelProjectionSha256"), Hash(projectionBytes));
        Assert.Equal(projectionBytes.Length, artifact.GetProperty("modelProjectionBytes").GetInt32());
        Assert.Equal(Hash(projectionBytes), Text(artifact, "modelProjectionSha256"));
        foreach (var contract in projection.GetProperty("contracts").EnumerateArray())
        {
            var record = Find(receipt.GetProperty("contracts"), "contractId", Text(contract, "contractId"));
            foreach (var property in contract.EnumerateObject()) AssertJson(property.Value, record.GetProperty(property.Name));
        }
        var inventory = inventoryDocument.RootElement;
        Assert.Equal(packageId, Text(inventory, "packageId"));
        Assert.Equal(Text(artifact, "selectedPackageVersion"), Text(inventory, "packageVersion"));
        Assert.Equal(Text(artifact, "selectedPackageContentIdentity"), Text(inventory, "packageContentIdentity"));
        Assert.Equal(Text(expected, "publicInventorySha256"), Text(artifact, "publicInventorySha256"));
        Assert.Equal(inventory.GetProperty("publicContractCount").GetInt32(), artifact.GetProperty("publicContractCount").GetInt32());
        foreach (var contract in inventory.GetProperty("contracts").EnumerateArray())
        {
            var selected = artifact.GetProperty("contracts").GetProperty(Text(contract, "contractId"));
            foreach (var property in contract.EnumerateObject().Where(property => property.Name != "contractId"))
                AssertJson(property.Value, selected.GetProperty(property.Name == "contractVersion" ? "resolvedContractVersion" : property.Name));
        }
    }

    [Fact]
    public void CurrentReceipt_ContractsRetainCanonicalWireBytesAndFullSourceMetadata()
    {
        using var expectations = Open("metadata-expectations.json");
        var expected = expectations.RootElement.GetProperty("mixed");
        using var receiptDocument = Open(Text(expected, "receipt"));
        using var schema = Open("../authoring-receipt.schema.json");
        var receipt = receiptDocument.RootElement;
        var contracts = receipt.GetProperty("contracts");
        Assert.Equal(expected.GetProperty("contracts").GetArrayLength(), contracts.GetArrayLength());
        AssertOrdered(contracts, "contractKey");
        AssertOrdered(receipt.GetProperty("bundles"), "contractKey");
        AssertOrdered(receipt.GetProperty("generatedBindings"), "contractKey");
        foreach (var reviewed in expected.GetProperty("contracts").EnumerateArray())
        {
            var key = Text(reviewed, "contractKey");
            var contract = Find(contracts, "contractKey", key);
            var bundle = Find(receipt.GetProperty("bundles"), "contractKey", key);
            AssertClosedRecord(schema.RootElement.GetProperty("$defs").GetProperty("receiptContract"), contract);
            foreach (var property in reviewed.EnumerateObject())
                AssertJson(property.Value, property.Name is "source" or "authorityKind" or "authorityId"
                    ? bundle.GetProperty(property.Name) : contract.GetProperty(property.Name));
            _ = AssertContract(contract);
        }
    }

    [Fact]
    public void CurrentReceipt_ConsumerReadinessAndPlatformProviderRolesStayDistinct()
    {
        using var expectations = Open("metadata-expectations.json");
        var expected = expectations.RootElement.GetProperty("mixed");
        using var lockDocument = Open(Text(expected, "lock"));
        using var receiptDocument = Open(Text(expected, "receipt"));
        var contractLock = lockDocument.RootElement;
        var receipt = receiptDocument.RootElement;
        var packageId = Text(expected, "selectedPackageId");
        var intent = contractLock.GetProperty("packageImports").GetProperty(packageId);
        AssertJson(expected.GetProperty("consumerRoles"), intent.GetProperty("consumerRoles"));
        Assert.Equal(Text(expected, "presence"), Text(intent, "presence"));
        var api = Assert.Single(receipt.GetProperty("packageApiImports").EnumerateArray());
        Assert.Equal(packageId, Text(api, "packageId"));
        Assert.Equal(Text(expected, "presence"), Text(api, "presence"));
        Assert.Equal(Strings(expected.GetProperty("packageApiContracts")), api.GetProperty("bundleRefs").EnumerateArray().Select(value => Text(value, "contractId")));
        Assert.Equal(Strings(intent.GetProperty("consumerRoles")), api.GetProperty("useRefs").EnumerateArray().Select(value => Text(value, "role")).Distinct(StringComparer.Ordinal));
        var use = Assert.Single(receipt.GetProperty("uses").EnumerateArray());
        Assert.True(use.GetProperty("required").GetBoolean()); // Optional package presence never weakens required provider readiness.
        Assert.Contains(Text(use, "contractKey"), Strings(intent.GetProperty("requiredContracts")));

        var platform = Assert.Single(receipt.GetProperty("platformImports").EnumerateArray());
        var selected = contractLock.GetProperty("platformImports").GetProperty(Text(expected, "platformKey"));
        Assert.Equal(Text(expected, "platformKey"), Text(platform, "key"));
        foreach (var name in new[] { "authorityKind", "authorityId", "authorityReleaseRange", "includePrerelease", "selectedAuthorityRelease", "selectedAuthorityContentIdentity", "contractId" })
            AssertJson(selected.GetProperty(name), platform.GetProperty(name));
        Assert.Equal(Text(selected, "resolvedContractVersion"), Text(platform, "contractVersion"));
        Assert.DoesNotContain(api.GetProperty("bundleRefs").EnumerateArray(), value => Text(value, "contractId") == Text(platform, "contractId"));
        var providerRef = Assert.Single(platform.GetProperty("providerRefs").EnumerateArray());
        Assert.Equal("runtime", Text(providerRef, "role"));
        var provider = Find(receipt.GetProperty("providers"), "providerId", Text(providerRef, "providerId"));
        Assert.Equal("public", Text(provider, "audience"));
        Assert.Equal(Text(platform, "contractId"), Text(provider, "contractKey"));
        Assert.Empty(receipt.GetProperty("providerImports").EnumerateArray());
    }

    [Fact]
    public void CurrentConsumerLocks_RequireExplicitRolesAndRealProjectionEvidence()
    {
        using var expectations = Open("metadata-expectations.json");
        var fixtures = expectations.RootElement.GetProperty("consumerLocks").EnumerateArray().ToArray();
        Assert.Equal(3, fixtures.Length);
        var projectionBytes = File.ReadAllBytes(Resolve("../conformance/valid/rpc-model-projection-nonempty.json"));
        foreach (var fixture in fixtures)
        {
            using var document = Open(Text(fixture, "path"));
            var root = document.RootElement;
            var intent = Assert.Single(root.GetProperty("packageImports").EnumerateObject()).Value;
            AssertJson(fixture.GetProperty("roles"), intent.GetProperty("consumerRoles"));
            var artifact = Assert.Single(root.GetProperty("packageArtifacts").EnumerateObject()).Value;
            Assert.Equal(projectionBytes.Length, artifact.GetProperty("modelProjectionBytes").GetInt32());
            Assert.Equal(Hash(projectionBytes), Text(artifact, "modelProjectionSha256"));
        }
    }

    [Fact]
    public void CurrentProjectExports_PreserveModelsReferencesOptionalityAndMethodPolicies()
    {
        using var expectations = Open("metadata-expectations.json");
        using var schema = Open("../package-project-export.schema.json");
        foreach (var fixture in expectations.RootElement.GetProperty("projectExports").EnumerateArray())
        {
            using var exportDocument = Open(Text(fixture, "export"));
            var export = exportDocument.RootElement;
            AssertClosedRecord(schema.RootElement, export);
            using var projectionDocument = Open(Text(fixture, "projection"));
            var projections = projectionDocument.RootElement.GetProperty("contracts");
            Assert.Equal(projections.GetArrayLength(), export.GetProperty("contracts").GetArrayLength());
            foreach (var contract in export.GetProperty("contracts").EnumerateArray())
            {
                AssertClosedRecord(schema.RootElement.GetProperty("$defs").GetProperty("exportedContract"), contract);
                var descriptor = AssertContract(contract);
                var reviewed = Find(projections, "contractId", descriptor.ContractId);
                foreach (var property in reviewed.EnumerateObject()) AssertJson(property.Value, contract.GetProperty(property.Name));
                Assert.Equal(reviewed.TryGetProperty("methodPolicies", out _), contract.TryGetProperty("methodPolicies", out _));
                Assert.Equal("owned", Text(contract, "source"));
                Assert.Equal("package", Text(contract, "authorityKind"));
                Assert.Equal(Text(export, "packageId"), Text(contract, "authorityId"));
            }
        }
    }

    [Fact]
    public void CurrentSchemas_RequireProjectionPinsSourceMetadataAndExplicitConsumerRoles()
    {
        using var expectations = Open("metadata-expectations.json");
        var required = expectations.RootElement.GetProperty("schemaRequirements");
        foreach (var (schemaPath, definition, review) in new[]
        {
            ("../contract-lock.schema.json", "packageArtifact", "lockArtifact"),
            ("../contract-lock.schema.json", "packageImport", "consumerImport"),
            ("../authoring-receipt.schema.json", "receiptContract", "receiptContract"),
            ("../package-project-export.schema.json", "exportedContract", "exportedContract"),
        })
        {
            using var schema = Open(schemaPath);
            var record = schema.RootElement.GetProperty("$defs").GetProperty(definition);
            Assert.Equal(JsonValueKind.False, record.GetProperty("additionalProperties").ValueKind);
            Assert.Equal(Strings(required.GetProperty(review)).Order(StringComparer.Ordinal), Strings(record.GetProperty("required")).Order(StringComparer.Ordinal));
        }
    }

    private static SunderRpcContractDescriptor AssertContract(JsonElement contract)
    {
        var bytes = Encoding.UTF8.GetBytes(Text(contract, "canonicalDescriptor"));
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        Assert.Equal(bytes, descriptor.GetCanonicalUtf8());
        Assert.Equal(contract.GetProperty("descriptorBytes").GetInt32(), bytes.Length);
        Assert.Equal(Text(contract, "contractId"), descriptor.ContractId);
        Assert.Equal(Text(contract, "contractVersion"), descriptor.Version);
        Assert.Equal(Text(contract, "descriptorSha256"), Hash(bytes));
        Assert.Equal(Text(contract, "abiSha256"), descriptor.AbiSha256);
        return descriptor;
    }

    // Only validates the reviewed closed record boundary. Full JSON Schema and
    // semantic rejection fixtures remain in SunderRpcAuthoringSchemaFixtureTests.
    private static void AssertClosedRecord(JsonElement schema, JsonElement value)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
        foreach (var required in Strings(schema.GetProperty("required"))) Assert.Contains(required, names);
        foreach (var name in names) Assert.True(schema.GetProperty("properties").TryGetProperty(name, out _), name);
    }

    private static void AssertOrdered(JsonElement values, string property)
    {
        var keys = values.EnumerateArray().Select(value => Text(value, property)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    private static JsonElement Find(JsonElement values, string property, string value)
        => Assert.Single(values.EnumerateArray(), item => Text(item, property) == value);
    private static void AssertJson(JsonElement expected, JsonElement actual) => Assert.True(JsonElement.DeepEquals(expected, actual), $"Expected {expected}; actual {actual}");
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static string[] Strings(JsonElement value) => value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Resolve(string path) => Path.GetFullPath(path, Root);
    private static JsonDocument Open(string path) => JsonDocument.Parse(File.ReadAllBytes(Resolve(path)));
}
