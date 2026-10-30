using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderRpcConsumerRequirementsTests
{
    private static readonly JsonObject Fixture = ReadFixture();

    public static IEnumerable<object[]> Cases() => Fixture["cases"]!.AsArray().Select(value => new object[] { value!["id"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Satisfies_ExecutesSharedConsumerRecipesWithoutChangingIntegrityEvidence(string id)
    {
        var recipe = Fixture["cases"]!.AsArray().Single(value => value!["id"]!.GetValue<string>() == id)!;
        var sourceJson = Fixture["baseline"]!.DeepClone();
        Apply(sourceJson, recipe["source"]);
        var providerJson = sourceJson.DeepClone();
        Apply(providerJson, recipe["provider"]);
        var source = Parse(sourceJson);
        var provider = Parse(providerJson);
        var methods = (recipe["methods"] ?? Fixture["methods"])!.AsArray().Select(value =>
            new SunderRpcRequiredMethod(value!["serviceId"]!.GetValue<string>(), value["methodId"]!.GetValue<string>())).ToArray();
        var views = (recipe["views"]?.AsObject() ?? []).ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value!.AsArray().Select(value => value!.GetValue<string>()).ToArray(), StringComparer.Ordinal);
        var exact = (recipe["exactOutputs"]?.AsArray() ?? []).Select(value => value!.GetValue<string>()).ToArray();
        var requirements = new SunderRpcConsumerRequirements(source, methods, views, exact);
        var sourceBytes = source.GetCanonicalUtf8();
        var providerBytes = provider.GetCanonicalUtf8();
        var evidence = requirements.ToCanonicalUtf8();
        var result = SunderRpcContractCompatibility.Satisfies(requirements, provider);

        Assert.Equal(recipe["satisfies"]!.GetValue<bool>(), result.Kind != SunderRpcContractChangeKind.Major);
        if (recipe["code"] is { } code) Assert.Contains(result.Changes, change => change.Code == code.GetValue<string>());
        Assert.Equal(sourceBytes, source.GetCanonicalUtf8());
        Assert.Equal(providerBytes, provider.GetCanonicalUtf8());
        Assert.Equal(evidence, requirements.ToCanonicalUtf8());
        using var json = JsonDocument.Parse(evidence);
        Assert.Equal(evidence, SunderRpcConsumerRequirements.Parse(json.RootElement, source).ToCanonicalUtf8());
        Assert.Equal(evidence, new SunderRpcConsumerRequirements(source, methods.Reverse().ToArray(), views, exact.Reverse().ToArray()).ToCanonicalUtf8());
        Assert.DoesNotContain("Unused", requirements.RequiredDefinitions);
    }

    [Fact]
    public void Requirements_MatchTheSharedCanonicalSelectorBytes()
    {
        var source = Parse(Fixture["baseline"]!);
        var requirement = new SunderRpcConsumerRequirements(source, [new("main", "watch"), new("main", "call")],
            new Dictionary<string, IReadOnlyList<string>> { ["Detail"] = ["keep"] });
        var bytes = ReadFixtureBytes("requirements.json");
        Assert.Equal(bytes, requirement.ToCanonicalUtf8());
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(bytes, SunderRpcConsumerRequirements.Parse(json.RootElement, source).ToCanonicalUtf8());
    }

    [Fact]
    public void Requirements_ContainReferencedSchemaClosureNotTheCompleteImportedInventory()
    {
        var source = Parse(Fixture["baseline"]!);
        var methods = new List<SunderRpcRequiredMethod> { new("main", "call") };
        var full = new SunderRpcConsumerRequirements(source, methods);
        methods.Clear();
        Assert.Single(full.Methods);
        var narrow = new SunderRpcConsumerRequirements(source, [new("main", "call")],
            new Dictionary<string, IReadOnlyList<string>> { ["Detail"] = ["keep"] });
        Assert.Equal(["Detail", "Error", "Input", "Other", "Output"], full.RequiredDefinitions);
        Assert.Equal(["Detail", "Error", "Input", "Output"], narrow.RequiredDefinitions);
        Assert.Equal(6, source.Definitions.Count);
        var exact = source.GetCanonicalUtf8();
        Assert.Contains("Unused", System.Text.Encoding.UTF8.GetString(exact));
        Assert.Equal(exact, source.GetCanonicalUtf8());
    }

    [Theory]
    [InlineData("unknown-member")]
    [InlineData("schema-version-type")]
    [InlineData("unordered-methods")]
    [InlineData("duplicate-methods")]
    [InlineData("null-views")]
    [InlineData("excessive-members")]
    public void MalformedOrNoncanonicalEvidenceIsRejected(string invalid)
    {
        var source = Parse(Fixture["baseline"]!);
        var value = new SunderRpcConsumerRequirements(source, [new("main", "call"), new("main", "watch")]).ToJson();
        switch (invalid)
        {
            case "unknown-member": value["unsupported"] = true; break;
            case "schema-version-type": value["schemaVersion"] = "1"; break;
            case "unordered-methods": value["methods"] = new JsonArray(value["methods"]!.AsArray().Reverse().Select(method => method!.DeepClone()).ToArray()); break;
            case "duplicate-methods": value["methods"]![1] = value["methods"]![0]!.DeepClone(); break;
            case "null-views": value["outputMembers"] = null; break;
            case "excessive-members": value["outputMembers"]!["Detail"] = new JsonArray(Enumerable.Repeat("keep", 257).Select(member => (JsonNode?)JsonValue.Create(member)).ToArray()); break;
        }
        Assert.Throws<InvalidDataException>(() => SunderRpcConsumerRequirements.Parse(JsonSerializer.SerializeToElement(value), source));
    }

    [Theory]
    [InlineData("unknown-method")]
    [InlineData("duplicate-method")]
    [InlineData("unknown-view")]
    [InlineData("unreachable-view")]
    [InlineData("unknown-member")]
    [InlineData("exact-view")]
    [InlineData("orphan-child-view")]
    public void InvalidSelectorsFailClosed(string invalid)
    {
        var source = Parse(Fixture["baseline"]!);
        SunderRpcRequiredMethod[] methods = invalid switch
        {
            "unknown-method" => [new("main", "absent")],
            "duplicate-method" => [new("main", "call"), new("main", "call")],
            _ => [new("main", "call")],
        };
        var views = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (invalid == "unknown-view") views["Missing"] = [];
        if (invalid == "unreachable-view") views["Unused"] = [];
        if (invalid == "unknown-member") views["Detail"] = ["missing"];
        if (invalid == "exact-view") views["Detail"] = ["keep"];
        if (invalid == "orphan-child-view") { views["Output"] = ["name"]; views["Detail"] = ["keep"]; }
        Assert.Throws<InvalidDataException>(() => new SunderRpcConsumerRequirements(source, methods, views, invalid == "exact-view" ? ["Detail"] : []));
    }

    [Theory]
    [InlineData("contractId")]
    [InlineData("contractVersion")]
    [InlineData("descriptorSha256")]
    [InlineData("abiSha256")]
    public void RequirementsCannotBeReboundToDifferentProvenance(string member)
    {
        var source = Parse(Fixture["baseline"]!);
        var value = new SunderRpcConsumerRequirements(source, [new("main", "call")]).ToJson();
        value[member] = "untrusted";
        Assert.Throws<InvalidDataException>(() => SunderRpcConsumerRequirements.Parse(JsonSerializer.SerializeToElement(value), source));
    }

    private static SunderRpcContractDescriptor Parse(JsonNode value) => SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(value));

    [Fact]
    public void ReferencedServiceProjection_CannotChangeTargetsBehindAnEqualWireSchema()
    {
        var source = Parse(Fixture["baseline"]!);
        var requirements = new SunderRpcConsumerRequirements(source, [new("main", "call")]);
        SunderRpcModelProjection Projection(string target) => new(new Dictionary<string, string>(),
            new Dictionary<string, SunderRpcServiceReferenceProjection> { ["Detail"] = new(target, "service") });
        var original = Projection("example.target");
        Assert.True(SunderRpcConsumerCompatibility.MatchesProjection(requirements, original, Projection("example.target")));
        Assert.False(SunderRpcConsumerCompatibility.MatchesProjection(requirements, original, Projection("example.other")));
        Assert.False(SunderRpcConsumerCompatibility.MatchesProjection(requirements, original, SunderRpcModelProjection.Empty));
    }

    [Fact]
    public void OutputReader_IgnoresOnlyUnknownRecordMembersAndValidatesDeclaredValues()
    {
        var source = Parse(Fixture["baseline"]!);
        var input = JsonSerializer.SerializeToElement(new { name = "old", nested = new { keep = "yes", added = 42 }, items = new[] { new { keep = "yes", added = 42 } }, added = true });
        var value = SunderRpcConsumerOutput.Read(source, "#/$defs/Output", input);
        Assert.False(value.TryGetProperty("added", out _));
        Assert.False(value.GetProperty("nested").TryGetProperty("added", out _));
        Assert.False(value.GetProperty("items")[0].TryGetProperty("added", out _));
        Assert.Throws<InvalidDataException>(() => SunderRpcConsumerOutput.Read(source, "#/$defs/Output",
            JsonSerializer.SerializeToElement(new { name = 42, nested = new { keep = "yes" }, items = Array.Empty<object>() })));
        Assert.Throws<InvalidDataException>(() => SunderRpcConsumerOutput.Read(source, "#/$defs/Output", input, ["Detail"]));
    }

    [Fact]
    public void ExplicitOutputView_DoesNotRequireOrDeliverUnselectedMembers()
    {
        var source = Parse(Fixture["baseline"]!);
        var requirements = new SunderRpcConsumerRequirements(source, [new("main", "call")],
            new Dictionary<string, IReadOnlyList<string>> { ["Output"] = ["name"] });
        var reader = requirements.OutputReader();
        var value = SunderRpcConsumerOutput.Read(reader, "#/$defs/Output", JsonSerializer.SerializeToElement(new { name = "kept", nested = false, items = 42 }));
        Assert.Equal("{\"name\":\"kept\"}", value.GetRawText());
        Assert.Equal(source.Sha256, requirements.Source.Sha256);
        Assert.Equal(4, source.Definitions["Output"].GetProperty("properties").GetPropertyCount());
    }
    private static void Apply(JsonNode value, JsonNode? operations)
    {
        foreach (var operation in operations?.AsArray() ?? [])
        {
            var path = operation!["path"]!.GetValue<string>().Split('/').Skip(1).Select(token => token.Replace("~1", "/").Replace("~0", "~")).ToArray();
            var parent = value;
            foreach (var token in path[..^1]) parent = parent[token]!;
            if (operation["remove"]?.GetValue<bool>() == true) parent.AsObject().Remove(path[^1]);
            else parent[path[^1]] = operation["value"]!.DeepClone();
        }
    }
    private static JsonObject ReadFixture() => JsonNode.Parse(ReadFixtureBytes("cases.json"))!.AsObject();
    private static byte[] ReadFixtureBytes(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "specs/rpc/v1/authoring/consumer-requirements", name);
            if (File.Exists(path)) return File.ReadAllBytes(path);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Consumer-requirement fixtures were not found.");
    }
}
