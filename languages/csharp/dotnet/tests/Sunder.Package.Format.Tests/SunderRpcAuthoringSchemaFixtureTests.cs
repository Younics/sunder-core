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
        Assert.Equal(8, rootSchemas.Length);
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

    [Theory]
    [InlineData("[\"app\"]", true)]
    [InlineData("[\"runtime\"]", true)]
    [InlineData("[\"app\",\"runtime\"]", true)]
    [InlineData("[\"runtime\",\"app\"]", false)]
    [InlineData("[\"app\",\"app\"]", false)]
    [InlineData("[\"app\",\"runtime\",\"runtime\"]", false)]
    [InlineData("[\"App\"]", false)]
    [InlineData("[\"worker\"]", false)]
    [InlineData("[]", false)]
    [InlineData("\"runtime\"", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void ConsumerRoles_RequireExactReviewedNonemptyOrderedIntent(string? roles, bool valid)
    {
        var root = ReadFixture("valid/package-api-contract-lock.json");
        var import = root["packageImports"]!["example.dependency"]!.AsObject();
        if (roles is null) import.Remove("consumerRoles");
        else import["consumerRoles"] = JsonNode.Parse(roles);
        Assert.Equal(valid, ValidateNode("contract-lock", root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredPrivateRecords_RejectPublicCurrentOrHistoryWithoutChangingActivePublicSchema(bool history)
    {
        var root = ReadFixture(history
            ? "valid/package-retired-private-history-baseline.json"
            : "valid/package-retired-private-baseline.json");
        Assert.True(ValidateNode("owned-contracts", root));
        var record = root["retiredPrivateContracts"]!["minimal"]!;
        var snapshot = history ? record["history"]![0]! : record["current"]!;
        snapshot["exposure"] = "public";
        Assert.False(ValidateNode("owned-contracts", root));
        // Public evidence remains valid only in the active evolution map.
        root["contracts"]!["minimal"] = record.DeepClone();
        root["retiredPrivateContracts"] = new JsonObject();
        Assert.True(ValidateNode("owned-contracts", root));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("history")]
    public void RetiredPrivateRecords_PreserveSnapshotStateAndEvidenceRequirements(string location)
    {
        var root = ReadFixture("valid/package-retired-private-history-baseline.json");
        var record = root["retiredPrivateContracts"]!["minimal"]!.AsObject();
        if (location == "current")
        {
            record["current"] = record["history"]![0]!.DeepClone();
            record["history"] = new JsonArray();
        }
        var snapshot = (location == "current" ? record["current"]! : record["history"]![0]!).AsObject();
        Assert.True(ValidateNode("owned-contracts", root));
        foreach (var required in new[] { "canonicalDescriptor", "canonicalAbiProjection", "descriptorSha256", "abiSha256", "packageRelease", "publication" })
        {
            var value = snapshot[required]!.DeepClone();
            snapshot.Remove(required);
            Assert.False(ValidateNode("owned-contracts", root));
            snapshot[required] = value;
        }
        foreach (var node in new[] { record, snapshot, snapshot["packageRelease"]!.AsObject(), snapshot["publication"]!.AsObject() })
        {
            node["unexpected"] = true;
            Assert.False(ValidateNode("owned-contracts", root));
            node.Remove("unexpected");
        }
        snapshot["state"] = "draft";
        snapshot.Remove("packageRelease");
        snapshot.Remove("publication");
        Assert.Equal(location == "current", ValidateNode("owned-contracts", root));
        if (location == "history")
        {
            record["history"] = new JsonArray();
            Assert.False(ValidateNode("owned-contracts", root));
        }
    }

    [Fact]
    public void RetiredPrivateMap_IsOptionalOrEmptyOnlyForPackageBaselines()
    {
        foreach (var mode in new[] { "package", "platform" })
        {
            var root = ReadFixture($"valid/{mode}-owned-baseline.json");
            Assert.True(ValidateNode("owned-contracts", root));
            root["retiredPrivateContracts"] = new JsonObject();
            Assert.Equal(mode == "package", ValidateNode("owned-contracts", root));
            root["retiredPrivateContracts"] = null;
            Assert.False(ValidateNode("owned-contracts", root));
        }
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void RetiredPrivateMap_KeepsTheExistingFamilySafetyCeiling(int count, bool valid)
    {
        var root = ReadFixture("valid/package-retired-private-baseline.json");
        var template = root["retiredPrivateContracts"]!["minimal"]!;
        var records = new JsonObject();
        for (var index = 0; index < count; index++) records[$"contract{index:D3}"] = template.DeepClone();
        root["retiredPrivateContracts"] = records;
        // The schema ceiling is independent of descriptor identity/combined-count semantic checks.
        Assert.Equal(valid, ValidateNode("owned-contracts", root));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void RetiredPrivateHistory_KeepsTheExistingHistorySafetyCeiling(int count, bool valid)
    {
        var root = ReadFixture("valid/package-retired-private-history-baseline.json");
        var record = root["retiredPrivateContracts"]!["minimal"]!;
        var template = record["history"]![0]!;
        var history = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            var snapshot = template.DeepClone();
            snapshot["contractVersion"] = $"2.0.{index}";
            history.Add(snapshot);
        }
        record["history"] = history;
        // This exercises the structural ceiling; full tuple/history validation is still mandatory.
        Assert.Equal(valid, ValidateNode("owned-contracts", root));
    }

    [Theory]
    [InlineData("2.0.0-beta.1+build.7", true)]
    [InlineData("2.0.0+BUILD.7", true)]
    [InlineData("2.0.0-beta.01", false)]
    [InlineData("2.0.0+", false)]
    [InlineData("v2.0.0", false)]
    [InlineData(null, false)]
    public void ProjectExport_RequiresFullStrictPackageSemVer(string? version, bool valid)
    {
        var root = ReadFixture("valid/minimal-package-project-export.json");
        if (version is null) root.Remove("packageVersion");
        else root["packageVersion"] = version;
        Assert.Equal(valid, ValidateNode("package-project-export", root));
        root["packageVersion"] = "1.0.0+" + new string('a', 251);
        Assert.False(ValidateNode("package-project-export", root));
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
    public void CurrentV1_RequiresModelProjectionAndSelectedArtifactPinsInBothReceiptRoles()
    {
        foreach (var schema in new[] { "package-project-export", "authoring-receipt" })
        {
            var root = ReadFixture(schema == "package-project-export"
                ? "valid/package-project-export-owned.json" : "valid/authoring-receipt-mixed.json");
            foreach (var role in new[] { "runtime", "app" })
            {
                var instance = root.DeepClone();
                if (schema == "authoring-receipt" && role == "app") instance["role"] = "app";
                Assert.True(ValidateNode(schema, instance));
                instance["contracts"]![0]!.AsObject().Remove("modelProjection");
                Assert.False(ValidateNode(schema, instance));
            }
        }
        foreach (var member in new[] { "modelProjectionBytes", "modelProjectionSha256" })
        {
            var root = ReadFixture("valid/package-api-contract-lock.json");
            root["packageArtifacts"]!["example.dependency"]!.AsObject().Remove(member);
            Assert.False(ValidateNode("contract-lock", root));
            root = ReadFixture("valid/authoring-receipt-mixed.json");
            root["packageArtifacts"]![0]!.AsObject().Remove(member);
            Assert.False(ValidateNode("authoring-receipt", root));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4194304, true)]
    [InlineData(4194305, false)]
    public void ModelProjectionPins_HaveTheSameInclusiveByteBoundInLockAndReceipt(int bytes, bool valid)
    {
        var root = ReadFixture("valid/package-api-contract-lock.json");
        root["packageArtifacts"]!["example.dependency"]!["modelProjectionBytes"] = bytes;
        Assert.Equal(valid, ValidateNode("contract-lock", root));
        root = ReadFixture("valid/authoring-receipt-mixed.json");
        root["packageArtifacts"]![0]!["modelProjectionBytes"] = bytes;
        Assert.Equal(valid, ValidateNode("authoring-receipt", root));
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

    [Theory]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":1}}}", true)]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":2100}}}", true)]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":0}}}", false)]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":-1}}}", false)]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":2101}}}", false)]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":1.5}}}", false)]
    [InlineData("{\"example\":{\"ping\":{\"timeoutSeconds\":1,\"required\":true}}}", false)]
    [InlineData("{\"example\":{}}", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    public void MethodPolicies_AreOptionalBoundedHintsInExportAndBothReceiptRoles(string policy, bool valid)
    {
        foreach (var schema in new[] { "package-project-export", "authoring-receipt" })
        {
            var root = ReadFixture(schema == "package-project-export"
                ? "valid/package-project-export-owned.json" : "valid/authoring-receipt-mixed.json");
            var contract = root["contracts"]![0]!.AsObject();
            contract["methodPolicies"] = JsonNode.Parse(policy);
            Assert.Equal(valid, ValidateNode(schema, root));
            if (schema == "authoring-receipt")
            {
                root["role"] = "app";
                Assert.Equal(valid, ValidateNode(schema, root));
            }
            // Private inline evidence may carry the same hint; publication filtering is separate.
            contract["exposure"] = "package";
            Assert.Equal(valid, ValidateNode(schema, root));
            contract.Remove("methodPolicies");
            Assert.True(ValidateNode(schema, root));
        }
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
        using var projectionSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AuthoringRoot, "rpc-model-projection.schema.json")));
        return JsonSchemaTestValidator.IsValid(schema, instance, document.RootElement, new Dictionary<string, JsonElement>
        {
            ["rpc-model-projection.schema.json#/$defs/modelProjection"] = projectionSchema.RootElement.GetProperty("$defs").GetProperty("modelProjection"),
            ["rpc-model-projection.schema.json#/$defs/methodPolicies"] = projectionSchema.RootElement.GetProperty("$defs").GetProperty("methodPolicies"),
        });
    }

    [Theory]
    [InlineData("{\"Imported_Context\":[\"executionTarget\"]}", true)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("{\"Context\":[]}", false)]
    [InlineData("{\"Context\":[\"executionTarget\",\"executionTarget\"]}", false)]
    [InlineData("{\"Context\":[null]}", false)]
    [InlineData("{\"#/$defs/Context\":[\"executionTarget\"]}", false)]
    public void OptionalReferencePolicy_IsAnOptionalThirdProjectionFieldInBothEmbeddingSchemas(string policy, bool valid)
    {
        foreach (var schema in new[] { "package-project-export", "authoring-receipt" })
        {
            var root = ReadFixture(schema == "package-project-export"
                ? "valid/package-project-export-optional-reference.json" : "valid/authoring-receipt-mixed.json");
            root["contracts"]![0]!["modelProjection"]!["optionalServiceReferenceMembers"] = JsonNode.Parse(policy);
            Assert.Equal(valid, ValidateNode(schema, root));
            root["contracts"]![0]!["modelProjection"]!.AsObject().Remove("optionalServiceReferenceMembers");
            Assert.True(ValidateNode(schema, root));
        }
    }

    private static JsonObject ReadFixture(string path)
        => JsonNode.Parse(File.ReadAllBytes(Path.Combine(ConformanceRoot, path)))!.AsObject();

    private static bool ValidateNode(string schema, JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return Validate($"../{schema}.schema.json", document.RootElement);
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
