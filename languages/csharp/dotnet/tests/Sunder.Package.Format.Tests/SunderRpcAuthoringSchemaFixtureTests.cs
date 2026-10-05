using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderRpcAuthoringSchemaFixtureTests
{
    private static readonly string AuthoringRoot = Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring");
    private static readonly string ConformanceRoot = Path.Combine(AuthoringRoot, "conformance");

    [Fact]
    public void AuthoringSchemas_ExecuteEveryIndexedStructuralFixture()
    {
        using var expectations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ConformanceRoot, "expectations.json")));
        Assert.Equal(1, expectations.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            expectations.RootElement.GetProperty("schemaDialect").GetString());

        var indexedPaths = new HashSet<string>(StringComparer.Ordinal);
        var representedSchemas = new Dictionary<string, (bool Valid, bool Invalid)>(StringComparer.Ordinal);
        foreach (var fixture in expectations.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var schemaPath = fixture.GetProperty("schemaPath").GetString()!;
            var expected = fixture.GetProperty("schemaValid").GetBoolean();
            var schemaFile = schemaPath.Split('#', 2)[0].Replace("../", string.Empty, StringComparison.Ordinal);
            representedSchemas.TryGetValue(schemaFile, out var representation);
            representedSchemas[schemaFile] = expected
                ? (true, representation.Invalid)
                : (representation.Valid, true);

            if (fixture.TryGetProperty("fixturePath", out var fixturePathElement))
            {
                var fixturePath = fixturePathElement.GetString()!;
                Assert.True(indexedPaths.Add(fixturePath), $"Duplicate authoring fixture index entry '{fixturePath}'.");
                using var instance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ConformanceRoot, fixturePath)));
                Assert.Equal(expected, Validate(schemaPath, instance.RootElement));
                continue;
            }

            var recipe = fixture.GetProperty("recipe");
            if (recipe.GetProperty("operation").GetString() == "materialization-boundary")
            {
                AssertMaterializationBoundary(recipe, fixture);
            }
            using var generated = GenerateRecipe(recipe);
            Assert.Equal(expected, Validate(schemaPath, generated.RootElement));
        }

        var files = Directory.EnumerateFiles(ConformanceRoot, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ConformanceRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !string.Equals(path, "expectations.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(files, indexedPaths.Order(StringComparer.Ordinal));

        var rootSchemas = Directory.EnumerateFiles(AuthoringRoot, "*.schema.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(7, rootSchemas.Length);
        foreach (var schema in rootSchemas)
        {
            Assert.True(representedSchemas.TryGetValue(schema!, out var representation), $"Schema '{schema}' has no fixture.");
            Assert.True(representation.Valid, $"Schema '{schema}' has no valid fixture.");
            Assert.True(representation.Invalid, $"Schema '{schema}' has no invalid fixture.");
        }
    }

    [Fact]
    public void LocalLockAndReceiptSchemas_AllowAbsentOriginsButRejectPresentNullOrMalformedOrigins()
    {
        foreach (var (schema, fixture, arrays) in new[]
        {
            ("../contract-lock.schema.json", "mixed-contract-lock.json", false),
            ("../authoring-receipt.schema.json", "authoring-receipt-mixed.json", true),
        })
        {
            var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(ConformanceRoot, "valid", fixture)))!;
            var artifacts = arrays ? root["packageArtifacts"]!.AsArray().Select(value => value!.AsObject()).ToArray()
                : root["packageArtifacts"]!.AsObject().Select(pair => pair.Value!.AsObject()).ToArray();
            var records = arrays ? artifacts : artifacts.Concat(root["platformImports"]!.AsObject().Select(pair => pair.Value!.AsObject())).ToArray();
            Assert.NotEmpty(records);
            foreach (var record in records) record.Remove("registryOrigin");
            using (var document = JsonDocument.Parse(root.ToJsonString())) Assert.True(Validate(schema, document.RootElement));
            foreach (var record in records)
                foreach (var origin in new string?[] { null, "", "not-an-origin", "https://registry.example/path", "https://REGISTRY.example/" })
                {
                    record["registryOrigin"] = origin;
                    using var document = JsonDocument.Parse(root.ToJsonString());
                    Assert.False(Validate(schema, document.RootElement));
                    record.Remove("registryOrigin");
                }
        }
    }

    [Fact]
    public void AuthoringSchemaCorpus_UsesDeterministicAsciiJsonFiles()
    {
        foreach (var path in Directory.EnumerateFiles(AuthoringRoot, "*.json", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.NotEmpty(bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.DoesNotContain(bytes, value => value > 0x7f);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            using var _ = JsonDocument.Parse(bytes);
        }
    }

    [Fact]
    public void SchemaHarness_UsesDraft202012ReferenceSiblingAndPatternEndSemantics()
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "$defs": {
                "bounded": { "type": "string", "minLength": 1 }
              },
              "$ref": "#/$defs/bounded",
              "maxLength": 3,
              "pattern": "^[a-z]+$"
            }
            """);
        using var valid = JsonDocument.Parse("\"abc\"");
        using var tooLong = JsonDocument.Parse("\"abcd\"");
        using var terminalNewline = JsonDocument.Parse("\"ab\\n\"");
        using var fractionalInteger = JsonDocument.Parse("1e-29");
        using var tinyFractionalInteger = JsonDocument.Parse("1e-999999999999");
        using var hugeInteger = JsonDocument.Parse("1e999999999999");
        using var integerSchema = JsonDocument.Parse("{ \"type\": \"integer\" }");

        Assert.True(JsonSchemaTestValidator.IsValid(schema.RootElement, valid.RootElement));
        Assert.False(JsonSchemaTestValidator.IsValid(schema.RootElement, tooLong.RootElement));
        Assert.False(JsonSchemaTestValidator.IsValid(schema.RootElement, terminalNewline.RootElement));
        Assert.False(JsonSchemaTestValidator.IsValid(integerSchema.RootElement, fractionalInteger.RootElement));
        Assert.False(JsonSchemaTestValidator.IsValid(integerSchema.RootElement, tinyFractionalInteger.RootElement));
        Assert.True(JsonSchemaTestValidator.IsValid(integerSchema.RootElement, hugeInteger.RootElement));
    }

    private static bool Validate(string schemaPath, JsonElement instance)
    {
        var pathAndFragment = schemaPath.Split('#', 2);
        var schemaFile = pathAndFragment[0].Replace("../", string.Empty, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AuthoringRoot, schemaFile)));
        var schema = document.RootElement;
        if (pathAndFragment.Length == 2 && pathAndFragment[1].Length > 0)
        {
            const string definitionPrefix = "/$defs/";
            Assert.StartsWith(definitionPrefix, pathAndFragment[1], StringComparison.Ordinal);
            schema = document.RootElement.GetProperty("$defs").GetProperty(pathAndFragment[1][definitionPrefix.Length..]);
        }
        return JsonSchemaTestValidator.IsValid(schema, instance, document.RootElement);
    }

    private static JsonDocument GenerateRecipe(JsonElement recipe)
    {
        return recipe.GetProperty("id").GetString() switch
        {
            "project-export-contracts-max-plus-one" => GenerateProjectExportOverflow(recipe),
            { } id when id.StartsWith("materialization-", StringComparison.Ordinal) => GenerateMaterializationBoundary(recipe),
            var id => throw new InvalidOperationException($"Unknown authoring conformance recipe '{id}'."),
        };
    }

    private static JsonDocument GenerateProjectExportOverflow(JsonElement recipe)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(
            ConformanceRoot,
            recipe.GetProperty("baseFixturePath").GetString()!)))!.AsObject();
        var template = JsonNode.Parse(File.ReadAllBytes(Path.Combine(
            ConformanceRoot,
            "valid/package-project-export-owned.json")))!["contracts"]![0]!.AsObject();
        var contracts = new JsonArray();
        for (var index = 0; index < recipe.GetProperty("count").GetInt32(); index++)
        {
            var contract = template.DeepClone().AsObject();
            contract["contractKey"] = $"contract{index:000}";
            contract["contractId"] = $"example.owner.contract{index:000}";
            contracts.Add(contract);
        }
        root["contracts"] = contracts;
        return JsonDocument.Parse(root.ToJsonString());
    }

    private static JsonDocument GenerateMaterializationBoundary(JsonElement recipe)
    {
        Assert.Equal("materialization-boundary", recipe.GetProperty("operation").GetString());
        Assert.Contains(
            recipe.GetProperty("target").GetString(),
            new[] { "bundles", "uses", "providers", "packageDependencies" });
        Assert.Contains(recipe.GetProperty("count").GetInt32(), new[] { 128, 129 });
        return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            ConformanceRoot,
            recipe.GetProperty("baseFixturePath").GetString()!)));
    }

    private static void AssertMaterializationBoundary(JsonElement recipe, JsonElement fixture)
    {
        var target = recipe.GetProperty("target").GetString()!;
        var count = recipe.GetProperty("count").GetInt32();
        var targetCountName = target switch
        {
            "bundles" => "contractBundles",
            "uses" => "roleScopedUses",
            "providers" => "providers",
            "packageDependencies" => "packageDependencies",
            _ => throw new InvalidOperationException($"Unknown materialization target '{target}'."),
        };
        foreach (var aggregateCount in recipe.GetProperty("expectedAggregateCounts").EnumerateObject())
        {
            Assert.Equal(
                string.Equals(aggregateCount.Name, targetCountName, StringComparison.Ordinal) ? count : 0,
                aggregateCount.Value.GetInt32());
        }

        var semanticValid = count <= 128;
        Assert.Equal(semanticValid, fixture.GetProperty("semanticValid").GetBoolean());
        var diagnostic = fixture.GetProperty("expectedSemanticDiagnosticId");
        if (semanticValid)
        {
            Assert.Equal(JsonValueKind.Null, diagnostic.ValueKind);
        }
        else
        {
            Assert.Equal("rpc-authoring.aggregate-materializability-overflow", diagnostic.GetString());
        }
    }
}
