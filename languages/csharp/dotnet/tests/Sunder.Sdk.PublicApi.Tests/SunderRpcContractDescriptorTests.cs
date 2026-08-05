using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcContractDescriptorTests
{
    [Fact]
    public void Parse_ValidatesSchemasAndCanonicalizesDeterministically()
    {
        var first = SunderRpcContractDescriptor.Parse(Descriptor());
        var second = SunderRpcContractDescriptor.Parse(Descriptor(reordered: true));

        Assert.Equal("example.messages", first.ContractId);
        Assert.Equal("1.2.3", first.Version);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.GetCanonicalUtf8(), second.GetCanonicalUtf8());
        Assert.Equal("send", Assert.Single(Assert.Single(first.Services).Methods).MethodId);

        using var valid = JsonDocument.Parse("""{"message":"hello"}""");
        using var missing = JsonDocument.Parse("{}");
        Assert.True(first.IsValid("#/$defs/Request", valid.RootElement, out var validError), validError);
        Assert.False(first.IsValid("#/$defs/Request", missing.RootElement, out var missingError));
        Assert.Contains("required", missingError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\uFEFF{")]
    [InlineData("{\"descriptorVersion\":1,\"DescriptorVersion\":1}")]
    [InlineData("{\"descriptorVersion\":1,\"descriptorVersion\":1}")]
    public void Parse_RejectsNonStrictJson(string json)
        => Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Parse_RejectsOpenUnboundedAndUnsupportedSchemas()
    {
        var descriptor = Encoding.UTF8.GetString(Descriptor());
        AssertRejected(descriptor.Replace(
            "\"additionalProperties\":false",
            "\"additionalProperties\":true",
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            ",\"maxLength\":128",
            string.Empty,
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            "\"maxLength\":128",
            "\"maxLength\":128,\"pattern\":\".*\"",
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            "#/$defs/Request",
            "https://example.invalid/request.json",
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            "\"descriptorVersion\":1",
            "\"descriptorVersion\":1.0",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsAmbiguousUntaggedOneOf()
    {
        var descriptor = Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "contractId":"example.union",
              "version":"1.0.0",
              "services":[{"serviceId":"union","methods":[{"methodId":"read","kind":"unary","requestSchema":{"$ref":"#/$defs/Union"},"responseSchema":{"$ref":"#/$defs/A"}}]}],
              "$defs":{
                "Union":{"oneOf":[{"$ref":"#/$defs/A"},{"$ref":"#/$defs/B"}]},
                "A":{"type":"object","properties":{"value":{"type":"string","minLength":0,"maxLength":10}},"required":["value"],"additionalProperties":false},
                "B":{"type":"object","properties":{"other":{"type":"string","minLength":0,"maxLength":10}},"required":["other"],"additionalProperties":false}
              }
            }
            """);

        var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(descriptor));
        Assert.Contains("discriminator", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_IsDeterministicAndEmitsBindingScaffolding()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Descriptor());

        var first = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated");
        var second = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated");

        Assert.Equal(first, second);
        Assert.Contains("using System.Collections.Generic;", first, StringComparison.Ordinal);
        Assert.Contains("using System.Threading.Tasks;", first, StringComparison.Ordinal);
        Assert.Contains("public sealed class Request", first, StringComparison.Ordinal);
        Assert.Contains("public interface IMessagesProvider", first, StringComparison.Ordinal);
        Assert.Contains("public sealed class MessagesClient", first, StringComparison.Ordinal);
        Assert.Contains("SunderRpcInvocationContext context", first, StringComparison.Ordinal);
        Assert.Contains("ISunderRpcCallScope scope", first, StringComparison.Ordinal);
        Assert.Contains("public sealed class ExampleMessagesRpcHandler", first, StringComparison.Ordinal);

        var taggedUnion = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "contractId":"example.union",
              "version":"1.0.0",
              "services":[{"serviceId":"union","methods":[{"methodId":"read","kind":"unary","requestSchema":{"$ref":"#/$defs/Command"},"responseSchema":{"$ref":"#/$defs/Response"}}]}],
              "$defs":{
                "Command":{"oneOf":[{"$ref":"#/$defs/Create"},{"$ref":"#/$defs/Delete"}]},
                "Create":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"create"}},"required":["kind"],"additionalProperties":false},
                "Delete":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"delete"}},"required":["kind"],"additionalProperties":false},
                "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
              }
            }
            """));
        var taggedUnionSource = SunderRpcCSharpGenerator.Generate(taggedUnion, "Example.Generated");
        Assert.Contains(
            "ValueTask<Response> ReadAsync(JsonElement request",
            taggedUnionSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("class Command", taggedUnionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_CompilesMapsOmittedRequiredCollisionsKeywordsAndControlLiterals()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "contractId":"example.generator",
              "version":"1.0.0",
              "services":{"foo-bar":{"methods":{
                "do-it":{"kind":"unary","requestSchema":"#/$defs/foo-bar","responseSchema":"#/$defs/foo_bar"},
                "do_it":{"kind":"server-stream","requestSchema":"#/$defs/StringMap","eventSchema":"#/$defs/Empty"}
              }}},
              "$defs":{
                "Empty":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "StringMap":{"type":"object","properties":{},"additionalProperties":{"type":"string","minLength":0,"maxLength":8},"propertyNames":{"type":"string","minLength":1,"maxLength":16},"minProperties":0,"maxProperties":4},
                "foo-bar":{"type":"object","properties":{"class":{"type":"string","minLength":0,"maxLength":8},"foo-bar":{"type":"integer","minimum":0,"maximum":8},"foo_bar":{"type":"boolean"},"line\u0001break":{"type":"string","minLength":0,"maxLength":8},"map":{"$ref":"#/$defs/StringMap"}},"required":["class","foo-bar","foo_bar","line\u0001break","map"],"additionalProperties":false},
                "foo_bar":{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}
              }
            }
            """));

        var source = SunderRpcCSharpGenerator.Generate(descriptor, "namespace.Generated");

        Assert.Contains("namespace @namespace.Generated;", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<string, string>", source, StringComparison.Ordinal);
        Assert.Contains("public sealed class FooBar2", source, StringComparison.Ordinal);
        Assert.Contains("DoItAsync2", source, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"line\\u0001break\")", source, StringComparison.Ordinal);
        AssertCompiles(source);
    }

    [Fact]
    public void RestrictedProfile_ValidatesTaggedUnionMapsArraysNumbersBooleanAndNull()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "contractId":"example.profile",
              "version":"1.0.0",
              "services":[{"serviceId":"profile","methods":[{"methodId":"apply","kind":"unary","requestSchema":{"$ref":"#/$defs/Command"},"responseSchema":{"$ref":"#/$defs/Details"}}]}],
              "$defs":{
                "Command":{"oneOf":[{"$ref":"#/$defs/Create"},{"$ref":"#/$defs/Delete"}]},
                "Create":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"create"},"name":{"type":"string","minLength":1,"maxLength":32}},"required":["kind","name"],"additionalProperties":false},
                "Delete":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"delete"},"name":{"type":"string","minLength":1,"maxLength":32}},"required":["kind","name"],"additionalProperties":false},
                "Details":{"type":"object","properties":{"count":{"type":"integer","minimum":0,"maximum":100},"score":{"type":"number","minimum":0,"maximum":100},"enabled":{"type":"boolean"},"nothing":{"type":"null"},"flags":{"type":"array","items":{"type":"boolean"},"minItems":0,"maxItems":4},"labels":{"type":"object","additionalProperties":{"type":"string","minLength":0,"maxLength":20},"propertyNames":{"type":"string","minLength":1,"maxLength":20},"minProperties":0,"maxProperties":4}},"required":["count","score","enabled","nothing","flags","labels"],"additionalProperties":false}
              }
            }
            """));
        using var command = JsonDocument.Parse("""{"kind":"create","name":"test"}""");
        using var details = JsonDocument.Parse("""{"count":1,"score":1.5,"enabled":true,"nothing":null,"flags":[true],"labels":{"a":"b"}}""");
        using var wrongTag = JsonDocument.Parse("""{"kind":"CREATE","name":"test"}""");

        Assert.True(descriptor.IsValid("#/$defs/Command", command.RootElement, out var commandError), commandError);
        Assert.True(descriptor.IsValid("#/$defs/Details", details.RootElement, out var detailsError), detailsError);
        Assert.False(descriptor.IsValid("#/$defs/Command", wrongTag.RootElement, out _));
    }

    [Fact]
    public void RestrictedProfile_ValidatesNullableValuesAndGeneratorEmitsNullableTypes()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "contractId":"example.nullable",
              "version":"1.0.0",
              "services":[{"serviceId":"nullable","methods":[{"methodId":"read","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Request"}}]}],
              "$defs":{
                "NullableText":{"oneOf":[{"type":"null"},{"type":"string","minLength":0,"maxLength":20}]},
                "Request":{"type":"object","properties":{"value":{"$ref":"#/$defs/NullableText"}},"required":["value"],"additionalProperties":false}
              }
            }
            """));
        using var nullValue = JsonDocument.Parse("""{"value":null}""");
        using var textValue = JsonDocument.Parse("""{"value":"test"}""");

        Assert.True(descriptor.IsValid("#/$defs/Request", nullValue.RootElement, out var nullError), nullError);
        Assert.True(descriptor.IsValid("#/$defs/Request", textValue.RootElement, out var textError), textError);
        Assert.Contains(
            "public required string? Value { get; init; }",
            SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated"),
            StringComparison.Ordinal);
    }

    private static void AssertCompiles(string source)
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                                ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var referencePaths = trustedAssemblies.Split(Path.PathSeparator).ToHashSet(StringComparer.Ordinal);
        referencePaths.Add(typeof(SunderRpcContractDescriptor).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "GeneratedRpcBindings",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            referencePaths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
    }

    private static void AssertRejected(string json)
        => Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json)));

    private static byte[] Descriptor(bool reordered = false)
    {
        var json = reordered
            ? """
              {"version":"1.2.3","services":[{"methods":[{"responseSchema":{"$ref":"#/$defs/Response"},"requestSchema":{"$ref":"#/$defs/Request"},"kind":"unary","methodId":"send"}],"serviceId":"messages"}],"contractId":"example.messages","descriptorVersion":1,"$defs":{"Response":{"required":["accepted"],"properties":{"accepted":{"type":"boolean"}},"additionalProperties":false,"type":"object"},"Request":{"required":["message"],"additionalProperties":false,"properties":{"message":{"maxLength":128,"minLength":1,"type":"string"}},"type":"object"}}}
              """
            : """
              {"descriptorVersion":1,"contractId":"example.messages","version":"1.2.3","services":[{"serviceId":"messages","methods":[{"methodId":"send","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}}]}],"$defs":{"Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},"Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}}}
              """;
        return Encoding.UTF8.GetBytes(json);
    }
}
