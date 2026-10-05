using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderRpcCSharpUnionCodecTests
{
    private const string Discriminator = "k\n\"\\\u96ea";
    private const string EscapedMember = "p\t\"\\\u0001\u96ea";
    private const string BranchTag = "\t\r\n\"\\caf\u00e9\U0001f680";

    [Fact]
    public async Task Tagged_UnaryAndStreamUseExactTagsAndTypedVariants()
    {
        using var probe = CompileProbe();
        foreach (var json in new[] { Payload(), Tagged("Case"), Branch() })
        {
            AssertJson(json, await probe.RoundTrip("echo", json));
            var events = await probe.Stream("watch", json);
            Assert.Equal(2, events.Count);
            AssertJson(json, events[0]);
            AssertJson(Tagged("Case"), events[1]);
            Assert.Equal(1, events[0].EnumerateObject().Count(property => property.Name == Discriminator));
        }
        Assert.Equal("Branch", probe.LastVariant());
        Assert.Equal(6, probe.Calls());
    }

    [Fact]
    public async Task SourceUntagged_UsesDescriptorDefinitionsAndExistingTransparentEnumAndCollectionCodecs()
    {
        using var probe = CompileProbe();
        foreach (var json in new[]
                 {
                     """{"kind":"record","state":"ready\n\"\\","values":[0,10]}""",
                     """{"kind":"wrapped","values":{"first":"ready\n\"\\","other":"done"}}""",
                 })
        {
            AssertJson(json, await probe.RoundTrip("choice", json));
            AssertJson(json, Assert.Single(await probe.Stream("choice-watch", json)));
        }
        Assert.Equal("WrappedChoice", probe.LastVariant());
        Assert.Equal(4, probe.Calls());
    }

    [Fact]
    public async Task RecursiveAndNestedUnions_PreserveAbsentPresentNullDefaultsAndPropertyNames()
    {
        using var probe = CompileProbe();
        var branch = Branch();
        branch["children"] = new JsonArray(Payload(), Tagged("Case"), null, Branch());
        branch["byKey"] = new JsonObject { ["line\n\"\\\u96ea"] = Branch(), ["none"] = null };
        branch["child"] = Payload();
        branch["optionalChild"] = null;
        AssertJson(branch, await probe.RoundTrip("echo", branch));

        var envelope = new JsonObject
        {
            ["items"] = new JsonArray(branch.DeepClone(), null),
            ["nonNullItems"] = new JsonArray(),
            ["byKey"] = new JsonObject { ["zero"] = Payload(), ["none"] = null },
            ["child"] = null,
        };
        var absent = await probe.RoundTrip("nested", envelope);
        AssertJson(envelope, absent);
        Assert.False(absent.TryGetProperty("optionalChild", out _));
        Assert.False(absent.GetProperty("byKey").GetProperty("zero").TryGetProperty("optionalCount", out _));
        envelope["optionalChild"] = null;
        ((JsonObject)envelope["byKey"]!["zero"]!)["optionalCount"] = 0;
        AssertJson(envelope, await probe.RoundTrip("nested", envelope));
        AssertJson(envelope, Assert.Single(await probe.Stream("nested-watch", envelope)));
        envelope["optionalChild"] = Tagged("Case");
        AssertJson(envelope, await probe.RoundTrip("nested", envelope));
    }

    [Fact]
    public async Task ConcreteVariants_UseSyntheticTagsAtEveryRecursiveReferenceAndDirectRoot()
    {
        using var probe = CompileProbe();
        var node = ConcreteNode();
        node["children"] = new JsonArray(ConcreteNode());
        node["byKey"] = new JsonObject { ["line\n\"\\\u96ea"] = ConcreteNode() };
        node["next"] = ConcreteNode();
        node["peer"] = ConcreteNode();
        node["payload"] = Payload();
        foreach (var method in new[] { "echo", "concrete", "wrapped-concrete" })
        {
            var result = await probe.RoundTrip(method, node);
            AssertJson(node, result);
            Assert.False(result.GetProperty("children")[0].TryGetProperty("next", out _));
            Assert.Equal(Discriminator, result.EnumerateObject().First().Name);
        }
        AssertJson(node, Assert.Single(await probe.Stream("concrete-watch", node)));
        node["next"] = null;
        node.Remove("peer");
        AssertJson(node, await probe.RoundTrip("concrete", node));
        AssertJson(Payload(), await probe.RoundTrip("concrete-payload", Payload()));
        AssertJson(Tagged("Case"), await probe.RoundTrip("concrete-marker", Tagged("Case")));
        Assert.Equal(7, probe.Calls());
    }

    [Fact]
    public async Task ConcreteVariants_RejectInvalidTagsMembersAndNonNullableValuesBeforeDispatch()
    {
        using var probe = CompileProbe();
        var invalid = new List<string> { "null" };
        foreach (var tag in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create("unknown"), JsonValue.Create("Case") })
        {
            var node = ConcreteNode();
            node[Discriminator] = tag;
            invalid.Add(node.ToJsonString());
        }
        var missing = ConcreteNode();
        missing.Remove(Discriminator);
        invalid.Add(missing.ToJsonString());
        var child = ConcreteNode();
        child["children"] = new JsonArray(missing.DeepClone());
        invalid.Add(child.ToJsonString());
        var map = ConcreteNode();
        map["byKey"] = new JsonObject { ["wrong"] = Payload() };
        invalid.Add(map.ToJsonString());
        foreach (var name in new[] { "children", "byKey", "peer" })
        {
            var node = ConcreteNode();
            node[name] = name == "children" ? new JsonArray((JsonNode?)null)
                : name == "byKey" ? new JsonObject { ["null"] = null } : null;
            invalid.Add(node.ToJsonString());
        }
        var colliding = ConcreteNode();
        colliding["K" + Discriminator[1..]] = "node";
        invalid.Add(colliding.ToJsonString());
        var duplicate = ConcreteNode().ToJsonString();
        invalid.Add(duplicate[..^1] + "," + JsonSerializer.Serialize(Discriminator) + ":\"node\"}");
        var extra = ConcreteNode();
        extra["extra"] = false;
        invalid.Add(extra.ToJsonString());
        foreach (var json in invalid)
        {
            await probe.Reject("concrete", json);
            await Assert.ThrowsAsync<JsonException>(() => probe.Stream("concrete-watch", json, validateRequest: false));
        }
        Assert.Equal(0, probe.Calls());
        AssertJson(ConcreteNode(), await probe.RoundTrip("concrete", ConcreteNode()));
    }

    [Fact]
    public async Task SyntheticRequiredTag_WorksWithParameterizedConstructorsAndSetsRequiredMembers()
    {
        using var probe = CompileProbe();
        var child = new JsonObject { [EscapedMember] = "child", ["children"] = new JsonArray(), [Discriminator] = "constructor" };
        var json = new JsonObject { [EscapedMember] = "parent", ["children"] = new JsonArray(child), [Discriminator] = "constructor" };
        AssertJson(json, await probe.RoundTrip("constructed", json));
        AssertJson(json, Assert.Single(await probe.Stream("constructed-watch", json)));
        AssertJson(json, await probe.RoundTrip("echo", json));

        // Exercise the real generated options without the handler's schema-validation boundary.
        var result = probe.CodecRoundTrip("ConstructorNode", json.ToJsonString());
        AssertJson(json, result);
        Assert.Equal(Discriminator, result.EnumerateObject().First().Name);
        var missing = (JsonObject)json.DeepClone();
        missing.Remove(Discriminator);
        Assert.Throws<JsonException>(() => probe.CodecRoundTrip("ConstructorNode", missing.ToJsonString()));
        foreach (var tag in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create("Constructor") })
        {
            var wrong = (JsonObject)json.DeepClone();
            wrong[Discriminator] = tag;
            Assert.Throws<JsonException>(() => probe.CodecRoundTrip("ConstructorNode", wrong.ToJsonString()));
        }
        child.Remove(Discriminator);
        Assert.Throws<JsonException>(() => probe.CodecRoundTrip("ConstructorNode", json.ToJsonString()));
    }

    [Fact]
    public async Task InvalidTaggedPayloads_AreRejectedBeforeUnaryOrStreamDispatch()
    {
        using var probe = CompileProbe();
        var valid = Payload().ToJsonString();
        var key = JsonSerializer.Serialize(Discriminator);
        var caseKey = JsonSerializer.Serialize("K" + Discriminator[1..]);
        var invalid = new List<string>
        {
            "{}", "[]", "0", "null", "\"case\"",
            Tagged("CASE").ToJsonString(),
            valid[..^1] + "," + key + ":\"case\"}",
            valid[..^1] + "," + key + ":\"Case\"}",
            valid[..^1] + "," + caseKey + ":\"case\"}",
            valid[..^1] + ",\"count\":0}",
            valid[..^1] + ",\"Count\":0}",
        };
        foreach (var (name, value) in new (string Name, JsonNode? Value)[]
                 {
                     (Discriminator, null), (Discriminator, 1), ("count", 11), ("count", -1),
                     ("count", "0"), ("count", null), ("extra", false), ("text", 0),
                     ("optionalCount", null), ("priority", 4), ("flag", null),
                 })
        {
            var payload = Payload();
            payload[name] = value;
            invalid.Add(payload.ToJsonString());
        }
        var missing = Payload();
        missing.Remove("flag");
        invalid.Add(missing.ToJsonString());
        var nested = Branch();
        nested["children"] = new JsonArray(Payload(), new JsonObject { [Discriminator] = "unknown" });
        invalid.Add(nested.ToJsonString());
        foreach (var json in invalid)
        {
            await probe.Reject("echo", json);
            await Assert.ThrowsAsync<JsonException>(() => probe.Stream("watch", json, validateRequest: false));
        }
        Assert.Equal(0, probe.Calls());
        AssertJson(Payload(), await probe.RoundTrip("echo", Payload()));
    }

    [Fact]
    public async Task SourceUntagged_RejectsNonmatchingRecordsAndInvalidNestedValuesWithoutFallback()
    {
        using var probe = CompileProbe();
        foreach (var json in new[]
                 {
                     "{}", "[]", "0",
                     """{"kind":"unknown","values":[]}""",
                     """{"kind":"record","state":"done","values":{}}""",
                     """{"kind":"record","state":"DONE","values":[]}""",
                     """{"kind":"record","state":"done","values":[11]}""",
                     """{"kind":"record","state":"done","values":[null]}""",
                     """{"kind":"wrapped","values":{"same":"done","Same":"done"}}""",
                     """{"kind":"wrapped","state":"done","values":{}}""",
                     """{"kind":"wrapped","kind":"record","values":{}}""",
                 })
        {
            await probe.Reject("choice", json);
        }
        Assert.Equal(0, probe.Calls());
    }

    [Fact]
    public async Task NonNullableUnionCollectionElements_RejectNullBeforeDispatch()
    {
        using var probe = CompileProbe();
        const string json = """{"items":[],"nonNullItems":[null],"byKey":{},"child":null}""";
        await probe.Reject("nested", json);
        await Assert.ThrowsAsync<JsonException>(() => probe.Stream("nested-watch", json, validateRequest: false));
        Assert.Equal(0, probe.Calls());
    }

    [Fact]
    public async Task Writes_RejectUnknownRuntimeVariantsInvalidValuesTagCollisionsAndCycles()
    {
        using var probe = CompileProbe();
        foreach (var method in new[] { "unknown", "invalid", "collision", "cycle", "null" })
        {
            var error = await Assert.ThrowsAsync<JsonException>(() => probe.RoundTrip(method, Payload()));
            if (method == "cycle") Assert.Contains("budget", error.Message, StringComparison.Ordinal);
            if (method == "collision") Assert.Contains("collides", error.Message, StringComparison.Ordinal);
        }
        await Assert.ThrowsAsync<JsonException>(() => probe.RoundTrip("unknown-choice", """{"kind":"wrapped","values":{}}"""));
        await Assert.ThrowsAsync<JsonException>(() => probe.Stream("unknown-watch", Payload()));
        await Assert.ThrowsAsync<JsonException>(() => probe.Stream("null-watch", Payload()));
        // A failed nested write must release its synchronous codec budget.
        AssertJson(Payload(), await probe.RoundTrip("echo", Payload()));
    }

    [Fact]
    public async Task Reads_RejectExhaustedDepthWithoutDispatchAndReleaseTheCodecGuard()
    {
        using var probe = CompileProbe();
        JsonObject value = Payload();
        for (var index = 0; index < 40; index++)
        {
            var parent = Branch();
            parent["children"] = new JsonArray(value);
            value = parent;
        }
        var json = value.ToJsonString(new JsonSerializerOptions { MaxDepth = 256 });
        await Assert.ThrowsAsync<JsonException>(() => probe.RoundTrip("echo", json));
        Assert.Equal(0, probe.Calls());
        AssertJson(Payload(), await probe.RoundTrip("echo", Payload()));
    }

    [Fact]
    public void MultipleProviders_EmitOnlyOneLazyDescriptorPerContract()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            [SunderRpcService(SunderRpcExposure.Package)]
            public interface MultiService
            {
                [SunderRpcMethod] ValueTask<Value> EchoAsync(Value request);
            }
            [SunderRpcProvider(typeof(MultiService), ProviderId = "example.unions.first")]
            public sealed partial class FirstProvider : MultiService
            {
                public ValueTask<Value> EchoAsync(Value request) => ValueTask.FromResult(request);
            }
            [SunderRpcProvider(typeof(MultiService), ProviderId = "example.unions.second")]
            public sealed partial class SecondProvider : MultiService
            {
                public ValueTask<Value> EchoAsync(Value request) => ValueTask.FromResult(request);
            }
            [SunderRpcUnion(typeof(First), typeof(Second)), SunderRpcDiscriminator("kind")]
            public abstract record Value;
            [SunderRpcVariant("first")] public sealed record First : Value;
            [SunderRpcVariant("second")] public sealed record Second : Value;
            """;
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = new SunderRpcCSharpCompilerInput("example.unions", "1.0.0", [new("Multi.cs", source)], References());
        var result = compiler.Compile(input);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(2, Assert.Single(result.Contracts).Providers.Count);
        var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts);
        Assert.Single(generated.Split("SunderRpcContractDescriptor.Parse(", StringSplitOptions.None).Skip(1));
        var verified = compiler.Compile(input with
        {
            SupplementalSources = [new("authoring.g.cs", generated)],
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        Assert.Equal(generated, SunderRpcCSharpImplementationEmitter.Emit(verified.Contracts));
    }

    [Fact]
    public void Metadata_TracksExactDefinitionReferencesAndSurvivesPartialTwoPassCompilation()
    {
        var (contract, generated) = CompileContract();
        var signal = Assert.Single(contract.SourceTypes, type => type.SourceType.Name == "Signal");
        Assert.Equal("#/$defs/WireSignal", signal.DefinitionReference);
        Assert.Equal(Discriminator, signal.Union!.Discriminator);
        Assert.Equal(new[] { "#/$defs/WirePayload", "#/$defs/Marker", "#/$defs/Branch", "#/$defs/CollisionSignal", "#/$defs/ConcreteNode", "#/$defs/ConstructorNode" },
            signal.Union.Variants.Select(variant => variant.DefinitionReference));
        Assert.Equal(new[] { "case", "Case", BranchTag, "collision", "node", "constructor" }, signal.Union.Variants.Select(variant => variant.Tag));
        foreach (var variant in signal.Union.Variants)
        {
            var concrete = Assert.Single(contract.SourceTypes, type => type.DefinitionReference == variant.DefinitionReference);
            Assert.Equal(new SunderRpcCSharpSourceSyntheticTagModel(Discriminator, variant.Tag!), concrete.SyntheticTag);
        }
        var choice = Assert.Single(contract.SourceTypes, type => type.SourceType.Name == "Choice");
        Assert.Null(choice.Union!.Discriminator);
        Assert.All(choice.Union.Variants, variant => Assert.Null(variant.Tag));
        Assert.Single(generated.Split("SunderRpcContractDescriptor.Parse(", StringSplitOptions.None).Skip(1));
        Assert.Contains("System.Lazy<global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCustomAttribute", generated, StringComparison.Ordinal);
        var payload = Assert.Single(contract.SourceTypes, type => type.SourceType.Name == "Payload");
        Assert.Single(payload.Properties, property => property.WireName == EscapedMember);
        Assert.DoesNotContain(payload.Properties, property => property.WireName == Discriminator);
        Assert.DoesNotContain("RemoveUnionDiscriminator", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("AddUnionDiscriminator", generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("union-first")]
    [InlineData("concrete-first")]
    [InlineData("concrete-only")]
    public void ConcreteVariantOrdering_PreservesFrozenTaggedDefinitionsAndTwoPassBytes(string order)
    {
        const string direct = "[SunderRpcMethod] public ValueTask<A> DirectAsync(A request) => ValueTask.FromResult(request);";
        const string union = "[SunderRpcMethod] public ValueTask<U> UnionAsync(U request) => ValueTask.FromResult(request);";
        var source = $$"""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            [SunderRpcService(SunderRpcExposure.Package)]
            public sealed partial class DirectService
            {
                {{(order == "union-first" ? union + "\n" + direct : direct + (order == "concrete-only" ? "" : "\n" + union))}}
            }
            [SunderRpcUnion(typeof(A), typeof(B)), SunderRpcDiscriminator("kind")]
            public abstract record U;
            [SunderRpcVariant("a")]
            public sealed record A : U { public required IReadOnlyList<A> Children { get; init; } }
            [SunderRpcVariant("b")]
            public sealed record B : U;
            """;
        var (contract, _) = CompileContract(source);
        var expected = JsonNode.Parse(FrozenConcreteDescriptor)!.AsObject();
        if (order == "concrete-only") expected["services"]!["direct"]!["methods"]!.AsObject().Remove("union");
        var frozen = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(expected.ToJsonString()));
        Assert.Equal(frozen.GetCanonicalUtf8(), contract.Descriptor.GetCanonicalUtf8());
        Assert.Equal(frozen.GetCanonicalAbiProjectionUtf8(), contract.Descriptor.GetCanonicalAbiProjectionUtf8());
        var variant = Assert.Single(contract.SourceTypes, type => type.SourceType.Name == "A");
        Assert.Equal(new SunderRpcCSharpSourceSyntheticTagModel("kind", "a"), variant.SyntheticTag);
        Assert.Single(contract.SourceTypes, type => type.Union is not null);
    }

    [Theory]
    [InlineData("[SunderRpcTransparent] public sealed record First : Value { public required string Value { get; init; } }", "public abstract record Value;", "cannot be transparent")]
    [InlineData("public sealed record First : Value;", "public abstract record Value { public required string Common { get; init; } }", "cannot inherit payload property")]
    [InlineData("public sealed record First : Value { [System.Text.Json.Serialization.JsonPropertyName(\"kind\")] public required string Tag { get; init; } }", "public abstract record Value;", "colliding property identity")]
    [InlineData("public sealed record First : Value { [System.Text.Json.Serialization.JsonPropertyName(\"Kind\")] public required string Tag { get; init; } }", "public abstract record Value;", "collid")]
    public void ImpossibleTaggedShapes_ReportCompilerDiagnostics(string first, string union, string message)
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            [SunderRpcService(SunderRpcExposure.Package)]
            public sealed partial class InvalidService
            {
                [SunderRpcMethod] public ValueTask<First> ConcreteAsync(First request) => ValueTask.FromResult(request);
                [SunderRpcMethod] public ValueTask<Value> EchoAsync(Value request) => ValueTask.FromResult(request);
            }
            [SunderRpcUnion(typeof(First), typeof(Second)), SunderRpcDiscriminator("kind")]
            {{union}}
            [SunderRpcVariant("first")]
            {{first}}
            [SunderRpcVariant("second")]
            public sealed record Second : Value;
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            "example.unions", "1.0.0", [new("Invalid.cs", source)], References()));
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("Invalid.cs", diagnostic.Path);
        Assert.StartsWith("rpc-authoring.", diagnostic.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousSourceRecords_StayRejectedByTheFrozenDescriptorProfile()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            [SunderRpcService(SunderRpcExposure.Package)]
            public sealed partial class InvalidService
            {
                [SunderRpcMethod] public ValueTask<Value> EchoAsync(Value request) => ValueTask.FromResult(request);
            }
            [SunderRpcUnion(typeof(First), typeof(Second))]
            public abstract record Value;
            public sealed record First : Value { public required bool Flag { get; init; } }
            public sealed record Second : Value { public required bool Flag { get; init; } }
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            "example.unions", "1.0.0", [new("Ambiguous.cs", source)], References()));
        Assert.False(result.Success);
        Assert.Equal("rpc-authoring.value-invalid", Assert.Single(result.Diagnostics).Id);
    }

    private static JsonObject Tagged(string tag) => new() { [Discriminator] = tag };

    private static JsonObject Payload()
    {
        var value = Tagged("case");
        value["flag"] = false;
        value["count"] = 0;
        value["priority"] = 0;
        value["text"] = null;
        value[EscapedMember] = false;
        return value;
    }

    private static JsonObject Branch()
    {
        var value = Tagged(BranchTag);
        value["children"] = new JsonArray();
        value["byKey"] = new JsonObject();
        value["child"] = null;
        value["choice"] = JsonNode.Parse("""{"kind":"wrapped","values":{}}""");
        return value;
    }

    private static JsonObject ConcreteNode()
    {
        var value = Tagged("node");
        value["children"] = new JsonArray();
        value["byKey"] = new JsonObject();
        value["payload"] = null;
        return value;
    }

    private static void AssertJson(JsonNode expected, JsonElement actual) => AssertJson(expected.ToJsonString(), actual);

    private static void AssertJson(string expected, JsonElement actual)
        => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())),
            $"Expected {expected}; actual {actual.GetRawText()}");

    private static (SunderRpcCSharpCompiledContract Contract, string Generated) CompileContract(string sourceText = Source)
    {
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = new SunderRpcCSharpCompilerInput("example.unions", "1.0.0",
            [new("Unions.cs", sourceText), new("Partial.cs", PartialSource)], References());
        var first = compiler.Compile(input);
        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
        var contract = Assert.Single(first.Contracts);
        var generated = SunderRpcCSharpImplementationEmitter.Emit(first.Contracts);
        var second = compiler.Compile(input with
        {
            SupplementalSources = [new("authoring.g.cs", generated)],
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics));
        var verified = Assert.Single(second.Contracts);
        Assert.Equal(contract.Descriptor.GetCanonicalUtf8(), verified.Descriptor.GetCanonicalUtf8());
        Assert.Equal(contract.Descriptor.GetCanonicalAbiProjectionUtf8(), verified.Descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(Encoding.UTF8.GetBytes(generated), Encoding.UTF8.GetBytes(SunderRpcCSharpImplementationEmitter.Emit(second.Contracts)));
        return (contract, generated);
    }

    private static RuntimeProbe CompileProbe()
    {
        var (contract, generated) = CompileContract();
        var compilation = CSharpCompilation.Create("UnionCodecs_" + Guid.NewGuid().ToString("N"),
            new SunderRpcCSharpSourceFile[] { new("Unions.cs", Source), new("Partial.cs", PartialSource), new("authoring.g.cs", generated) }
                .Select(source => CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)),
            References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable, generalDiagnosticOption: ReportDiagnostic.Error));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Build");
            return new(context, assembly, contract.Descriptor);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static string[] References()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderRpcServiceAttribute).Assembly.Location)
            .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.Ordinal).ToArray();

    private sealed class RuntimeProbe : IDisposable
    {
        private readonly AssemblyLoadContext _loadContext;
        private readonly Assembly _assembly;
        private readonly SunderWorkerProviderRegistration _registration;
        private readonly SunderRpcInvocationContext _context;

        public RuntimeProbe(AssemblyLoadContext loadContext, Assembly assembly, SunderRpcContractDescriptor descriptor)
        {
            _loadContext = loadContext;
            _assembly = assembly;
            Descriptor = descriptor;
            var probe = assembly.GetType("Example.Unions.Probe")!;
            _registration = probe.GetMethod("Create")!.CreateDelegate<Func<SunderWorkerProviderRegistration>>()();
            Calls = probe.GetMethod("Calls")!.CreateDelegate<Func<int>>();
            LastVariant = probe.GetMethod("LastVariant")!.CreateDelegate<Func<string>>();
            Assert.Equal(descriptor.ContractId, _registration.ContractId);
            Assert.Equal(descriptor.Sha256, _registration.DescriptorSha256);
            Assert.Equal(descriptor.AbiSha256, _registration.AbiSha256);
            Assert.True(loadContext.IsCollectible);
            _context = (SunderRpcInvocationContext)Activator.CreateInstance(typeof(SunderRpcInvocationContext),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args:
                [
                    "example.caller", "1.0.0",
                    new SunderRpcProviderSnapshot("example.unions", "1.0.0", _registration.ProviderId,
                        descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256,
                        Guid.NewGuid(), 1, 1, new("union-endpoint"), 1, SunderRpcProviderState.Active),
                    DateTimeOffset.MaxValue, 1, CancellationToken.None, null,
                ], culture: null)!;
        }

        public SunderRpcContractDescriptor Descriptor { get; }
        public Func<int> Calls { get; }
        public Func<string> LastVariant { get; }

        public JsonElement CodecRoundTrip(string typeName, string json)
        {
            var type = _assembly.GetType("Example.Unions." + typeName)!;
            var options = (JsonSerializerOptions)_registration.Handler.GetType()
                .GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            return JsonSerializer.SerializeToElement(JsonSerializer.Deserialize(json, type, options), type, options);
        }

        public Task<JsonElement> RoundTrip(string method, JsonNode request) => RoundTrip(method, request.ToJsonString());

        public async Task<JsonElement> RoundTrip(string method, string json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
            var service = Descriptor.Services.Single();
            var descriptor = service.FindMethod(method)!;
            Assert.True(Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out var error), error);
            var result = await _registration.Handler.InvokeUnaryAsync(_context, service.ServiceId, method, document.RootElement.Clone());
            Assert.True(Descriptor.IsValid(descriptor.OutputSchemaReference, result, out error), error);
            return result;
        }

        public async Task Reject(string method, string json)
        {
            using var document = JsonDocument.Parse(json);
            var service = Descriptor.Services.Single();
            Assert.False(Descriptor.IsValid(service.FindMethod(method)!.RequestSchemaReference, document.RootElement, out _));
            await Assert.ThrowsAsync<JsonException>(() => _registration.Handler
                .InvokeUnaryAsync(_context, service.ServiceId, method, document.RootElement.Clone()).AsTask());
        }

        public Task<List<JsonElement>> Stream(string method, JsonNode request) => Stream(method, request.ToJsonString());

        public async Task<List<JsonElement>> Stream(string method, string json, bool validateRequest = true)
        {
            using var document = JsonDocument.Parse(json);
            var service = Descriptor.Services.Single();
            var descriptor = service.FindMethod(method)!;
            if (validateRequest) Assert.True(Descriptor.IsValid(descriptor.RequestSchemaReference, document.RootElement, out var requestError), requestError);
            var result = new List<JsonElement>();
            await foreach (var item in _registration.Handler.InvokeServerStreamAsync(_context, service.ServiceId, method, document.RootElement.Clone()))
            {
                Assert.True(Descriptor.IsValid(descriptor.OutputSchemaReference, item, out var error), error);
                result.Add(item);
            }
            return result;
        }

        public void Dispose() => _loadContext.Unload();
    }

    private const string PartialSource = """
        using System.Text.Json.Serialization;
        namespace Example.Unions;
        public sealed partial record Payload
        {
            [JsonPropertyName("p\t\"\\\u0001\u96ea")]
            public required bool Escaped { get; init; }
        }
        """;

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Worker;
        namespace Example.Unions;
        public static class Probe
        {
            private static readonly UnionService Service = new();
            public static SunderWorkerProviderRegistration Create() => Service.CreateSunderRpcProviderRegistration();
            public static int Calls() => Service.Calls;
            public static string LastVariant() => Service.LastVariant;
        }
        [SunderRpcService(SunderRpcExposure.Package)]
        public sealed partial class UnionService
        {
            public int Calls { get; private set; }
            public string LastVariant { get; private set; } = "";
            private ValueTask<T> Respond<T>(T value) where T : class
            {
                Calls++;
                LastVariant = value.GetType().Name;
                return ValueTask.FromResult(value);
            }
            [SunderRpcMethod] public ValueTask<Signal> EchoAsync(Signal request) => Respond(request);
            [SunderRpcMethod] public async IAsyncEnumerable<Signal> WatchAsync(Signal request)
            {
                yield return await EchoAsync(request);
                yield return new Marker();
            }
            [SunderRpcMethod] public ValueTask<Choice> ChoiceAsync(Choice request) => Respond(request);
            [SunderRpcMethod] public ValueTask<ConcreteNode> ConcreteAsync(ConcreteNode request) => Respond(request);
            [SunderRpcMethod] public async IAsyncEnumerable<ConcreteNode> ConcreteWatchAsync(ConcreteNode request)
            {
                yield return await ConcreteAsync(request);
            }
            [SunderRpcMethod] public ValueTask<Payload> ConcretePayloadAsync(Payload request) => Respond(request);
            [SunderRpcMethod] public ValueTask<Marker> ConcreteMarkerAsync(Marker request) => Respond(request);
            [SunderRpcMethod] public ValueTask<ConcreteValue> WrappedConcreteAsync(ConcreteValue request) => Respond(request);
            [SunderRpcMethod] public ValueTask<ConstructorNode> ConstructedAsync(ConstructorNode request) => Respond(request);
            [SunderRpcMethod] public async IAsyncEnumerable<ConstructorNode> ConstructedWatchAsync(ConstructorNode request)
            {
                yield return await ConstructedAsync(request);
            }
            [SunderRpcMethod] public async IAsyncEnumerable<Choice> ChoiceWatchAsync(Choice request)
            {
                yield return await ChoiceAsync(request);
            }
            [SunderRpcMethod] public ValueTask<Envelope> NestedAsync(Envelope request) => Respond(request);
            [SunderRpcMethod] public async IAsyncEnumerable<Envelope> NestedWatchAsync(Envelope request)
            {
                yield return await NestedAsync(request);
            }
            [SunderRpcMethod] public ValueTask<Signal> UnknownAsync(Signal request) => Respond<Signal>(new UnknownSignal());
            [SunderRpcMethod] public async IAsyncEnumerable<Signal> UnknownWatchAsync(Signal request)
            {
                yield return await UnknownAsync(request);
            }
            [SunderRpcMethod] public ValueTask<Choice> UnknownChoiceAsync(Choice request) => Respond<Choice>(new UnknownChoice());
            [SunderRpcMethod] public ValueTask<Signal> InvalidAsync(Signal request)
                => Respond<Signal>(new Payload { Flag = false, Count = new() { Value = 11 }, Priority = Priority.Zero, Text = null, Escaped = false });
            [SunderRpcMethod] public ValueTask<Signal> CollisionAsync(Signal request) => Respond<Signal>(new CollisionSignal());
            [SunderRpcMethod] public ValueTask<Signal> NullAsync(Signal request)
            {
                Calls++;
                return ValueTask.FromResult<Signal>(null!);
            }
            [SunderRpcMethod] public async IAsyncEnumerable<Signal> NullWatchAsync(Signal request)
            {
                yield return await NullAsync(request);
            }
            [SunderRpcMethod] public ValueTask<Signal> CycleAsync(Signal request)
            {
                var children = new List<Signal?>();
                var branch = new Branch
                {
                    Children = children, ByKey = new Dictionary<string, Signal?>(), Child = new(null),
                    Choice = new WrappedChoice { Value = new ChoiceData { Kind = "wrapped", Values = new Dictionary<string, Status>() } },
                };
                children.Add(branch);
                return Respond<Signal>(branch);
            }
        }
        [SunderRpcDefinition("WireSignal")]
        [SunderRpcUnion(typeof(Payload), typeof(Marker), typeof(Branch), typeof(CollisionSignal), typeof(ConcreteNode), typeof(ConstructorNode))]
        [SunderRpcDiscriminator("k\n\"\\\u96ea")]
        public abstract record Signal;
        [SunderRpcDefinition("WirePayload"), SunderRpcVariant("case")]
        public sealed partial record Payload : Signal
        {
            public required bool Flag { get; init; }
            public required CountValue Count { get; init; }
            public required Priority Priority { get; init; }
            public required string? Text { get; init; }
            public SunderRpcOptional<int> OptionalCount { get; init; } = new(9);
        }
        [SunderRpcVariant("Case")]
        public sealed record Marker : Signal;
        [SunderRpcVariant("\t\r\n\"\\caf\u00e9\U0001f680")]
        public sealed record Branch : Signal
        {
            public required IReadOnlyList<Signal?> Children { get; init; }
            public required IReadOnlyDictionary<string, Signal?> ByKey { get; init; }
            public SunderRpcOptional<Signal?> Child { get; init; }
            public required Choice Choice { get; init; }
            public SunderRpcOptional<Signal?> OptionalChild { get; init; } = new(new Marker());
        }
        [SunderRpcVariant("node")]
        public sealed record ConcreteNode : Signal
        {
            public required IReadOnlyList<ConcreteNode> Children { get; init; }
            public required IReadOnlyDictionary<string, ConcreteNode> ByKey { get; init; }
            public required Payload? Payload { get; init; }
            public SunderRpcOptional<ConcreteNode?> Next { get; init; } = new(null);
            public SunderRpcOptional<ConcreteNode> Peer { get; init; }
        }
        [SunderRpcTransparent]
        public sealed record ConcreteValue { public required ConcreteNode Value { get; init; } }
        [SunderRpcVariant("constructor")]
        public sealed record ConstructorNode : Signal
        {
            [JsonConstructor, System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
            public ConstructorNode(string text, IReadOnlyList<ConstructorNode> children)
            {
                Text = text;
                Children = children;
            }
            [JsonPropertyName("p\t\"\\\u0001\u96ea"), JsonPropertyOrder(-1)]
            public required string Text { get; init; }
            public required IReadOnlyList<ConstructorNode> Children { get; init; }
        }
        public sealed record UnknownSignal : Signal;
        [SunderRpcVariant("collision"), JsonConverter(typeof(CollisionConverter))]
        public sealed record CollisionSignal : Signal;
        public sealed class CollisionConverter : JsonConverter<CollisionSignal>
        {
            public override CollisionSignal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                return new();
            }
            public override void Write(Utf8JsonWriter writer, CollisionSignal value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                writer.WriteString("k\n\"\\\u96ea", "spoofed");
                writer.WriteEndObject();
            }
        }
        [SunderRpcUnion(typeof(RecordChoice), typeof(WrappedChoice))]
        public abstract record Choice;
        public sealed record RecordChoice : Choice
        {
            [SunderRpcFacet(ConstantString = "record")] public required string Kind { get; init; }
            public required Status State { get; init; }
            public required IReadOnlyList<CountValue> Values { get; init; }
        }
        [SunderRpcTransparent]
        public sealed record WrappedChoice : Choice { public required ChoiceData Value { get; init; } }
        public sealed record ChoiceData
        {
            [SunderRpcFacet(ConstantString = "wrapped")] public required string Kind { get; init; }
            public required IReadOnlyDictionary<string, Status> Values { get; init; }
        }
        public sealed record UnknownChoice : Choice;
        [SunderRpcTransparent]
        public readonly record struct CountValue
        {
            [SunderRpcFacet(Minimum = "0", Maximum = "10")] public required int Value { get; init; }
        }
        [SunderRpcNumericEnum] public enum Priority { Zero = 0, Seven = 7 }
        public enum Status
        {
            [JsonStringEnumMemberName("ready\n\"\\")] Ready,
            [JsonStringEnumMemberName("done")] Done,
        }
        public sealed record Envelope
        {
            public required IReadOnlyList<Signal?> Items { get; init; }
            public required IReadOnlyList<Signal> NonNullItems { get; init; }
            public required IReadOnlyDictionary<string, Signal?> ByKey { get; init; }
            public required Signal? Child { get; init; }
            public SunderRpcOptional<Signal?> OptionalChild { get; init; } = new(new Marker());
        }
        """;

    private const string FrozenConcreteDescriptor = """
        {
          "$defs": {
            "A": {
              "additionalProperties": false,
              "properties": {
                "kind": {"const":"a","type":"string"},
                "children": {"items":{"$ref":"#/$defs/A"},"type":"array"}
              },
              "required": ["kind","children"],
              "type": "object"
            },
            "B": {
              "additionalProperties": false,
              "properties": {"kind":{"const":"b","type":"string"}},
              "required": ["kind"],
              "type": "object"
            },
            "U": {"oneOf":[{"$ref":"#/$defs/A"},{"$ref":"#/$defs/B"}]}
          },
          "contractId": "example.unions.direct",
          "descriptorVersion": 1,
          "errors": {},
          "schemaProfile": "sunder.rpc.values.v1",
          "services": {
            "direct": {
              "methods": {
                "direct": {"errors":[],"responseSchema":{"$ref":"#/$defs/A"},"idempotency":"none","kind":"unary","requestSchema":{"$ref":"#/$defs/A"}},
                "union": {"errors":[],"responseSchema":{"$ref":"#/$defs/U"},"idempotency":"none","kind":"unary","requestSchema":{"$ref":"#/$defs/U"}}
              }
            }
          },
          "version": "1.0.0"
        }
        """;
}
