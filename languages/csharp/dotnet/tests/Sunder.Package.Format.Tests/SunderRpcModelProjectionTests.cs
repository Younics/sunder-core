using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed partial class SunderRpcModelProjectionTests
{
    [Fact]
    public void CurrentCorpus_ValidatesExactSidecarBytesAndDescriptorAssociations()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "conformance");
        using var expectations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "expectations.json")));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "valid", "authoring-receipt-mixed.json")));
        var descriptors = receipt.RootElement.GetProperty("contracts").EnumerateArray()
            .Select(c => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(c.GetProperty("canonicalDescriptor").GetString()!)))
            .ToDictionary(d => d.ContractId);
        using var serviceExport = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "valid", "package-project-export-service-reference.json")));
        foreach (var contract in serviceExport.RootElement.GetProperty("contracts").EnumerateArray())
        {
            var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
            Assert.Equal(contract.GetProperty("descriptorBytes").GetInt32(), descriptor.GetCanonicalUtf8().Length);
            Assert.Equal(contract.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
            Assert.Equal(contract.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
            descriptors.Add(descriptor.ContractId, descriptor);
        }
        var evidence = expectations.RootElement.GetProperty("immutableArtifactEvidence").EnumerateArray()
            .ToDictionary(e => e.GetProperty("publicInventorySha256").GetString()!);
        foreach (var item in evidence.Values)
        {
            var document = SunderRpcModelProjectionDocument.Parse(File.ReadAllBytes(Path.Combine(root, item.GetProperty("modelProjectionFixturePath").GetString()!)));
            Assert.Equal(item.GetProperty("modelProjectionBytes").GetInt32(), document.ModelProjectionBytes);
            Assert.Equal(item.GetProperty("modelProjectionSha256").GetString(), document.ModelProjectionSha256);
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "valid"), "*contract-lock.json"))
        {
            using var contractLock = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var artifact in contractLock.RootElement.GetProperty("packageArtifacts").EnumerateObject())
            {
                var selected = evidence[artifact.Value.GetProperty("publicInventorySha256").GetString()!];
                Assert.Equal(selected.GetProperty("modelProjectionBytes").GetInt32(), artifact.Value.GetProperty("modelProjectionBytes").GetInt32());
                Assert.Equal(selected.GetProperty("modelProjectionSha256").GetString(), artifact.Value.GetProperty("modelProjectionSha256").GetString());
            }
        }
        foreach (var fixture in expectations.RootElement.GetProperty("fixtures").EnumerateArray()
                     .Where(f => f.GetProperty("schemaPath").GetString() == "../rpc-model-projection.schema.json"))
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, fixture.GetProperty("fixturePath").GetString()!));
            void Validate()
            {
                var projection = SunderRpcModelProjectionDocument.Parse(bytes);
                Assert.Equal(bytes, projection.ToCanonicalUtf8());
                if (fixture.TryGetProperty("expectedFileSha256", out var hash))
                    Assert.Equal(hash.GetString(), projection.ModelProjectionSha256);
                SunderPackageContractBundleManifest[] bundles = [];
                if (fixture.TryGetProperty("semanticContextFixturePaths", out var contexts))
                {
                    using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, contexts[0].GetString()!)));
                    foreach (var contract in inventory.RootElement.GetProperty("contracts").EnumerateArray())
                        if (contract.TryGetProperty("canonicalDescriptor", out var canonical))
                        {
                            var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(canonical.GetString()!));
                            descriptors.TryAdd(descriptor.ContractId, descriptor);
                        }
                    bundles = inventory.RootElement.GetProperty("contracts").EnumerateArray().Select(c => new SunderPackageContractBundleManifest
                    {
                        ContractId = c.GetProperty("contractId").GetString(), Version = c.GetProperty("contractVersion").GetString(),
                        DescriptorSha256 = c.GetProperty("descriptorSha256").GetString(), AbiSha256 = c.GetProperty("abiSha256").GetString(),
                        Source = (c.TryGetProperty("artifactSource", out var source) ? source : c.GetProperty("source")).GetString(), AuthorityKind = c.GetProperty("authorityKind").GetString(),
                        AuthorityId = c.GetProperty("authorityId").GetString(), Exposure = "public",
                    }).ToArray();
                }
                projection.Validate(new SunderPackageManifest { Id = projection.PackageId, Version = projection.PackageVersion, ContractBundles = bundles }, descriptors);
            }
            if (fixture.GetProperty("semanticValid").GetBoolean()) Validate();
            else Assert.Contains(fixture.GetProperty("expectedSemanticDiagnosticId").GetString()!,
                Assert.Throws<InvalidDataException>(Validate).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProjectionAndDocument_DetachSourceMapsListsJsonAndByteEvidence()
    {
        var models = new Dictionary<string, string> { ["Value"] = "SharedValue" };
        var references = new Dictionary<string, SunderRpcServiceReferenceProjection>();
        var projection = new SunderRpcModelProjection(models, references);
        var entry = Entry(Descriptor("example.first"), projection);
        var list = new List<SunderRpcModelProjectionContract> { entry };
        var document = new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", list);
        var before = document.ToCanonicalUtf8();
        models["Value"] = "Changed"; references["Other"] = new("example.first", "example"); list.Clear();
        projection.ToJson()["models"]!["Value"] = "ChangedAgain";
        document.ToCanonicalUtf8()[0] = 0;
        Assert.Equal(before, document.ToCanonicalUtf8());
        Assert.Equal("SharedValue", projection.Models["Value"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)projection.Models).Add("Other", "Other"));
        Assert.Equal(document.ModelProjectionSha256, SunderRpcModelProjectionDocument.Parse(before).ModelProjectionSha256);
    }

    [Theory]
    [InlineData("missing-map")]
    [InlineData("null-map")]
    [InlineData("null-value")]
    [InlineData("unknown-member")]
    [InlineData("wrong-version")]
    [InlineData("wrong-type")]
    [InlineData("invalid-id")]
    [InlineData("invalid-version")]
    [InlineData("invalid-hash")]
    [InlineData("invalid-name")]
    [InlineData("keyword-name")]
    [InlineData("case-name")]
    [InlineData("ref-key")]
    [InlineData("case-key")]
    [InlineData("overlap")]
    [InlineData("map-order")]
    [InlineData("duplicate-entry")]
    public void StrictDocument_RejectsMalformedOrAmbiguousProjection(string failure)
    {
        var json = Document().ToJson();
        var entry = json["contracts"]![0]!;
        var projection = entry["modelProjection"]!;
        switch (failure)
        {
            case "missing-map": projection.AsObject().Remove("models"); break;
            case "null-map": projection["models"] = null; break;
            case "null-value": projection["models"]!["Value"] = null; break;
            case "unknown-member": entry["sourcePath"] = "private.cs"; break;
            case "wrong-version": json["schemaVersion"] = 2; break;
            case "wrong-type": entry["contractId"] = 7; break;
            case "invalid-id": json["packageId"] = "Example.Owner"; break;
            case "invalid-version": entry["contractVersion"] = "1.0.0-beta.01"; break;
            case "invalid-hash": entry["abiSha256"] = new string('A', 64); break;
            case "invalid-name": projection["models"]!["Value"] = "Ns.Value"; break;
            case "keyword-name": projection["models"]!["Value"] = "class"; break;
            case "case-name": projection["models"]!["Z"] = "sharedValue"; break;
            case "ref-key": projection["models"] = new JsonObject { ["#/$defs/Value"] = "Value" }; break;
            case "case-key": projection["models"]!["value"] = "Other"; break;
            case "overlap": projection["serviceReferences"]!["Value"] = new SunderRpcServiceReferenceProjection("example.first", "example").ToJson(); break;
            case "map-order": projection["models"] = new JsonObject { ["2"] = "Second", ["10"] = "Tenth" }; break;
            case "duplicate-entry": json["contracts"]!.AsArray().Add(entry.DeepClone()); break;
        }
        Assert.Throws<InvalidDataException>(() => SunderRpcModelProjectionDocument.Parse(SunderRpcModelProjectionJson.Write(json)));
    }

    [Fact]
    public void StrictDocument_RejectsNoncanonicalEncodingAndBudgetsBeforeMaterialization()
    {
        var bytes = Document().ToCanonicalUtf8();
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var invalid in new[]
        {
            Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.TrimEnd()), Encoding.UTF8.GetBytes(text + "\n"),
            Encoding.UTF8.GetBytes(text.Replace("example.owner", "example.\\u006fwner", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"SchemaVersion\": 1,", StringComparison.Ordinal)),
            new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray(),
            new byte[] { 0xff }.Concat(bytes).ToArray(),
            Encoding.UTF8.GetBytes(new string('[', 17) + "0" + new string(']', 17)),
            Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("0", 524_288)) + "]"),
        }) Assert.Throws<InvalidDataException>(() => SunderRpcModelProjectionDocument.Parse(invalid));

        var exactLimit = new byte[SunderPackageFormat.MaxRpcModelProjectionBytes];
        Array.Fill(exactLimit, (byte)' '); exactLimit[0] = (byte)'{'; exactLimit[1] = (byte)'}';
        using var acceptedPreflight = SunderRpcAuthoringJson.ParseModelProjection(exactLimit);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringJson.ParseModelProjection([.. exactLimit, (byte)' ']));
        Assert.Throws<InvalidDataException>(() => new SunderRpcModelProjection(
            Enumerable.Range(0, 257).ToDictionary(i => "D" + i, i => "M" + i), new Dictionary<string, SunderRpcServiceReferenceProjection>()));
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("facet", false)]
    [InlineData("required", false)]
    [InlineData("nullable", false)]
    [InlineData("enum", false)]
    [InlineData("other-authority", true)]
    public void ModelIdentity_ComparesNormalizedClosuresAcrossDefinitionSpellings(string change, bool valid)
    {
        var first = Descriptor("example.first", "A");
        var second = Descriptor("example.second", "B", change is "same" ? null : change);
        var entries = new[] { Entry(first, Projection("A", "SharedValue")), Entry(second, Projection("B", "SharedValue")) };
        var descriptors = new[] { first, second }.ToDictionary(d => d.ContractId);
        var authorities = new Dictionary<string, string> { [first.ContractId] = "package:example.owner",
            [second.ContractId] = change == "other-authority" ? "package:example.other" : "package:example.owner" };
        if (valid) SunderRpcModelProjectionGraph.Validate(entries, descriptors, authorities);
        else Assert.Throws<InvalidDataException>(() => SunderRpcModelProjectionGraph.Validate(entries, descriptors, authorities));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelIdentity_AlsoChecksAliasesWithinOneContract(bool conflicting)
    {
        var json = JsonNode.Parse(Descriptor("example.first").GetCanonicalUtf8())!;
        json["$defs"]!["Alias"] = json["$defs"]![conflicting ? "ValueText" : "Value"]!.DeepClone();
        json["services"]!["example"]!["methods"]!["invoke"]!["responseSchema"]!["$ref"] = "#/$defs/Alias";
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var projection = new SunderRpcModelProjection(new Dictionary<string, string>
        { ["Alias"] = "SharedValue", ["Value"] = "SharedValue" }, new Dictionary<string, SunderRpcServiceReferenceProjection>());
        var document = new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [Entry(descriptor, projection)]);
        void Validate() => document.Validate(Manifest(descriptor), new Dictionary<string, SunderRpcContractDescriptor> { [descriptor.ContractId] = descriptor });
        if (conflicting) Assert.Throws<InvalidDataException>(Validate); else Validate();
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("unreachable")]
    [InlineData("descriptor")]
    [InlineData("package")]
    [InlineData("coverage")]
    public void ArtifactAssociation_RejectsChangedSourceProjectionOrIdentity(string failure)
    {
        var descriptor = Descriptor("example.first");
        var doc = Document();
        var manifest = Manifest(descriptor);
        if (failure == "unknown-key") doc = new(1, "example.owner", "1.0.0", [Entry(descriptor, Projection("Unknown", "Hidden"))]);
        if (failure == "unreachable")
        {
            var json = JsonNode.Parse(descriptor.GetCanonicalUtf8())!;
            json["$defs"]!["Hidden"] = new JsonObject { ["type"] = "string" };
            descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
            manifest = Manifest(descriptor);
            doc = new(1, "example.owner", "1.0.0", [Entry(descriptor, Projection("Hidden", "Hidden"))]);
        }
        if (failure == "descriptor") descriptor = Descriptor("example.first", change: "facet");
        if (failure == "package") doc = new(1, "example.changed", "1.0.0", doc.Contracts);
        if (failure == "coverage") doc = new(1, "example.owner", "1.0.0", []);
        Assert.Throws<InvalidDataException>(() => doc.Validate(manifest, new Dictionary<string, SunderRpcContractDescriptor> { [descriptor.ContractId] = descriptor }));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("endpoint-only", false)]
    [InlineData("missing-generation", false)]
    [InlineData("service-instance", false)]
    [InlineData("unsafe-generation", false)]
    [InlineData("private-target", false)]
    [InlineData("missing-service", false)]
    public void ServiceReferences_RequireCompleteLocatorAndPublicTarget(string failure, bool valid)
    {
        var target = Descriptor("example.target");
        var schema = SunderRpcServiceReferenceSchema.Create();
        if (failure == "endpoint-only") schema = JsonNode.Parse("""{"type":"object","properties":{"endpoint":{"type":"string","minLength":1,"maxLength":256}},"required":["endpoint"],"additionalProperties":false}""")!.AsObject();
        if (failure == "missing-generation")
        {
            schema["properties"]!.AsObject().Remove("sessionGeneration");
            schema["required"] = new JsonArray(schema["required"]!.AsArray().Where(v => v!.GetValue<string>() != "sessionGeneration").Select(v => v!.DeepClone()).ToArray());
        }
        if (failure == "service-instance")
        { schema["properties"]!["Service"] = new JsonObject { ["type"] = "string" }; schema["required"]!.AsArray().Add("Service"); }
        if (failure == "unsafe-generation") schema["properties"]!["sessionGeneration"] = new JsonObject { ["type"] = "number", ["format"] = "float64" };
        var sourceJson = JsonNode.Parse(target.GetCanonicalUtf8())!;
        sourceJson["contractId"] = "example.source";
        sourceJson["$defs"] = new JsonObject { ["Value"] = schema };
        var source = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(sourceJson.ToJsonString()));
        var projection = new SunderRpcModelProjection(new Dictionary<string, string>(), new Dictionary<string, SunderRpcServiceReferenceProjection>
        { ["Value"] = new(target.ContractId, failure == "missing-service" ? "absent" : "example") });
        var document = new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0",
            failure == "private-target" ? [Entry(source, projection)] : [Entry(source, projection), Entry(target, SunderRpcModelProjection.Empty)]);
        var manifest = Manifest([source, target], failure == "private-target" ? target.ContractId : null);
        void Validate() => document.Validate(manifest, new[] { source, target }.ToDictionary(d => d.ContractId));
        if (valid) Validate(); else Assert.Throws<InvalidDataException>(Validate);
    }

    private static SunderRpcModelProjectionDocument Document()
        => new(1, "example.owner", "1.0.0", [Entry(Descriptor("example.first"), Projection("Value", "SharedValue"))]);

    private static SunderRpcModelProjection Projection(string key, string name)
        => new(new Dictionary<string, string> { [key] = name }, new Dictionary<string, SunderRpcServiceReferenceProjection>());

    private static SunderRpcModelProjectionContract Entry(SunderRpcContractDescriptor descriptor, SunderRpcModelProjection projection,
        SunderRpcMethodPolicies? methodPolicies = null)
        => new(descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, projection, methodPolicies);

    private static SunderPackageManifest Manifest(params SunderRpcContractDescriptor[] descriptors)
        => Manifest(descriptors, null);

    private static SunderPackageManifest Manifest(SunderRpcContractDescriptor[] descriptors, string? privateContract)
        => new()
        {
            Id = "example.owner", Version = "1.0.0",
            ContractBundles = descriptors.Select(d => new SunderPackageContractBundleManifest
            {
                ContractId = d.ContractId, Version = d.Version, DescriptorSha256 = d.Sha256, AbiSha256 = d.AbiSha256,
                Source = "owned", AuthorityKind = "package", AuthorityId = "example.owner", Exposure = d.ContractId == privateContract ? "package" : "public",
            }).ToArray(),
        };

    private static SunderRpcContractDescriptor Descriptor(string id, string definition = "Value", string? change = null)
    {
        var text = JsonNode.Parse("""
            {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"example.first","version":"1.0.0","errors":{},
             "services":{"example":{"methods":{"invoke":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Value"},"responseSchema":{"$ref":"#/$defs/Value"}}}}},
             "$defs":{"Value":{"type":"object","properties":{"text":{"$ref":"#/$defs/Text"}},"required":["text"],"additionalProperties":false},"Text":{"type":"string","minLength":1,"maxLength":128}}}
            """)!;
        text["contractId"] = id;
        if (change is "facet" or "other-authority") text["$defs"]!["Text"]!["maxLength"] = 64;
        if (change == "required") text["$defs"]!["Value"]!["required"] = new JsonArray();
        if (change == "nullable") text["$defs"]!["Value"]!["properties"]!["text"] = JsonNode.Parse("""{"oneOf":[{"type":"null"},{"$ref":"#/$defs/Text"}]}""");
        if (change == "enum") text["$defs"]!["Text"]!["enum"] = new JsonArray("a", "b");
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(text.ToJsonString().Replace("Value", definition, StringComparison.Ordinal)
            .Replace("Text", definition + "Text", StringComparison.Ordinal)));
    }
}
