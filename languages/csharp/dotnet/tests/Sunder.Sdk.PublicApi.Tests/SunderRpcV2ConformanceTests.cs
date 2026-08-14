using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcV2ConformanceTests
{
    public static IEnumerable<object?[]> ConformanceFixtures()
    {
        var conformanceDirectory = Path.Combine(AppContext.BaseDirectory, "RpcV2Conformance");
        using var expectations = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(conformanceDirectory, "expectations.json")));
        foreach (var fixture in expectations.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            yield return
            [
                fixture.GetProperty("path").GetString()!,
                fixture.GetProperty("valid").GetBoolean(),
                OptionalString(fixture, "descriptorSha256"),
                OptionalString(fixture, "abiSha256"),
                OptionalInt32(fixture, "canonicalDescriptorBytes"),
                OptionalInt32(fixture, "canonicalAbiProjectionBytes"),
            ];
        }
    }

    [Theory]
    [MemberData(nameof(ConformanceFixtures))]
    public void V2ConformanceCorpus_MatchesValidityHashesAndCanonicalLengths(
        string relativePath,
        bool valid,
        string? descriptorSha256,
        string? abiSha256,
        int? canonicalDescriptorBytes,
        int? canonicalAbiProjectionBytes)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "RpcV2Conformance",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var descriptorUtf8 = File.ReadAllBytes(path);
        if (!valid)
        {
            Assert.Throws<SunderRpcDescriptorException>(() => SunderRpcContractDescriptor.Parse(descriptorUtf8));
            return;
        }

        var descriptor = SunderRpcContractDescriptor.Parse(descriptorUtf8);
        var abiProjection = descriptor.GetCanonicalAbiProjectionUtf8();

        Assert.Equal(SunderRpcProtocol.DescriptorVersionV2, descriptor.DescriptorVersion);
        Assert.Equal(descriptorSha256, descriptor.Sha256);
        Assert.Equal(abiSha256, descriptor.AbiSha256);
        Assert.Equal(canonicalDescriptorBytes, descriptor.GetCanonicalUtf8().Length);
        Assert.NotNull(abiProjection);
        Assert.Equal(canonicalAbiProjectionBytes, abiProjection!.Length);
    }

    [Fact]
    public void V2RuntimeValues_UseLogicalSafeIntegerAndBinary64Semantics()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(RuntimeValueDescriptor());
        (string Schema, string Json, bool Valid)[] cases =
        [
            ("Uuid", "\"00000000-0000-0000-0000-000000000000\"", true),
            ("Uuid", "\"0192c4f0-8d3c-7a61-9b3f-6a9b89f4b8d2\"", true),
            ("Uuid", "\"0192C4F0-8D3C-7A61-9B3F-6A9B89F4B8D2\"", false),
            ("Uuid", "\"{0192c4f0-8d3c-7a61-9b3f-6a9b89f4b8d2}\"", false),
            ("Instant", "\"0001-01-01T00:00:00Z\"", true),
            ("Instant", "\"2000-02-29T23:59:59.000000001Z\"", true),
            ("Instant", "\"1900-02-29T00:00:00Z\"", false),
            ("Instant", "\"2030-01-02T03:04:05.10Z\"", false),
            ("Instant", "\"2030-01-02T03:04:60Z\"", false),
            ("Integer", "9007199254740991.0", true),
            ("Integer", "1e0", true),
            ("Integer", "9007199254740991.1", false),
            ("Integer", "9007199254740992", false),
            ("Integer", "1e-9999", false),
            ("Number", "1.5", true),
            ("Number", "-0", true),
            ("Number", "1e-9999", true),
            ("Number", "1e309", false),
            ("NumberEnum", "1.0000000000000001", true),
            ("NumberEnum", "1.0000000000000002", false),
            ("Content", ValidContentReference(), true),
            ("Content", ValidContentReference().Replace("}", ",\"extra\":true}", StringComparison.Ordinal), false),
            ("Content", ValidContentReference().Replace(new string('0', 64), new string('A', 64), StringComparison.Ordinal), false),
            ("Content", ValidContentReference().Replace("9007199254740991.0", "9007199254740992", StringComparison.Ordinal), false),
        ];

        foreach (var (schema, json, valid) in cases)
        {
            using var value = JsonDocument.Parse(json);
            var actual = descriptor.IsValid("#/$defs/" + schema, value.RootElement, out var error);
            Assert.True(actual == valid, $"{schema} with {json}: expected {valid}, got {actual}: {error}");
        }
    }

    [Fact]
    public void V2DescriptorCaseCollisionFold_IsAsciiOnly()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "contractId":"example.rpc.v2.casefold",
              "version":"2.0.0",
              "errors":{},
              "services":{"example":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Value"},"responseSchema":{"$ref":"#/$defs/Value"}}}}},
              "$defs":{"Value":{"type":"object","properties":{"K":{"type":"boolean"},"K":{"type":"boolean"}},"required":["K","K"],"additionalProperties":false}}
            }
            """));
        using var value = JsonDocument.Parse("""{"K":true,"K":false}""");

        Assert.True(descriptor.IsValid("#/$defs/Value", value.RootElement, out var error), error);

        var collision = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8())
            .Replace("\"K\"", "\"k\"", StringComparison.Ordinal);
        Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(collision)));

        var unpairedSurrogate = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8())
            .Replace("\"type\":\"object\"", "\"description\":\"\\uD800\",\"type\":\"object\"", StringComparison.Ordinal);
        Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(unpairedSurrogate)));
    }

    [Fact]
    public void V2ResolvedSchemaDepth_IsIndependentOfDefinitionOrder()
    {
        foreach (var reverseDefinitions in new[] { false, true })
        {
            var valid = SunderRpcContractDescriptor.Parse(
                ReferenceChainDescriptor(schemaCount: 64, reverseDefinitions));
            Assert.Equal(SunderRpcProtocol.DescriptorVersionV2, valid.DescriptorVersion);

            var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
                SunderRpcContractDescriptor.Parse(
                    ReferenceChainDescriptor(schemaCount: 65, reverseDefinitions)));
            Assert.Contains("depth", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void V2SchemaNodeLimit_RemainsStructural()
    {
        var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(WideSchemaDescriptor(definitionCount: 16, propertiesPerDefinition: 256)));

        Assert.Contains("node-count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V2NullableUnion_RejectsMultipleRecursivelyNullAcceptingBranches()
    {
        var descriptor = Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "contractId":"example.rpc.v2.nestednullable",
              "version":"2.0.0",
              "errors":{},
              "services":{"example":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Ambiguous"},"responseSchema":{"$ref":"#/$defs/Text"}}}}},
              "$defs":{
                "Null":{"type":"null"},
                "Text":{"type":"string","minLength":0,"maxLength":32},
                "NullableText":{"oneOf":[{"$ref":"#/$defs/Null"},{"$ref":"#/$defs/Text"}]},
                "NullableAlias":{"$ref":"#/$defs/NullableText"},
                "Ambiguous":{"oneOf":[{"$ref":"#/$defs/NullableAlias"},{"$ref":"#/$defs/Null"}]}
              }
            }
            """);

        var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(descriptor));
        Assert.Contains("exactly one branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V2RuntimeValues_AcceptCollectionsBeyondTheFormerInstanceNodeCap()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(RuntimeValueDescriptor());
        var value = JsonSerializer.SerializeToElement(Enumerable.Repeat(false, 100_001));

        Assert.True(descriptor.IsValid("#/$defs/LargeArray", value, out var error), error);
    }

    private static string? OptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int? OptionalInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetInt32() : null;

    private static string ValidContentReference()
        => """
           {"id":"opaque","length":9007199254740991.0,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","mediaType":"application/octet-stream","fileName":"display-name.bin","expiresAtUtc":"2030-01-02T03:04:05Z","repeatability":"single-use"}
           """;

    private static byte[] RuntimeValueDescriptor()
        => Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "contractId":"example.rpc.v2.values",
              "version":"2.0.0",
              "errors":{},
              "services":{"example":{"methods":{"check":{"kind":"unary","idempotency":"idempotent","errors":[],"requestSchema":{"$ref":"#/$defs/Empty"},"responseSchema":{"$ref":"#/$defs/Empty"}}}}},
              "$defs":{
                "Empty":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Uuid":{"type":"string","logicalType":"uuid"},
                "Instant":{"type":"string","logicalType":"utc-date-time"},
                "Content":{"type":"object","logicalType":"content-reference"},
                "Integer":{"type":"integer","minimum":-9007199254740991,"maximum":9007199254740991},
                "Number":{"type":"number","minimum":-9007199254740991,"maximum":9007199254740991},
                "NumberEnum":{"type":"number","minimum":0,"maximum":2,"enum":[1]},
                "LargeArray":{"type":"array","items":{"type":"boolean"},"minItems":0,"maxItems":1000000}
              }
            }
            """);

    private static byte[] ReferenceChainDescriptor(int schemaCount, bool reverseDefinitions)
    {
        var definitions = Enumerable.Range(0, schemaCount);
        if (reverseDefinitions)
        {
            definitions = definitions.Reverse();
        }
        var definitionJson = string.Join(
            ',',
            definitions.Select(index => index == schemaCount - 1
                ? $"\"D{index}\":{{\"type\":\"boolean\"}}"
                : $"\"D{index}\":{{\"$ref\":\"#/$defs/D{index + 1}\"}}"));
        var descriptor = """
            {
              "descriptorVersion":2,
              "contractId":"example.rpc.v2.depth",
              "version":"2.0.0",
              "errors":{},
              "services":{"depth":{"methods":{"check":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/D0"},"responseSchema":{"$ref":"#/$defs/D0"}}}}},
              "$defs":{ __DEFINITIONS__ }
            }
            """.Replace("__DEFINITIONS__", definitionJson, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(descriptor);
    }

    private static byte[] WideSchemaDescriptor(int definitionCount, int propertiesPerDefinition)
    {
        var properties = string.Join(
            ',',
            Enumerable.Range(0, propertiesPerDefinition)
                .Select(index => $"\"p{index}\":{{\"type\":\"boolean\"}}"));
        var definitions = string.Join(
            ',',
            Enumerable.Range(0, definitionCount)
                .Select(index => $"\"Wide{index}\":{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[],\"additionalProperties\":false}}"));
        var descriptor = """
            {
              "descriptorVersion":2,
              "contractId":"example.rpc.v2.nodes",
              "version":"2.0.0",
              "errors":{},
              "services":{"nodes":{"methods":{"check":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Wide0"},"responseSchema":{"$ref":"#/$defs/Wide0"}}}}},
              "$defs":{ __DEFINITIONS__ }
            }
            """.Replace("__DEFINITIONS__", definitions, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(descriptor);
    }
}
