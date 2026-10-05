using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderRpcContractCompatibilityTests
{
    private static readonly JsonObject ProofFixture = ReadFixture("proof-cases.json");
    private static readonly JsonObject HistoryFixture = ReadFixture("history-cases.json");

    public static IEnumerable<object[]> ProofCases()
    {
        foreach (var item in ProofFixture["cases"]!.AsArray())
        {
            var recipe = item!;
            yield return [recipe["id"]!.GetValue<string>(), false];
            if (recipe["inverse"] is not null) yield return [recipe["id"]!.GetValue<string>(), true];
        }
    }

    [Theory]
    [MemberData(nameof(ProofCases))]
    public void Classify_ExecutesSharedProofRecipes(string id, bool inverse)
    {
        var recipe = ProofFixture["cases"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == id)!;
        var previousJson = Baseline();
        Apply(previousJson, recipe["setup"]);
        Apply(previousJson, recipe["previous"]);
        var candidateJson = previousJson.DeepClone().AsObject();
        Apply(candidateJson, recipe["candidate"]);
        var previous = Parse(previousJson);
        var candidate = Parse(candidateJson);
        if (inverse) (previous, candidate) = (candidate, previous);
        var expected = inverse ? recipe["inverse"]! : recipe;
        var oldBytes = previous.GetCanonicalUtf8();
        var newBytes = candidate.GetCanonicalUtf8();

        var result = SunderRpcContractCompatibility.Classify(previous, candidate);

        AssertResult(expected["kind"]!.GetValue<string>(), Changes(expected["changes"]!), result);
        Assert.Equal(oldBytes, previous.GetCanonicalUtf8());
        Assert.Equal(newBytes, candidate.GetCanonicalUtf8());
        AssertResult(expected["kind"]!.GetValue<string>(), result.Changes,
            SunderRpcContractCompatibility.Classify(previous, candidate));
        if (result.Kind is SunderRpcContractChangeKind.Minor or SunderRpcContractChangeKind.Patch)
            AssertSampledDirection(previous, candidate);
    }

    public static IEnumerable<object[]> FourStateCases()
    {
        foreach (var item in ProofFixture["fourStateCases"]!.AsArray())
            foreach (var direction in new[] { "request", "output" })
                yield return [item!["from"]!.GetValue<string>(), item!["to"]!.GetValue<string>(), direction];
    }

    [Theory]
    [MemberData(nameof(FourStateCases))]
    public void Classify_SeparatesAllFourPresenceAndNullabilityStates(string from, string to, string direction)
    {
        var recipe = ProofFixture["fourStateCases"]!.AsArray().Single(item =>
            item!["from"]!.GetValue<string>() == from && item!["to"]!.GetValue<string>() == to)!;
        var slot = direction == "request" ? "Input" : "Output";
        var previousJson = Baseline();
        previousJson["$defs"]![slot] = ProofFixture["schemas"]![from]!.DeepClone();
        var candidateJson = previousJson.DeepClone().AsObject();
        candidateJson["$defs"]![slot] = ProofFixture["schemas"]![to]!.DeepClone();
        var previous = Parse(previousJson);
        var candidate = Parse(candidateJson);
        var kind = recipe[direction]!.GetValue<string>();
        var expected = recipe[direction + "Codes"]!.AsArray().Select(code =>
            new SunderRpcContractChange($"/$defs/{slot}/properties/value", code!.GetValue<string>())).ToList();
        if (kind == "Minor") expected.Add(new($"/$defs/{slot}", "schema-compatible"));

        AssertResult(kind, expected, SunderRpcContractCompatibility.Classify(previous, candidate));
        if (kind == "Minor") AssertSampledDirection(previous, candidate);
    }

    public static IEnumerable<object[]> MinimumCases()
        => HistoryFixture["minimumCases"]!.AsArray().Select(item =>
            item!.AsArray().Select(value => (object)value!.GetValue<string>()).ToArray());

    [Theory]
    [MemberData(nameof(MinimumCases))]
    public void GetMinimumVersion_ExecutesSharedArbitraryDigitRecipes(string previous, string kind, string expected)
        => Assert.Equal(expected, SunderRpcContractCompatibility.GetMinimumVersion(
            previous, Enum.Parse<SunderRpcContractChangeKind>(kind)));

    public static IEnumerable<object[]> HistoryCases()
        => HistoryFixture["cases"]!.AsArray().Select(item => new object[] { item!["id"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(HistoryCases))]
    public void ValidateVersion_ExecutesSharedImmutableHistoryRecipes(string id)
    {
        var recipe = HistoryFixture["cases"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == id)!;
        var candidate = HistoryDescriptor(recipe["candidate"]!);
        var history = recipe["history"]!.AsArray().Select(item => HistoryDescriptor(item!)).ToArray();
        var expected = recipe["diagnostics"]!.AsArray();

        var diagnostics = SunderRpcContractCompatibility.ValidateVersion(candidate, history);

        if (expected.Count == 0) Assert.Empty(diagnostics);
        else
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.InRange(diagnostic.Length, 1, 1024);
            foreach (var text in expected) Assert.Contains(text!.GetValue<string>(), diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("Reviewed annotation", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("$defs", diagnostic, StringComparison.Ordinal);
        }
        Assert.Equal(diagnostics, SunderRpcContractCompatibility.ValidateVersion(candidate, history.Reverse().ToArray()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3-01")]
    public void GetMinimumVersion_RejectsInvalidVersionSyntax(string? version)
        => Assert.Throws<ArgumentException>(() => SunderRpcContractCompatibility.GetMinimumVersion(
            version!, SunderRpcContractChangeKind.Patch));

    [Theory]
    [InlineData("Patch")]
    [InlineData("Minor")]
    [InlineData("Major")]
    public void GetMinimumVersion_RejectsUnfrozenPrereleaseTransitions(string kind)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SunderRpcContractCompatibility.GetMinimumVersion(
            "1.2.3-alpha.99999999999999999999999+reviewed", Enum.Parse<SunderRpcContractChangeKind>(kind)));
        Assert.Contains("contract-version.prerelease-policy-required", exception.Message, StringComparison.Ordinal);
        Assert.Contains("explicit prerelease transition policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMinimumVersion_RejectsUnknownClassification()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SunderRpcContractCompatibility.GetMinimumVersion(
            "1.2.3", (SunderRpcContractChangeKind)99));

    [Theory]
    [InlineData("Patch")]
    [InlineData("Minor")]
    [InlineData("Major")]
    public void GetMinimumVersion_IncrementsAtTheDescriptorLengthBoundary(string kind)
    {
        var digits = new string('9', SemanticVersion.MaximumLength - 5);
        var previous = kind switch { "Patch" => "1.0." + digits, "Minor" => "1." + digits + ".0", _ => digits + ".0.0" };
        var increment = "1" + new string('0', digits.Length);
        var expected = kind switch { "Patch" => "1.0." + increment, "Minor" => "1." + increment + ".0", _ => increment + ".0.0" };
        Assert.Equal(SemanticVersion.MaximumLength, expected.Length);
        var result = SunderRpcContractCompatibility.GetMinimumVersion(previous, Enum.Parse<SunderRpcContractChangeKind>(kind));
        Assert.Equal(expected, result);
        Assert.True(SemanticVersion.TryParse(result, out _));
    }

    [Theory]
    [InlineData("Patch")]
    [InlineData("Minor")]
    [InlineData("Major")]
    public void GetMinimumVersion_FailsActionablyRatherThanReturningAnOverlengthDescriptorVersion(string kind)
    {
        var digits = new string('9', SemanticVersion.MaximumLength - 4);
        var previous = kind switch { "Patch" => "1.0." + digits, "Minor" => "1." + digits + ".0", _ => digits + ".0.0" };
        Assert.True(SemanticVersion.TryParse(previous, out _));
        var exception = Assert.Throws<InvalidOperationException>(() => SunderRpcContractCompatibility.GetMinimumVersion(
            previous, Enum.Parse<SunderRpcContractChangeKind>(kind)));
        Assert.Contains("contract-version.version-limit-exceeded", exception.Message, StringComparison.Ordinal);
        Assert.Contains("higher stable bump", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateVersion_AllowsHigherFittingBumpAboveAnOverlengthMathematicalMinimum(bool minor)
    {
        var digits = new string('9', SemanticVersion.MaximumLength - 4);
        var previousJson = Baseline();
        previousJson["version"] = minor ? "1." + digits + ".0" : "1.0." + digits;
        var candidateJson = previousJson.DeepClone().AsObject();
        candidateJson["version"] = minor ? "2.0.0" : "1.1.0";
        if (minor) candidateJson["$defs"]!["Input"]!["maxLength"] = 20;
        Assert.Empty(SunderRpcContractCompatibility.ValidateVersion(Parse(candidateJson), [Parse(previousJson)]));
    }

    [Fact]
    public void CanonicalPropertyOrderingIsNotAnImmutableByteMutation()
    {
        var baseline = Baseline();
        var reordered = new JsonObject(baseline.Reverse().Select(pair =>
            new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value!.DeepClone())));
        var previous = Parse(baseline);
        var candidate = Parse(reordered);
        Assert.NotEqual(baseline.ToJsonString(), reordered.ToJsonString());
        AssertResult("Unchanged", [], SunderRpcContractCompatibility.Classify(previous, candidate));
        Assert.Empty(SunderRpcContractCompatibility.ValidateVersion(candidate, [previous]));
    }

    [Fact]
    public void AcceptedVersionExtractionDoesNotCreateAnAutomaticPatchLoop()
    {
        var previousJson = Baseline();
        var changedJson = previousJson.DeepClone().AsObject();
        changedJson["$defs"]!["Input"]!["description"] = "An accepted source annotation.";
        var previous = Parse(previousJson);
        var drift = SunderRpcContractCompatibility.Classify(previous, Parse(changedJson));
        Assert.Equal(SunderRpcContractChangeKind.Patch, drift.Kind);
        changedJson["version"] = SunderRpcContractCompatibility.GetMinimumVersion(previous.Version, drift.Kind);
        var accepted = Parse(changedJson);
        Assert.Empty(SunderRpcContractCompatibility.ValidateVersion(accepted, [previous]));
        var extractedAtAcceptedVersion = Parse(changedJson.DeepClone().AsObject());
        var clean = SunderRpcContractCompatibility.Classify(accepted, extractedAtAcceptedVersion);
        Assert.Equal(SunderRpcContractChangeKind.Unchanged, clean.Kind);
        Assert.Equal(accepted.Version, SunderRpcContractCompatibility.GetMinimumVersion(accepted.Version, clean.Kind));
    }

    [Fact]
    public void SharedRequestAndResponseDefinitionMustSatisfyBothDirections()
    {
        var previousJson = Baseline();
        previousJson["services"]!["main"]!["methods"]!["call"]!["responseSchema"] = new JsonObject { ["$ref"] = "#/$defs/Input" };
        var candidateJson = previousJson.DeepClone().AsObject();
        candidateJson["$defs"]!["Input"]!["maxLength"] = 20;
        AssertResult("Major", [new("/$defs/Input", "schema-compatible"), new("/$defs/Input/maxLength", "upper-bound-not-contained")],
            SunderRpcContractCompatibility.Classify(Parse(previousJson), Parse(candidateJson)));
    }

    [Fact]
    public void RemovingARecursiveOutputEdgeDoesNotGuessACoinductiveProof()
    {
        var previousJson = Baseline();
        previousJson["$defs"]!["Input"] = ProofFixture["schemas"]!["recursive"]!.DeepClone();
        previousJson["services"]!["main"]!["methods"]!["call"]!["requestSchema"] = new JsonObject { ["$ref"] = "#/$defs/Unused" };
        previousJson["services"]!["main"]!["methods"]!["call"]!["responseSchema"] = new JsonObject { ["$ref"] = "#/$defs/Input" };
        var candidateJson = previousJson.DeepClone().AsObject();
        candidateJson["$defs"]!["Input"]!["properties"]!.AsObject().Remove("next");
        AssertResult("Major", [new("/$defs/Input", "recursive-change-unclassified")],
            SunderRpcContractCompatibility.Classify(Parse(previousJson), Parse(candidateJson)));
    }

    [Fact]
    public void UnusedAliasStillExposesAnEditedGeneratedValueApi()
    {
        var previousJson = Baseline();
        previousJson["$defs"]!["Alias"] = new JsonObject { ["$ref"] = "#/$defs/Input" };
        var candidateJson = previousJson.DeepClone().AsObject();
        candidateJson["$defs"]!["Input"]!["maxLength"] = 20;
        AssertResult("Major", [new("/$defs/Alias", "definition-change-unclassified"), new("/$defs/Input", "schema-compatible")],
            SunderRpcContractCompatibility.Classify(Parse(previousJson), Parse(candidateJson)));
    }

    [Fact]
    public void ReasonLimitDoesNotHideASubsequentMajorChange()
    {
        var previousJson = Baseline();
        var candidateJson = previousJson.DeepClone().AsObject();
        var methods = candidateJson["services"]!["main"]!["methods"]!.AsObject();
        for (var index = 0; index < 80; index++) methods["added" + index.ToString("D3", CultureInfo.InvariantCulture)] = methods["call"]!.DeepClone();
        candidateJson["errors"]!["example.failed"]!.AsObject().Remove("detailsSchema");
        var previous = Parse(previousJson);
        var candidate = Parse(candidateJson);
        var result = SunderRpcContractCompatibility.Classify(previous, candidate);
        Assert.Equal(SunderRpcContractChangeKind.Major, result.Kind);
        Assert.Equal(64, result.Changes.Count);
        Assert.Contains(new SunderRpcContractChange("", "changes-truncated"), result.Changes);
        AssertResult("Major", result.Changes, SunderRpcContractCompatibility.Classify(previous, candidate));
    }

    [Fact]
    public void TraversalBudgetFailsClosedOnLargeOtherwiseCompatibleLiteralProofs()
    {
        var previousJson = Baseline();
        var candidateJson = Baseline();
        var sourceProperties = new JsonObject();
        var targetProperties = new JsonObject();
        var values = new JsonArray(Enumerable.Range(0, 255).Select(index =>
            (JsonNode?)JsonValue.Create("v" + index.ToString(CultureInfo.InvariantCulture))).ToArray());
        for (var index = 0; index < 256; index++)
        {
            var name = "p" + index.ToString("D3", CultureInfo.InvariantCulture);
            sourceProperties[name] = new JsonObject { ["type"] = "string", ["enum"] = values.DeepClone() };
            targetProperties[name] = sourceProperties[name]!.DeepClone();
            targetProperties[name]!["enum"]!.AsArray().Add("z");
        }
        previousJson["$defs"]!["Input"] = Record(sourceProperties);
        candidateJson["$defs"]!["Input"] = Record(targetProperties);
        var previous = Parse(previousJson);
        var candidate = Parse(candidateJson);
        var result = SunderRpcContractCompatibility.Classify(previous, candidate);
        Assert.Equal(SunderRpcContractChangeKind.Major, result.Kind);
        Assert.Contains(new SunderRpcContractChange("", "analysis-limit-exceeded"), result.Changes);
        Assert.InRange(result.Changes.Count, 1, 64);
        AssertResult("Major", result.Changes, SunderRpcContractCompatibility.Classify(previous, candidate));
    }

    [Fact]
    public void OverlongReasonPointersFailClosedWithoutTruncatingAPointerToken()
    {
        var previousJson = Baseline();
        var candidateJson = Baseline();
        JsonNode oldValue = ProofFixture["schemas"]!["text"]!.DeepClone();
        JsonNode newValue = ProofFixture["schemas"]!["short-text"]!.DeepClone();
        for (var index = 0; index < 10; index++)
        {
            var name = new string('a', 128);
            oldValue = Record(new JsonObject { [name] = oldValue });
            newValue = Record(new JsonObject { [name] = newValue });
        }
        previousJson["$defs"]!["Input"] = oldValue;
        candidateJson["$defs"]!["Input"] = newValue;
        var result = SunderRpcContractCompatibility.Classify(Parse(previousJson), Parse(candidateJson));
        Assert.Equal(SunderRpcContractChangeKind.Major, result.Kind);
        Assert.Contains(new SunderRpcContractChange("", "analysis-limit-exceeded"), result.Changes);
        Assert.All(result.Changes, change => Assert.InRange(change.Path.Length, 0, 1024));
    }

    [Fact]
    public void ReasonsNeverIncludeSchemaLiteralOrAnnotationBodies()
    {
        const string secret = "DO-NOT-PRINT-DESCRIPTOR-CONTENT";
        var previousJson = Baseline();
        previousJson["$defs"]!["Input"] = new JsonObject { ["type"] = "string", ["const"] = "previous" };
        var candidateJson = previousJson.DeepClone().AsObject();
        candidateJson["$defs"]!["Input"]!["const"] = secret;
        candidateJson["$defs"]!["Input"]!["description"] = secret;
        var previous = Parse(previousJson);
        var candidate = Parse(candidateJson);
        var result = SunderRpcContractCompatibility.Classify(previous, candidate);
        Assert.Equal(SunderRpcContractChangeKind.Major, result.Kind);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Assert.Single(SunderRpcContractCompatibility.ValidateVersion(candidate, [previous])), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidDescriptorsStayTheExistingParsersResponsibility()
    {
        var invalid = Baseline();
        invalid["$defs"]!["Input"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true };
        Assert.Throws<SunderRpcDescriptorException>(() => Parse(invalid));
        var valid = Parse(Baseline());
        Assert.Throws<ArgumentNullException>(() => SunderRpcContractCompatibility.Classify(null!, valid));
        Assert.Throws<ArgumentNullException>(() => SunderRpcContractCompatibility.Classify(valid, null!));
        Assert.Throws<ArgumentNullException>(() => SunderRpcContractCompatibility.ValidateVersion(null!, []));
        Assert.Throws<ArgumentNullException>(() => SunderRpcContractCompatibility.ValidateVersion(valid, null!));
    }

    [Fact]
    public void SharedFixtureIdsAreUniqueAndTheFourStateMatrixIsComplete()
    {
        Assert.Equal(1, ProofFixture["formatVersion"]!.GetValue<int>());
        Assert.Equal(1, HistoryFixture["formatVersion"]!.GetValue<int>());
        foreach (var fixture in new[] { ProofFixture, HistoryFixture })
        {
            var ids = fixture["cases"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        }
        Assert.Equal(32, FourStateCases().DistinctBy(row => string.Join("/", row)).Count());
    }

    private static JsonObject ReadFixture(string name)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "evolution", name)))!.AsObject();

    private static JsonObject Baseline() => ProofFixture["baseline"]!.DeepClone().AsObject();

    private static SunderRpcContractDescriptor Parse(JsonObject descriptor)
        => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(descriptor.ToJsonString()));

    private static SunderRpcContractDescriptor HistoryDescriptor(JsonNode recipe)
    {
        var descriptor = Baseline();
        Apply(descriptor, HistoryFixture["variants"]![recipe["variant"]!.GetValue<string>()]);
        descriptor["version"] = recipe["version"]!.GetValue<string>();
        return Parse(descriptor);
    }

    private static void Apply(JsonObject descriptor, JsonNode? operations)
    {
        if (operations is null) return;
        foreach (var item in operations.AsArray())
        {
            var operation = item!;
            var path = operation["path"]!.GetValue<string>();
            var separator = path.LastIndexOf('/');
            var parent = Select(descriptor, path[..separator]);
            var member = Decode(path[(separator + 1)..]);
            if (operation["remove"]?.GetValue<bool>() == true)
            {
                Assert.True(parent.AsObject().Remove(member));
                continue;
            }
            var value = operation["copy"] is { } source ? Select(descriptor, source.GetValue<string>())
                : operation["schema"] is { } schema ? ProofFixture["schemas"]![schema.GetValue<string>()]
                : operation["value"];
            if (parent is JsonArray array) array[int.Parse(member, CultureInfo.InvariantCulture)] = value?.DeepClone();
            else parent[member] = value?.DeepClone();
        }
    }

    private static JsonNode Select(JsonNode node, string pointer)
    {
        foreach (var token in pointer.Split('/').Skip(1))
        {
            var member = Decode(token);
            node = node is JsonArray array ? array[int.Parse(member, CultureInfo.InvariantCulture)]! : node[member]!;
        }
        return node;
    }

    private static string Decode(string token) => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static JsonObject Record(JsonObject properties)
        => new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(), ["additionalProperties"] = false };

    private static IEnumerable<SunderRpcContractChange> Changes(JsonNode array)
        => array.AsArray().Select(item => new SunderRpcContractChange(item![0]!.GetValue<string>(), item![1]!.GetValue<string>()));

    private static void AssertResult(string kind, IEnumerable<SunderRpcContractChange> expected, SunderRpcContractCompatibilityResult result)
    {
        Assert.Equal(kind, result.Kind.ToString());
        Assert.Equal(expected.OrderBy(change => change.Path, StringComparer.Ordinal).ThenBy(change => change.Code, StringComparer.Ordinal), result.Changes);
    }

    private static void AssertSampledDirection(SunderRpcContractDescriptor previous, SunderRpcContractDescriptor candidate)
    {
        var samples = new List<string> { "null", "true", "false", "0", "1", "{}", "[]", "{\"value\":null}", "{\"value\":\"a\"}", "{\"value\":\"\"}", "{\"unknown\":\"a\"}" };
        foreach (var length in new[] { 0, 1, 2, 3, 5, 10, 11, 20, 21 })
        {
            var text = JsonSerializer.Serialize(new string('a', length));
            samples.Add(text);
            samples.Add("{\"value\":" + text + "}");
            samples.Add("{\"" + new string('a', length) + "\":\"a\"}");
            samples.Add("[" + text + "]");
        }
        samples.Add("\"b\"");
        samples.Add("[\"a\",\"a\",\"a\",\"a\"]");
        samples.Add("{\"a\":\"a\",\"b\":\"a\",\"c\":\"a\",\"d\":\"a\"}");
        foreach (var json in samples)
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            foreach (var service in previous.Services)
            {
                var newService = candidate.FindService(service.ServiceId)!;
                foreach (var method in service.Methods)
                {
                    var newMethod = newService.FindMethod(method.MethodId)!;
                    if (previous.IsValid(method.RequestSchemaReference, value, out _))
                        Assert.True(candidate.IsValid(newMethod.RequestSchemaReference, value, out _));
                    if (candidate.IsValid(newMethod.OutputSchemaReference, value, out _))
                        Assert.True(previous.IsValid(method.OutputSchemaReference, value, out _));
                }
            }
            foreach (var error in previous.Errors.Values)
            {
                var newError = candidate.FindError(error.Code)!;
                if (error.DetailsSchema is not null && newError.DetailsSchema is not null
                    && candidate.IsValid(newError.DetailsSchema, value, out _))
                    Assert.True(previous.IsValid(error.DetailsSchema, value, out _));
            }
        }
    }
}
