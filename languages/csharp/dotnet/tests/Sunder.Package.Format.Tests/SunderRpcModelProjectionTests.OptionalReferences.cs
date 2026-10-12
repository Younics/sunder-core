using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed partial class SunderRpcModelProjectionTests
{
    [Theory]
    [InlineData("Context", true, false)]
    [InlineData("Context", true, true)]
    [InlineData("Imported_Context", false, true)]
    public void OptionalReferences_AdmitOnlyExplicitDirectOccurrencesIncludingBorrowedRecords(string owner, bool model, bool aliases)
    {
        var descriptor = OptionalDescriptor("example.optional", owner, aliases);
        var projection = OptionalProjection(owner, model, optional: true);
        ValidateOptionalGraph((descriptor, projection));
        Assert.Equal(model, projection.Models.ContainsKey(owner));
        Assert.Equal(new[] { "executionTarget" }, projection.OptionalServiceReferenceMembers[owner]);
        Assert.DoesNotContain("strictTarget", projection.OptionalServiceReferenceMembers[owner]);
        using var json = JsonDocument.Parse(projection.ToCanonicalUtf8());
        Assert.Equal(new[] { "models", "serviceReferences", "optionalServiceReferenceMembers" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        var parsed = SunderRpcModelProjection.Parse(json.RootElement);
        ValidateOptionalGraph((descriptor, parsed));
        Assert.Equal(projection.ToCanonicalUtf8(), parsed.ToCanonicalUtf8());
    }

    [Fact]
    public void OptionalReferences_CopyAndSortMetadataWithoutChangingWireIdentityOrStrictDefaults()
    {
        var descriptor = OptionalDescriptor("example.optional");
        var members = new List<string> { "executionTarget" };
        var owners = new Dictionary<string, IReadOnlyList<string>> { ["Context"] = members };
        var strict = OptionalProjection("Context", true, false);
        var policy = new SunderRpcModelProjection(strict.Models, strict.ServiceReferences, owners);
        var before = policy.ToCanonicalUtf8();
        members[0] = "strictTarget"; owners.Clear();
        policy.ToJson()["optionalServiceReferenceMembers"]!["Context"]![0] = "strictTarget";
        Assert.Equal(before, policy.ToCanonicalUtf8());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)policy.OptionalServiceReferenceMembers["Context"])[0] = "strictTarget");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, IReadOnlyList<string>>)policy.OptionalServiceReferenceMembers).Clear());
        Assert.False(strict.ToJson().ContainsKey("optionalServiceReferenceMembers"));
        Assert.Empty(SunderRpcModelProjection.Empty.OptionalServiceReferenceMembers);
        Assert.Equal(strict.ToCanonicalUtf8(), new SunderRpcModelProjection(strict.Models, strict.ServiceReferences,
            new Dictionary<string, IReadOnlyList<string>>()).ToCanonicalUtf8());
        var strictEntry = Entry(descriptor, strict); var optionalEntry = Entry(descriptor, policy);
        Assert.Equal(strictEntry.DescriptorSha256, optionalEntry.DescriptorSha256);
        Assert.Equal(strictEntry.AbiSha256, optionalEntry.AbiSha256);
        Assert.False(strictEntry.ToCanonicalUtf8().AsSpan().SequenceEqual(optionalEntry.ToCanonicalUtf8()));
        Assert.NotEqual(new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [strictEntry]).ModelProjectionSha256,
            new SunderRpcModelProjectionDocument(1, "example.owner", "1.0.0", [optionalEntry]).ModelProjectionSha256);

        var unordered = new Dictionary<string, IReadOnlyList<string>> { ["Z"] = new[] { "z", "a" }, ["A"] = new[] { "b" } };
        var sorted = new SunderRpcModelProjection(strict.Models, strict.ServiceReferences, unordered);
        Assert.Equal(new[] { "A", "Z" }, sorted.OptionalServiceReferenceMembers.Keys);
        Assert.Equal(new[] { "a", "z" }, sorted.OptionalServiceReferenceMembers["Z"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Context\":[]}")]
    [InlineData("{\"Context\":null}")]
    [InlineData("{\"Context\":[null]}")]
    [InlineData("{\"Context\":[1]}")]
    [InlineData("{\"Context\":[\"\"]}")]
    [InlineData("{\"Context\":[\"executionTarget\",\"executionTarget\"]}")]
    [InlineData("{\"Context\":[\"ExecutionTarget\",\"executionTarget\"]}")]
    [InlineData("{\"Context\":[\"z\",\"a\"]}")]
    [InlineData("{\"Z\":[\"a\"],\"A\":[\"a\"]}")]
    [InlineData("{\"Context\":[\"a\"],\"context\":[\"b\"]}")]
    [InlineData("{\"Context\":[\"a\"],\"Context\":[\"b\"]}")]
    [InlineData("{\"#/$defs/Context\":[\"executionTarget\"]}")]
    public void OptionalReferences_RejectMalformedOrNoncanonicalMaps(string policy)
    {
        using var json = JsonDocument.Parse("{\"models\":{},\"serviceReferences\":{},\"optionalServiceReferenceMembers\":" + policy + "}");
        Assert.Throws<InvalidDataException>(() => SunderRpcModelProjection.Parse(json.RootElement));
    }

    [Theory]
    [InlineData("unknown-owner")]
    [InlineData("unknown-property")]
    [InlineData("forged-path")]
    [InlineData("union-owner")]
    [InlineData("alias-owner")]
    [InlineData("scalar-owner")]
    [InlineData("container-owner")]
    [InlineData("nonnullable")]
    [InlineData("wire-optional")]
    [InlineData("nullable-dto")]
    [InlineData("container")]
    [InlineData("lookalike")]
    [InlineData("unmapped-reference")]
    [InlineData("missing-target")]
    public void OptionalReferences_RejectPoliciesOutsideTheExactOccurrenceContract(string failure)
    {
        var source = JsonNode.Parse(OptionalDescriptor("example.optional").GetCanonicalUtf8())!;
        var definitions = source["$defs"]!.AsObject();
        var owner = "Context"; var member = "executionTarget";
        var property = definitions[owner]!["properties"]!;
        if (failure == "unknown-owner") owner = "Missing";
        if (failure == "unknown-property") member = "missing";
        if (failure == "forged-path") member = "executionTarget.endpoint";
        if (failure == "union-owner") owner = "NullableReference";
        if (failure == "scalar-owner") owner = "Null";
        if (failure == "container-owner")
        {
            definitions["Array"] = JsonNode.Parse("""{"type":"array","items":{"$ref":"#/$defs/Locator"}}""");
            property[member] = new JsonObject { ["$ref"] = "#/$defs/Array" };
            owner = "Array"; member = "*";
        }
        if (failure == "alias-owner")
        {
            definitions["OwnerAlias"] = new JsonObject { ["$ref"] = "#/$defs/Context" };
            source["services"]!["example"]!["methods"]!["invoke"]!["requestSchema"]!["$ref"] = "#/$defs/OwnerAlias";
            owner = "OwnerAlias";
        }
        if (failure == "nonnullable") member = "strictTarget";
        if (failure == "wire-optional") definitions[owner]!["required"] = new JsonArray("strictTarget");
        if (failure is "nullable-dto" or "lookalike")
        {
            definitions["Other"] = definitions[failure == "lookalike" ? "Locator" : "Empty"]!.DeepClone();
            property[member] = JsonNode.Parse("""{"oneOf":[{"type":"null"},{"$ref":"#/$defs/Other"}]}""");
        }
        if (failure == "container") property[member] = JsonNode.Parse("""{"oneOf":[{"type":"null"},{"type":"array","items":{"$ref":"#/$defs/Locator"}}]}""");
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(source.ToJsonString()));
        var references = failure == "unmapped-reference" ? new Dictionary<string, SunderRpcServiceReferenceProjection>()
            : new Dictionary<string, SunderRpcServiceReferenceProjection> { ["Locator"] = new(failure == "missing-target" ? "example.missing" : "example.target", "example") };
        var projection = new SunderRpcModelProjection(new Dictionary<string, string>(), references,
            new Dictionary<string, IReadOnlyList<string>> { [owner] = new[] { member } });
        Assert.Throws<InvalidDataException>(() => ValidateOptionalGraph((descriptor, projection)));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void OptionalReferences_SameAuthorityModelIdentityIncludesPolicyAcrossRebasedDefinitions(bool leftOptional, bool rightOptional, bool valid)
    {
        var left = OptionalDescriptor("example.left", "LeftContext", aliases: false);
        var right = OptionalDescriptor("example.right", "RightContext", aliases: true);
        void Validate() => ValidateOptionalGraph((left, OptionalProjection("LeftContext", true, leftOptional)),
            (right, OptionalProjection("RightContext", true, rightOptional)));
        if (valid) Validate(); else Assert.Throws<InvalidDataException>(Validate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OptionalReferences_SameModelAliasesRetainUnderlyingRecordPolicy(bool optional, bool modelAlias)
    {
        var descriptor = OptionalDescriptor("example.optional");
        var source = JsonNode.Parse(descriptor.GetCanonicalUtf8())!;
        source["$defs"]!["OwnerAlias"] = new JsonObject { ["$ref"] = "#/$defs/Context" };
        source["services"]!["example"]!["methods"]!["invoke"]!["requestSchema"]!["$ref"] = "#/$defs/OwnerAlias";
        descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(source.ToJsonString()));
        var policy = OptionalProjection("Context", true, optional);
        var models = new Dictionary<string, string>(policy.Models) { ["OwnerAlias"] = "SharedContext" };
        if (!modelAlias) models.Remove("Context");
        var projection = new SunderRpcModelProjection(models, policy.ServiceReferences, policy.OptionalServiceReferenceMembers);
        var other = OptionalDescriptor("example.other", "OtherContext");
        ValidateOptionalGraph((descriptor, projection), (other, OptionalProjection("OtherContext", true, optional)));
    }

    [Fact]
    public void OptionalReferences_BorrowedNestedPolicyParticipatesInOuterModelClosure()
    {
        (SunderRpcContractDescriptor, SunderRpcModelProjection) Make(string id, bool optional)
        {
            var source = JsonNode.Parse(OptionalDescriptor(id, "Imported_Context").GetCanonicalUtf8())!;
            source["$defs"]!["Envelope"] = JsonNode.Parse("""{"type":"object","properties":{"context":{"$ref":"#/$defs/Imported_Context"}},"required":["context"],"additionalProperties":false}""");
            source["services"]!["example"]!["methods"]!["invoke"]!["requestSchema"]!["$ref"] = "#/$defs/Envelope";
            var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(source.ToJsonString()));
            var projection = OptionalProjection("Imported_Context", false, optional);
            return (descriptor, new(new Dictionary<string, string> { ["Envelope"] = "Envelope" }, projection.ServiceReferences,
                projection.OptionalServiceReferenceMembers));
        }
        Assert.Throws<InvalidDataException>(() => ValidateOptionalGraph(Make("example.left", false), Make("example.right", true)));
        ValidateOptionalGraph(Make("example.left", true), Make("example.right", true));
    }

    [Fact]
    public void OptionalReferences_MapBoundsAreIndependentOfModelOwnershipAndCountAllMembers()
    {
        var map = Enumerable.Range(0, 256).ToDictionary(i => $"D{i:000}", _ => (IReadOnlyList<string>)new[] { "member" });
        var empty = SunderRpcModelProjection.Empty;
        Assert.Equal(256, new SunderRpcModelProjection(empty.Models, empty.ServiceReferences, map).OptionalServiceReferenceMembers.Count);
        map.Add("D256", new[] { "member" });
        Assert.Throws<InvalidDataException>(() => new SunderRpcModelProjection(empty.Models, empty.ServiceReferences, map));
        map = Enumerable.Range(0, 16).ToDictionary(i => $"D{i:000}", _ => (IReadOnlyList<string>)Enumerable.Range(0, 256).Select(i => $"m{i:000}").ToArray());
        var atLimit = new SunderRpcModelProjection(empty.Models, empty.ServiceReferences, map);
        using (var document = JsonDocument.Parse(atLimit.ToCanonicalUtf8())) Assert.Equal(16, SunderRpcModelProjection.Parse(document.RootElement).OptionalServiceReferenceMembers.Count);
        map.Add("D016", new[] { "member" });
        Assert.Throws<InvalidDataException>(() => new SunderRpcModelProjection(empty.Models, empty.ServiceReferences, map));
        var invalid = atLimit.ToJson(); invalid["optionalServiceReferenceMembers"]!["D016"] = new JsonArray("member");
        using (var document = JsonDocument.Parse(invalid.ToJsonString())) Assert.Throws<InvalidDataException>(() => SunderRpcModelProjection.Parse(document.RootElement));
        invalid = atLimit.ToJson(); invalid["optionalServiceReferenceMembers"]!["D000"]!.AsArray().Add("z");
        using (var document = JsonDocument.Parse(invalid.ToJsonString())) Assert.Throws<InvalidDataException>(() => SunderRpcModelProjection.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("\u000b")]
    [InlineData("\u2028")]
    [InlineData("\ue000")]
    [InlineData("target.\u00e9")]
    public void OptionalReferences_WireNamesUseExistingScalarGrammarAndDeterministicOuterEscapes(string member)
    {
        var source = JsonNode.Parse(OptionalDescriptor("example.optional").GetCanonicalUtf8())!;
        var owner = source["$defs"]!["Context"]!;
        var property = owner["properties"]!["executionTarget"]!.DeepClone();
        owner["properties"]!.AsObject().Remove("executionTarget"); owner["properties"]![member] = property;
        owner["required"] = new JsonArray(member, "strictTarget");
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(source.ToJsonString()));
        var original = OptionalProjection("Context", true, false);
        var projection = new SunderRpcModelProjection(original.Models, original.ServiceReferences,
            new Dictionary<string, IReadOnlyList<string>> { ["Context"] = new[] { member } });
        ValidateOptionalGraph((descriptor, projection));
        var quoted = new StringBuilder(); SunderRpcAuthoringJson.AppendString(quoted, member);
        Assert.Contains(quoted.ToString(), Encoding.UTF8.GetString(projection.ToCanonicalUtf8()), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(projection.ToCanonicalUtf8());
        Assert.Equal(projection.ToCanonicalUtf8(), SunderRpcModelProjection.Parse(document.RootElement).ToCanonicalUtf8());
    }

    private static void ValidateOptionalGraph(params (SunderRpcContractDescriptor Descriptor, SunderRpcModelProjection Projection)[] sources)
    {
        var target = Descriptor("example.target");
        SunderRpcModelProjectionGraph.Validate(sources.Select(s => Entry(s.Descriptor, s.Projection)).Append(Entry(target, SunderRpcModelProjection.Empty)).ToArray(),
            sources.Select(s => s.Descriptor).Append(target).ToDictionary(d => d.ContractId),
            sources.Select(s => s.Descriptor).Append(target).ToDictionary(d => d.ContractId, _ => "package:example.owner"));
    }

    private static SunderRpcModelProjection OptionalProjection(string owner, bool model, bool optional)
        => new(model ? new Dictionary<string, string> { [owner] = "SharedContext" } : new Dictionary<string, string>(),
            new Dictionary<string, SunderRpcServiceReferenceProjection> { ["Locator"] = new("example.target", "example") },
            optional ? new Dictionary<string, IReadOnlyList<string>> { [owner] = new[] { "executionTarget" } } : null);

    private static SunderRpcContractDescriptor OptionalDescriptor(string id, string owner = "Context", bool aliases = true)
    {
        var nullable = aliases ? """{"oneOf":[{"$ref":"#/$defs/Null"},{"$ref":"#/$defs/ReferenceAlias"}]}"""
            : """{"oneOf":[{"type":"null"},{"$ref":"#/$defs/Locator"}]}""";
        var json = JsonNode.Parse(Descriptor(id).GetCanonicalUtf8())!;
        json["$defs"] = new JsonObject
        {
            ["Empty"] = JsonNode.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
            ["Locator"] = SunderRpcServiceReferenceSchema.Create(),
            ["Null"] = new JsonObject { ["type"] = "null" },
            ["NullableReference"] = JsonNode.Parse(nullable),
            ["ReferenceAlias"] = new JsonObject { ["$ref"] = "#/$defs/Locator" },
            [owner] = new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject
                {
                    ["executionTarget"] = aliases ? new JsonObject { ["$ref"] = "#/$defs/NullableReference" } : JsonNode.Parse(nullable),
                    ["strictTarget"] = new JsonObject { ["$ref"] = "#/$defs/Locator" },
                },
                ["required"] = new JsonArray("executionTarget", "strictTarget"), ["additionalProperties"] = false,
            },
        };
        json["services"]!["example"]!["methods"]!["invoke"]!["requestSchema"]!["$ref"] = "#/$defs/" + owner;
        json["services"]!["example"]!["methods"]!["invoke"]!["responseSchema"]!["$ref"] = "#/$defs/Empty";
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }
}
