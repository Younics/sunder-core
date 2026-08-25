using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                fixture.GetProperty("schemaValid").GetBoolean(),
                OptionalString(fixture, "errorCode"),
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
        bool schemaValid,
        string? errorCode,
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
        using var descriptorDocument = JsonDocument.Parse(descriptorUtf8);
        using var schemaDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "RpcV2Conformance",
            "descriptor.schema.json")));
        Assert.Equal(
            schemaValid,
            IsJsonSchemaValid(schemaDocument.RootElement, descriptorDocument.RootElement, schemaDocument.RootElement));
        if (!valid)
        {
            var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
                SunderRpcContractDescriptor.Parse(descriptorUtf8));
            Assert.Equal(errorCode, exception.ErrorCode);
            return;
        }

        var descriptor = SunderRpcContractDescriptor.Parse(descriptorUtf8);
        var abiProjection = descriptor.GetCanonicalAbiProjectionUtf8();

        Assert.Equal(SunderRpcProtocol.DescriptorVersionV2, descriptor.DescriptorVersion);
        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, descriptor.SchemaProfile);
        Assert.Equal(descriptorSha256, descriptor.Sha256);
        Assert.Equal(abiSha256, descriptor.AbiSha256);
        Assert.Equal(canonicalDescriptorBytes, descriptor.GetCanonicalUtf8().Length);
        Assert.NotNull(abiProjection);
        Assert.Equal(canonicalAbiProjectionBytes, abiProjection!.Length);
    }

    [Fact]
    public void V2RuntimeValues_UseAssertedExactAndSelectedFloatSemantics()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(RuntimeValueDescriptor());
        (string Schema, string Json, bool Valid)[] cases =
        [
            ("Uuid", "\"00000000-0000-0000-0000-000000000000\"", true),
            ("Uuid", "\"0192c4f0-8d3c-7a61-9b3f-6a9b89f4b8d2\"", true),
            ("Uuid", "\"0192C4F0-8D3C-7A61-9B3F-6A9B89F4B8D2\"", false),
            ("Uuid", "\"{0192c4f0-8d3c-7a61-9b3f-6a9b89f4b8d2}\"", false),
            ("Date", "\"2000-02-29\"", true),
            ("Date", "\"1900-02-29\"", false),
            ("Instant", "\"0001-01-01T00:00:00Z\"", true),
            ("Instant", "\"2000-02-29T23:59:59.000000001Z\"", true),
            ("Instant", "\"2030-01-02T03:04:05.10+05:30\"", true),
            ("Instant", "\"2030-01-02T03:04:05+14:01\"", false),
            ("Instant", "\"2030-01-02T03:04:05.1234567890Z\"", false),
            ("Instant", "\"1900-02-29T00:00:00Z\"", false),
            ("Instant", "\"2030-01-02T03:04:05\"", false),
            ("Instant", "\"2030-01-02T03:04:60Z\"", false),
            ("Duration", "\"P1Y2M3DT4H5M6.7S\"", true),
            ("Duration", "\"P01D\"", false),
            ("Duration", "\"PT1.20S\"", false),
            ("Uri", "\"https://example.test/a%20b\"", true),
            ("Uri", "\"relative/path\"", false),
            ("Uri", "\"https://example.test/%ZZ\"", false),
            ("Uri", "\"https://example.test/\\u007f\"", false),
            ("BigInteger", "\"9999999999999999999999999999999999999999\"", true),
            ("BigInteger", "\"01\"", false),
            ("BigInteger", "\"-0\"", false),
            ("BigInteger", "\"10000000000000000000000000000000000000000\"", false),
            ("Decimal", "\"-999.00000001\"", true),
            ("Decimal", "\"1.20\"", false),
            ("Decimal", "\"1000.125\"", false),
            ("Decimal", "\"0.000000001\"", false),
            ("Bytes", "\"AQI=\"", true),
            ("Bytes", "\"AR==\"", false),
            ("Bytes", "\"AQ\"", false),
            ("Bytes", "\"AQIDBAUGBwgJ\"", false),
            ("Integer", "9007199254740991.0", true),
            ("Integer", "1e0", true),
            ("Integer", "9007199254740991.1", false),
            ("Integer", "9007199254740992", false),
            ("Integer", "1e-9999", false),
            ("Float32", "1.5", true),
            ("Float32", "1e-9999", true),
            ("Float32", "1e39", false),
            ("Float32Enum", "1.00000005", true),
            ("Float32Enum", "1.0000001", false),
            ("Float64", "1.5", true),
            ("Float64", "-0", true),
            ("Float64", "1e309", false),
            ("Float64Enum", "1.0000000000000001", true),
            ("Float64Enum", "1.0000000000000002", false),
            ("JSON", "[null,true,\"text\",1.5]", true),
            ("JSON", "1e400", false),
            ("JSONObject", "{\"A\":1,\"a\":2}", true),
            ("JSONObject", "{\"a\":1,\"b\":2,\"c\":3}", false),
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
    [Trait("Category", "Conformance")]
    [Trait("Size", "Small")]
    public void V2RuntimeInstanceCorpus_IsExecutedByDotNet()
        => VerifyRuntimeInstanceCorpus(mutateFirstExpectation: false);

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Size", "Small")]
    public void V2RuntimeInstanceCorpus_OracleRejectsSeededExpectationMutation()
        => Assert.ThrowsAny<Exception>(() => VerifyRuntimeInstanceCorpus(mutateFirstExpectation: true));

    [Fact]
    public void V2DescriptorCaseCollisionFold_IsAsciiOnly()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
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
              "schemaProfile":"sunder.rpc.values.v1",
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

    [Fact]
    public void V2OptionalInstanceFacets_DefaultToZeroMinimumAndUnboundedMaximum()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(RelaxedFacetDescriptor());
        var longText = JsonSerializer.SerializeToElement(new string('x', 1_000_001));
        var largeArray = JsonSerializer.SerializeToElement(Enumerable.Repeat(false, 1_000_001));
        var largeBase64 = JsonSerializer.SerializeToElement(string.Concat(Enumerable.Repeat("AAAA", 333_334)));
        var largeExactText = new string('9', 1_000_001);
        var largeInteger = JsonSerializer.SerializeToElement(largeExactText);
        using var emptyKeyMap = JsonDocument.Parse("{\"\":true}");

        Assert.True(descriptor.IsValid("#/$defs/Text", longText, out var textError), textError);
        Assert.True(descriptor.IsValid("#/$defs/LargeTextBound", longText, out var boundedTextError), boundedTextError);
        Assert.True(descriptor.IsValid("#/$defs/Values", largeArray, out var arrayError), arrayError);
        Assert.True(descriptor.IsValid("#/$defs/StringMap", emptyKeyMap.RootElement, out var mapError), mapError);
        Assert.True(descriptor.IsValid("#/$defs/Bytes", largeBase64, out var base64Error), base64Error);
        Assert.True(descriptor.IsValid("#/$defs/BigInteger", largeInteger, out var integerError), integerError);
        Assert.True(SunderRpcDecimal.TryParse(largeExactText, out _));
        Assert.True(
            descriptor.IsValid("#/$defs/Decimal", JsonSerializer.SerializeToElement("1234567890.123456789"), out var decimalError),
            decimalError);
    }

    [Fact]
    public void V2InstanceValidation_HasNoSchemaDepthLimit()
    {
        const int depth = 512;
        var descriptor = SunderRpcContractDescriptor.Parse(RelaxedFacetDescriptor());
        using var value = JsonDocument.Parse(
            new string('[', depth) + new string(']', depth),
            new JsonDocumentOptions { MaxDepth = depth + 1 });

        Assert.True(descriptor.IsValid("#/$defs/RecursiveArray", value.RootElement, out var recursiveError), recursiveError);
        Assert.True(descriptor.IsValid("#/$defs/Any", value.RootElement, out var jsonError), jsonError);
    }

    [Theory]
    [InlineData("{\"type\":\"string\",\"minLength\":2,\"maxLength\":1}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"boolean\"},\"minItems\":2,\"maxItems\":1}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"boolean\"},\"propertyNames\":{\"type\":\"string\"},\"minProperties\":2,\"maxProperties\":1}")]
    public void V2ExplicitInstanceMinimum_MustNotExceedMaximum(string schema)
    {
        var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(SingleSchemaDescriptor(schema)));

        Assert.Contains("must not exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V2Canonicalization_UsesRfc8785FiniteNumberForms()
    {
        var source = """
            {
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc.v2.canonicalnumbers",
              "version":"2.0.0",
              "errors":{},
              "services":{"numbers":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Unit"},"responseSchema":{"$ref":"#/$defs/Unit"}}}}},
              "$defs":{
                "Unit":{"type":"object","properties":{},"required":[],"additionalProperties":false,"maxProperties":2000000},
                "Fixed":{"type":"number","format":"float64","minimum":1.0,"maximum":1e20},
                "Scientific":{"type":"number","format":"float64","minimum":1e-7,"maximum":0.000001},
                "Rounded":{"type":"number","format":"float64","const":333333333.33333329},
                "Large":{"type":"number","format":"float64","const":1e30},
                "Zero":{"type":"number","format":"float32","const":-0.0}
              }
            }
            """;
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(source));
        var canonical = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8());

        Assert.Contains("\"maximum\":100000000000000000000", canonical, StringComparison.Ordinal);
        Assert.Contains("\"minimum\":1", canonical, StringComparison.Ordinal);
        Assert.Contains("\"minimum\":1e-7", canonical, StringComparison.Ordinal);
        Assert.Contains("\"maximum\":0.000001", canonical, StringComparison.Ordinal);
        Assert.Contains("\"const\":333333333.3333333", canonical, StringComparison.Ordinal);
        Assert.Contains("\"const\":1e+30", canonical, StringComparison.Ordinal);
        Assert.Contains("\"const\":0", canonical, StringComparison.Ordinal);

        var canonicalSource = source
            .Replace("\"minimum\":1.0", "\"minimum\":1", StringComparison.Ordinal)
            .Replace("\"maximum\":1e20", "\"maximum\":100000000000000000000", StringComparison.Ordinal)
            .Replace("333333333.33333329", "333333333.3333333", StringComparison.Ordinal)
            .Replace("-0.0", "0", StringComparison.Ordinal);
        var equivalent = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(canonicalSource));
        Assert.Equal(descriptor.Sha256, equivalent.Sha256);
        Assert.Equal(descriptor.GetCanonicalUtf8(), equivalent.GetCanonicalUtf8());
    }

    [Fact]
    public void V2AbiProjection_RetainsSchemaProfileAndErrorDetailsReference()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "RpcV2Conformance",
            "valid",
            "complete.rpc.json");
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(path));
        var projection = Encoding.UTF8.GetString(descriptor.GetCanonicalAbiProjectionUtf8()!);

        Assert.Equal(SunderRpcProtocol.ValueSchemaProfile, descriptor.SchemaProfile);
        Assert.Contains("\"schemaProfile\":\"sunder.rpc.values.v1\"", projection, StringComparison.Ordinal);
        Assert.Contains(
            "\"detailsSchema\":{\"$ref\":\"#/$defs/ErrorDetails\"}",
            projection,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"description\"", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void V2ProductiveRecursion_ValidatesFiniteValues()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "RpcV2Conformance",
            "valid",
            "productive-recursion.rpc.json");
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(path));
        using var node = JsonDocument.Parse(
            """{"value":"root","children":[{"value":"leaf","children":[]}]}""");
        using var emptyMap = JsonDocument.Parse("{}");

        Assert.True(descriptor.IsValid("#/$defs/Node", node.RootElement, out var nodeError), nodeError);
        Assert.True(descriptor.IsValid("#/$defs/NodeMap", emptyMap.RootElement, out var mapError), mapError);
    }

    private static string? OptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int? OptionalInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetInt32() : null;

    private static void VerifyRuntimeInstanceCorpus(bool mutateFirstExpectation)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "RpcV2Conformance");
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "instances.json")));
        Assert.Equal(1, corpus.RootElement.GetProperty("formatVersion").GetInt32());
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
            root,
            corpus.RootElement.GetProperty("descriptor").GetString()!
                .Replace('/', Path.DirectorySeparatorChar))));
        var index = 0;
        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var expected = item.GetProperty("valid").GetBoolean();
            if (mutateFirstExpectation && index == 0) expected = !expected;
            var actual = descriptor.IsValid(
                "#/$defs/" + item.GetProperty("schema").GetString(),
                item.GetProperty("value"),
                out var error);
            Assert.True(
                actual == expected,
                $"{item.GetProperty("name").GetString()}: expected {expected}, got {actual}: {error}");
            index++;
        }
        Assert.True(index >= 20, "The shared runtime instance corpus lost its boundary coverage.");
    }

    private static bool IsJsonSchemaValid(JsonElement schema, JsonElement instance, JsonElement rootSchema)
    {
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/$defs/";
            var value = reference.GetString()!;
            return value.StartsWith(prefix, StringComparison.Ordinal)
                   && rootSchema.GetProperty("$defs").TryGetProperty(value[prefix.Length..], out var target)
                   && IsJsonSchemaValid(target, instance, rootSchema);
        }
        if (schema.TryGetProperty("type", out var type) && !MatchesJsonSchemaType(type.GetString()!, instance))
        {
            return false;
        }
        if (schema.TryGetProperty("const", out var constant)
            && !JsonElement.DeepEquals(constant, instance))
        {
            return false;
        }
        if (schema.TryGetProperty("enum", out var enumValues)
            && !enumValues.EnumerateArray().Any(value => JsonElement.DeepEquals(value, instance)))
        {
            return false;
        }
        if (schema.TryGetProperty("allOf", out var allOf)
            && allOf.EnumerateArray().Any(item => !IsJsonSchemaValid(item, instance, rootSchema)))
        {
            return false;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf)
            && oneOf.EnumerateArray().Count(item => IsJsonSchemaValid(item, instance, rootSchema)) != 1)
        {
            return false;
        }
        if (schema.TryGetProperty("not", out var not)
            && IsJsonSchemaValid(not, instance, rootSchema))
        {
            return false;
        }
        if (schema.TryGetProperty("if", out var condition)
            && IsJsonSchemaValid(condition, instance, rootSchema)
            && schema.TryGetProperty("then", out var consequence)
            && !IsJsonSchemaValid(consequence, instance, rootSchema))
        {
            return false;
        }

        return instance.ValueKind switch
        {
            JsonValueKind.Object => IsJsonSchemaObjectValid(schema, instance, rootSchema),
            JsonValueKind.Array => IsJsonSchemaArrayValid(schema, instance, rootSchema),
            JsonValueKind.String => IsJsonSchemaStringValid(schema, instance),
            JsonValueKind.Number => IsJsonSchemaNumberValid(schema, instance),
            _ => true,
        };
    }

    private static bool IsJsonSchemaObjectValid(
        JsonElement schema,
        JsonElement instance,
        JsonElement rootSchema)
    {
        var properties = instance.EnumerateObject().ToArray();
        if (schema.TryGetProperty("minProperties", out var minimum)
            && properties.Length < minimum.GetInt32()
            || schema.TryGetProperty("maxProperties", out var maximum)
            && properties.Length > maximum.GetInt32())
        {
            return false;
        }
        if (schema.TryGetProperty("required", out var required)
            && required.EnumerateArray().Any(name => !instance.TryGetProperty(name.GetString()!, out _)))
        {
            return false;
        }

        var hasDeclaredProperties = schema.TryGetProperty("properties", out var declaredProperties);
        foreach (var property in properties)
        {
            if (schema.TryGetProperty("propertyNames", out var propertyNameSchema))
            {
                using var propertyName = JsonDocument.Parse(JsonSerializer.Serialize(property.Name));
                if (!IsJsonSchemaValid(propertyNameSchema, propertyName.RootElement, rootSchema)) return false;
            }
            if (hasDeclaredProperties && declaredProperties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!IsJsonSchemaValid(propertySchema, property.Value, rootSchema)) return false;
                continue;
            }
            if (!schema.TryGetProperty("additionalProperties", out var additional)) continue;
            if (additional.ValueKind == JsonValueKind.False
                || additional.ValueKind == JsonValueKind.Object
                && !IsJsonSchemaValid(additional, property.Value, rootSchema))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsJsonSchemaArrayValid(
        JsonElement schema,
        JsonElement instance,
        JsonElement rootSchema)
    {
        var length = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minimum) && length < minimum.GetInt32()
            || schema.TryGetProperty("maxItems", out var maximum) && length > maximum.GetInt32())
        {
            return false;
        }
        if (schema.TryGetProperty("uniqueItems", out var unique)
            && unique.ValueKind == JsonValueKind.True)
        {
            var values = instance.EnumerateArray().ToArray();
            for (var left = 0; left < values.Length; left++)
            {
                for (var right = left + 1; right < values.Length; right++)
                {
                    if (JsonElement.DeepEquals(values[left], values[right])) return false;
                }
            }
        }
        return !schema.TryGetProperty("items", out var items)
               || instance.EnumerateArray().All(item => IsJsonSchemaValid(items, item, rootSchema));
    }

    private static bool IsJsonSchemaStringValid(JsonElement schema, JsonElement instance)
    {
        var value = instance.GetString()!;
        var length = value.EnumerateRunes().Count();
        return (!schema.TryGetProperty("minLength", out var minimum) || length >= minimum.GetInt32())
               && (!schema.TryGetProperty("maxLength", out var maximum) || length <= maximum.GetInt32())
               && (!schema.TryGetProperty("pattern", out var pattern)
                   || Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant));
    }

    private static bool IsJsonSchemaNumberValid(JsonElement schema, JsonElement instance)
    {
        if (!decimal.TryParse(
                instance.GetRawText(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return !schema.TryGetProperty("minimum", out _)
                   && !schema.TryGetProperty("maximum", out _);
        }
        return (!schema.TryGetProperty("minimum", out var minimum) || value >= minimum.GetDecimal())
               && (!schema.TryGetProperty("maximum", out var maximum) || value <= maximum.GetDecimal());
    }

    private static bool MatchesJsonSchemaType(string type, JsonElement value)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number
                         && decimal.TryParse(
                             value.GetRawText(),
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var integer)
                         && decimal.Truncate(integer) == integer,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static string ValidContentReference()
        => """
           {"id":"opaque","length":9007199254740991.0,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","mediaType":"application/octet-stream","fileName":"display-name.bin","expiresAtUtc":"2030-01-02T03:04:05Z","repeatability":"single-use"}
           """;

    private static byte[] RuntimeValueDescriptor()
        => Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc.v2.values",
              "version":"2.0.0",
              "errors":{},
              "services":{"example":{"methods":{"check":{"kind":"unary","idempotency":"idempotent","errors":[],"requestSchema":{"$ref":"#/$defs/Empty"},"responseSchema":{"$ref":"#/$defs/Empty"}}}}},
              "$defs":{
                "Empty":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Uuid":{"type":"string","format":"uuid"},
                "Date":{"type":"string","format":"date"},
                "Instant":{"type":"string","format":"date-time"},
                "Duration":{"type":"string","format":"duration"},
                "Uri":{"type":"string","format":"uri"},
                "BigInteger":{"type":"string","format":"big-integer","maxDigits":40,"valueMinimum":"-9999999999999999999999999999999999999999","valueMaximum":"9999999999999999999999999999999999999999"},
                "Decimal":{"type":"string","format":"decimal","maxPrecision":20,"maxScale":8,"valueMinimum":"-1000.125","valueExclusiveMaximum":"1000.125"},
                "Bytes":{"type":"string","contentEncoding":"base64","maxDecodedBytes":8},
                "JSON":{},
                "JSONObject":{"type":"object","maxProperties":2},
                "Content":{"type":"object","logicalType":"content-reference"},
                "Integer":{"type":"integer","minimum":-9007199254740991,"maximum":9007199254740991},
                "Float32":{"type":"number","format":"float32","minimum":-1000.5,"maximum":1000.5},
                "Float32Enum":{"type":"number","format":"float32","enum":[1]},
                "Float64":{"type":"number","format":"float64","minimum":-9007199254740991.5,"maximum":9007199254740991.5},
                "Float64Enum":{"type":"number","format":"float64","enum":[1]},
                "LargeArray":{"type":"array","items":{"type":"boolean"},"minItems":0,"maxItems":1000000}
              }
            }
            """);

    private static byte[] RelaxedFacetDescriptor()
        => Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc.v2.relaxedfacets",
              "version":"2.0.0",
              "errors":{},
              "services":{"example":{"methods":{"check":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Unit"},"responseSchema":{"$ref":"#/$defs/Unit"}}}}},
              "$defs":{
                "Unit":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Any":{},
                "Text":{"type":"string"},
                "LargeTextBound":{"type":"string","maxLength":2000000},
                "Values":{"type":"array","items":{"type":"boolean"}},
                "StringMap":{"type":"object","additionalProperties":{"type":"boolean"},"propertyNames":{"type":"string"}},
                "Bytes":{"type":"string","contentEncoding":"base64"},
                "BigInteger":{"type":"string","format":"big-integer"},
                "Decimal":{"type":"string","format":"decimal"},
                "RecursiveArray":{"type":"array","items":{"$ref":"#/$defs/RecursiveArray"}}
              }
            }
            """);

    private static byte[] SingleSchemaDescriptor(string schema)
        => Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc.v2.invalidfacet",
              "version":"2.0.0",
              "errors":{},
              "services":{"example":{"methods":{"check":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Value"},"responseSchema":{"$ref":"#/$defs/Value"}}}}},
              "$defs":{"Value":__SCHEMA__}
            }
            """.Replace("__SCHEMA__", schema, StringComparison.Ordinal));

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
              "schemaProfile":"sunder.rpc.values.v1",
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
              "schemaProfile":"sunder.rpc.values.v1",
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
