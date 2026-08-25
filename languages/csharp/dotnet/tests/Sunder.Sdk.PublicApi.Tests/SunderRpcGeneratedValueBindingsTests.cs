using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcGeneratedValueBindingsTests
{
    [Fact]
    public void ValueProfile_MapsToExactClrTypesAndGeneratedSourceCompiles()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "RpcV2Conformance",
            "valid",
            "value-profile.rpc.json")));

        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated.Values");

        Assert.Contains("public const string SchemaProfile = \"sunder.rpc.values.v1\";", source, StringComparison.Ordinal);
        Assert.Contains("public required global::System.Guid Uuid", source, StringComparison.Ordinal);
        Assert.Contains("public required global::System.DateOnly Date", source, StringComparison.Ordinal);
        Assert.Contains("public required global::Sunder.Sdk.Rpc.SunderRpcOffsetDateTime DateTime", source, StringComparison.Ordinal);
        Assert.Contains("public required global::Sunder.Sdk.Rpc.SunderRpcDuration Duration", source, StringComparison.Ordinal);
        Assert.Contains("public required global::Sunder.Sdk.Rpc.SunderRpcUri Uri", source, StringComparison.Ordinal);
        Assert.Contains("public required global::System.Numerics.BigInteger BigInteger", source, StringComparison.Ordinal);
        Assert.Contains("public required global::Sunder.Sdk.Rpc.SunderRpcDecimal Decimal", source, StringComparison.Ordinal);
        Assert.Contains("public required byte[] Bytes", source, StringComparison.Ordinal);
        Assert.Contains("public required long SafeInteger", source, StringComparison.Ordinal);
        Assert.Contains("public required float Float32", source, StringComparison.Ordinal);
        Assert.Contains("public required double Float64", source, StringComparison.Ordinal);
        Assert.Contains("public required global::System.Text.Json.JsonElement Json", source, StringComparison.Ordinal);
        Assert.Contains("public required global::System.Text.Json.JsonElement Object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class Uuid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class Decimal", source, StringComparison.Ordinal);
        AssertCompiles(source);
    }

    [Fact]
    public void NamedEnumsTaggedUnionsAndProductiveRecursion_AreTypedAndRoundTrip()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(StructuredDescriptor));
        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated.Structured");

        Assert.Contains("public enum Status", source, StringComparison.Ordinal);
        Assert.Contains("JsonStringEnumMemberName(\"in-progress\")", source, StringComparison.Ordinal);
        Assert.Contains("JsonStringEnumMemberName(\"in_progress\")", source, StringComparison.Ordinal);
        Assert.Contains("public abstract record Event", source, StringComparison.Ordinal);
        Assert.Contains("public sealed record Added : Event", source, StringComparison.Ordinal);
        Assert.Contains("public sealed record Removed : Event", source, StringComparison.Ordinal);
        Assert.Contains("public string Kind { get; } = \"added\";", source, StringComparison.Ordinal);
        Assert.Contains("public abstract record EnvelopeInlineEventUnion", source, StringComparison.Ordinal);
        Assert.Contains("SunderRpcOptional<NodeRecord?> Next", source, StringComparison.Ordinal);
        Assert.Contains("SunderRpcOptional<Event?> OptionalEvent", source, StringComparison.Ordinal);
        Assert.Contains("SunderRpcOptional<global::System.Text.Json.JsonElement> Value", source, StringComparison.Ordinal);

        var internalSource = SunderRpcCSharpGenerator.Generate(
            descriptor,
            "Example.Generated.InternalStructured",
            SunderRpcCSharpAccessibility.Internal);
        Assert.Contains("internal enum Status", internalSource, StringComparison.Ordinal);
        Assert.Contains("internal abstract record Event", internalSource, StringComparison.Ordinal);
        Assert.Contains("internal sealed record Added : Event", internalSource, StringComparison.Ordinal);
        Assert.Contains("internal abstract record EnvelopeInlineEventUnion", internalSource, StringComparison.Ordinal);
        Assert.Empty(CompileAndLoad(internalSource).GetExportedTypes());

        var assembly = CompileAndLoad(source);
        var eventType = assembly.GetType("Example.Generated.Structured.Event", throwOnError: true)!;
        var addedType = assembly.GetType("Example.Generated.Structured.Added", throwOnError: true)!;
        var statusType = assembly.GetType("Example.Generated.Structured.Status", throwOnError: true)!;
        var nodeType = assembly.GetType("Example.Generated.Structured.NodeRecord", throwOnError: true)!;
        var jsonEnvelopeType = assembly.GetType("Example.Generated.Structured.JsonEnvelope", throwOnError: true)!;
        var options = V2Options();

        var added = Activator.CreateInstance(addedType)!;
        addedType.GetProperty("Status")!.SetValue(added, Enum.Parse(statusType, "Ready"));
        var eventJson = JsonSerializer.Serialize(added, eventType, options);
        Assert.Equal("added", JsonDocument.Parse(eventJson).RootElement.GetProperty("kind").GetString());
        Assert.Equal("ready", JsonDocument.Parse(eventJson).RootElement.GetProperty("status").GetString());
        Assert.Equal(addedType, JsonSerializer.Deserialize(eventJson, eventType, options)!.GetType());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"kind\":\"unknown\"}", eventType, options));

        var child = Activator.CreateInstance(nodeType)!;
        nodeType.GetProperty("Value")!.SetValue(child, 2L);
        var root = Activator.CreateInstance(nodeType)!;
        nodeType.GetProperty("Value")!.SetValue(root, 1L);
        var nextProperty = nodeType.GetProperty("Next")!;
        nextProperty.SetValue(root, Activator.CreateInstance(nextProperty.PropertyType, child));
        var recursiveJson = JsonSerializer.Serialize(root, nodeType, options);
        var recursiveRoundTrip = JsonSerializer.Deserialize(recursiveJson, nodeType, options)!;
        var optionalNext = nextProperty.GetValue(recursiveRoundTrip)!;
        var next = nextProperty.PropertyType.GetProperty("Value")!.GetValue(optionalNext)!;
        Assert.Equal(2L, nodeType.GetProperty("Value")!.GetValue(next));

        var jsonValueProperty = jsonEnvelopeType.GetProperty("Value")!;
        var absentJson = JsonSerializer.Deserialize("{}", jsonEnvelopeType, options)!;
        var explicitNullJson = JsonSerializer.Deserialize("{\"value\":null}", jsonEnvelopeType, options)!;
        var absentState = jsonValueProperty.GetValue(absentJson)!;
        var nullState = jsonValueProperty.GetValue(explicitNullJson)!;
        Assert.False((bool)jsonValueProperty.PropertyType.GetProperty("IsPresent")!.GetValue(absentState)!);
        Assert.True((bool)jsonValueProperty.PropertyType.GetProperty("IsPresent")!.GetValue(nullState)!);
        Assert.Equal(
            JsonValueKind.Null,
            ((JsonElement)jsonValueProperty.PropertyType.GetProperty("Value")!.GetValue(nullState)!).ValueKind);
    }

    [Fact]
    public void ProductiveRecursionFixture_GeneratesRecursiveRecordsCollectionsAndMaps()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "RpcV2Conformance",
            "valid",
            "productive-recursion.rpc.json")));

        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated.Recursion");

        Assert.Contains("public sealed class Node", source, StringComparison.Ordinal);
        Assert.Contains("public Node? Next", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<Node> Children", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<string, Node>", source, StringComparison.Ordinal);
        AssertCompiles(source);
    }

    [Fact]
    public void ValueConverters_PreserveExactWireValuesAndRejectNonCanonicalForms()
    {
        var options = V2Options();
        var dateTime = new SunderRpcOffsetDateTime("2000-02-29T23:59:59.123456789-04:30");

        Assert.Equal("\"2000-02-29T23:59:59.123456789-04:30\"", JsonSerializer.Serialize(dateTime, options));
        Assert.Equal(dateTime, JsonSerializer.Deserialize<SunderRpcOffsetDateTime>(JsonSerializer.Serialize(dateTime, options), options));
        Assert.False(dateTime.TryGetDateTimeOffset(out _));
        Assert.True(new SunderRpcOffsetDateTime("2000-02-29T23:59:59.123456700-04:30").TryGetDateTimeOffset(out _));
        Assert.Equal("\"2024-02-29\"", JsonSerializer.Serialize(new DateOnly(2024, 2, 29), options));
        Assert.Equal("\"P1Y2M3DT4H5M6.25S\"", JsonSerializer.Serialize(new SunderRpcDuration("P1Y2M3DT4H5M6.25S"), options));
        Assert.Equal("\"https://example.test/a%20b?x=1\"", JsonSerializer.Serialize(new SunderRpcUri("https://example.test/a%20b?x=1"), options));
        Assert.Equal("\"123456789012345678901234567890\"", JsonSerializer.Serialize(BigInteger.Parse("123456789012345678901234567890"), options));
        Assert.Equal("\"-1000000000000000000000.125\"", JsonSerializer.Serialize(new SunderRpcDecimal("-1000000000000000000000.125"), options));
        Assert.Equal("\"AAECAw==\"", JsonSerializer.Serialize(new byte[] { 0, 1, 2, 3 }, options));
        Assert.Equal("0", JsonSerializer.Serialize(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), options));
        Assert.Equal("0", JsonSerializer.Serialize(BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)), options));
        Assert.Equal(1L, JsonSerializer.Deserialize<long>("1e0", options));

        Assert.Throws<FormatException>(() => new SunderRpcDuration("P01D"));
        Assert.Throws<FormatException>(() => new SunderRpcUri("relative/path"));
        Assert.Throws<FormatException>(() => new SunderRpcDecimal("1.230"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BigInteger>("\"01\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<byte[]>("\"AB==\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(float.PositiveInfinity, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(9_007_199_254_740_992L, options));
    }

    private static JsonSerializerOptions V2Options()
        => new(JsonSerializerDefaults.Web)
        {
            Converters = { new SunderRpcV2JsonConverterFactory() },
        };

    private static void AssertCompiles(string source) => _ = CompileAndLoad(source);

    private static Assembly CompileAndLoad(params string[] sources)
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                                ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var referencePaths = trustedAssemblies.Split(Path.PathSeparator).ToHashSet(StringComparer.Ordinal);
        referencePaths.Add(typeof(SunderRpcContractDescriptor).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "GeneratedRpcValueBindings_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))),
            referencePaths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        using var assemblyStream = new MemoryStream();
        var result = compilation.Emit(assemblyStream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        assemblyStream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
    }

    private const string StructuredDescriptor = """
        {
          "descriptorVersion":2,
          "schemaProfile":"sunder.rpc.values.v1",
          "contractId":"example.rpc.v2.generatedvalues",
          "version":"2.0.0",
          "errors":{},
          "services":{"values":{"methods":{"echo":{"kind":"unary","idempotency":"idempotent","errors":[],"requestSchema":{"$ref":"#/$defs/Envelope"},"responseSchema":{"$ref":"#/$defs/Envelope"}}}}},
          "$defs":{
            "Any":{},
            "JsonEnvelope":{"type":"object","properties":{"value":{"$ref":"#/$defs/Any"}},"required":[],"additionalProperties":false},
            "Status":{"type":"string","minLength":5,"maxLength":11,"enum":["ready","in-progress","in_progress"]},
            "Text":{"type":"string","minLength":0,"maxLength":64},
            "Node":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/NodeRecord"}]},
            "NodeRecord":{"type":"object","properties":{"value":{"type":"integer"},"next":{"$ref":"#/$defs/Node"}},"required":["value"],"additionalProperties":false},
            "Added":{"type":"object","properties":{"kind":{"type":"string","minLength":5,"maxLength":5,"const":"added"},"status":{"$ref":"#/$defs/Status"}},"required":["kind","status"],"additionalProperties":false},
            "Removed":{"type":"object","properties":{"kind":{"type":"string","minLength":7,"maxLength":7,"const":"removed"},"reason":{"$ref":"#/$defs/Text"}},"required":["kind","reason"],"additionalProperties":false},
            "Event":{"oneOf":[{"$ref":"#/$defs/Added"},{"$ref":"#/$defs/Removed"}]},
            "NullableEvent":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/Event"}]},
            "Envelope":{"type":"object","properties":{"event":{"$ref":"#/$defs/Event"},"inlineEvent":{"oneOf":[{"type":"object","properties":{"type":{"type":"string","minLength":4,"maxLength":4,"const":"left"}},"required":["type"],"additionalProperties":false},{"type":"object","properties":{"type":{"type":"string","minLength":5,"maxLength":5,"const":"right"}},"required":["type"],"additionalProperties":false}]},"node":{"$ref":"#/$defs/Node"},"optionalEvent":{"$ref":"#/$defs/NullableEvent"}},"required":["event","inlineEvent","node"],"additionalProperties":false}
          }
        }
        """;
}
