using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpSourceCompilerTests
{
    private static readonly string ParityRoot = Path.Combine(AppContext.BaseDirectory, "CurrentRpcAuthoring");

    [Theory]
    [InlineData("naming-defaults")]
    [InlineData("provider-forms")]
    [InlineData("identity-limits")]
    [InlineData("complete")]
    public void Compile_CurrentFixture_ProducesExactCanonicalContractsAndProviders(string fixtureId)
    {
        using var expectations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ParityRoot, "expectations.json")));
        var fixture = expectations.RootElement.GetProperty("validFixtures")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetString() == fixtureId);
        var sourcePath = Path.Combine(ParityRoot, fixture.GetProperty("path").GetString()!);
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            fixture.GetProperty("packageId").GetString()!,
            fixture.GetProperty("packageVersion").GetString()!,
            [new(sourcePath, File.ReadAllText(sourcePath))],
            CompilationReferences()));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var expectedContracts = fixture.GetProperty("contracts").EnumerateArray().ToArray();
        Assert.Equal(expectedContracts.Length, result.Contracts.Count);
        foreach (var expected in expectedContracts)
        {
            var contractKey = expected.GetProperty("contractKey").GetString()!;
            var actual = Assert.Single(result.Contracts, contract => contract.ContractKey == contractKey);
            Assert.Equal(expected.GetProperty("contractId").GetString(), actual.Descriptor.ContractId);
            Assert.Equal(expected.GetProperty("contractVersion").GetString(), actual.Descriptor.Version);
            Assert.Equal(expected.GetProperty("exposure").GetString(), Exposure(actual.Exposure));
            Assert.Equal(expected.GetProperty("canonicalDescriptorBytes").GetInt32(), actual.Descriptor.GetCanonicalUtf8().Length);
            Assert.Equal(expected.GetProperty("descriptorSha256").GetString(), actual.Descriptor.Sha256);
            Assert.Equal(expected.GetProperty("canonicalAbiProjectionBytes").GetInt32(), actual.Descriptor.GetCanonicalAbiProjectionUtf8().Length);
            Assert.Equal(expected.GetProperty("abiSha256").GetString(), actual.Descriptor.AbiSha256);
            Assert.Equal(expected.GetProperty("logicalName").GetString(), actual.GeneratedLogicalName);
            if (expected.TryGetProperty("contractIdCharacters", out var boundary)) Assert.Equal(boundary.GetInt32(), actual.Descriptor.ContractId.Length);
            var expectedProviders = expected.GetProperty("providers").EnumerateArray().ToArray();
            Assert.Equal(expectedProviders.Length, actual.Providers.Count);
            foreach (var expectedProvider in expectedProviders)
            {
                var providerId = expectedProvider.GetProperty("providerId").GetString()!;
                var actualProvider = Assert.Single(actual.Providers, provider => provider.ProviderId == providerId);
                Assert.Equal(expectedProvider.GetProperty("audience").GetString(), Audience(actualProvider.Audience));
                Assert.Equal("runtime", expectedProvider.GetProperty("role").GetString());
                if (expectedProvider.TryGetProperty("characters", out var providerBoundary)) Assert.Equal(providerBoundary.GetInt32(), actualProvider.ProviderId.Length);
                Assert.Equal(actual.Descriptor.Services.Single().ServiceId, actualProvider.ServiceId);
            }
        }
    }

    [Fact]
    public void Compile_SchemaDialectOptInPreservesExactDescriptorAnnotationWithoutChangingAbiOrDefaults()
    {
        var ordinary = Assert.Single(CompileSource(SimpleServiceSource).Contracts).Descriptor;
        var explicitlyAbsent = Assert.Single(CompileSource(SimpleServiceSource.Replace(
            "SunderServiceExposure.Package)", "SunderServiceExposure.Package, IncludeSchemaDialect = false)", StringComparison.Ordinal)).Contracts).Descriptor;
        var annotatedResult = CompileSource(SimpleServiceSource.Replace(
            "SunderServiceExposure.Package)", "SunderServiceExposure.Package, IncludeSchemaDialect = true)", StringComparison.Ordinal));
        Assert.True(annotatedResult.Success, string.Join(Environment.NewLine, annotatedResult.Diagnostics.Select(FormatDiagnostic)));
        var annotated = Assert.Single(annotatedResult.Contracts).Descriptor;
        var expected = JsonNode.Parse(ordinary.GetCanonicalUtf8())!.AsObject();
        expected["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        Assert.Equal(SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(expected)).GetCanonicalUtf8(), annotated.GetCanonicalUtf8());
        Assert.Equal(ordinary.GetCanonicalUtf8(), explicitlyAbsent.GetCanonicalUtf8());
        Assert.NotEqual(ordinary.Sha256, annotated.Sha256);
        Assert.Equal(ordinary.AbiSha256, annotated.AbiSha256);
    }

    [Fact]
    public void Compile_DeepNullableCollectionsUseTheDescriptorJsonDepthBudget()
    {
        var type = "string";
        for (var index = 0; index < 22; index++) type = $"IReadOnlyList<{type}>?";
        var source = $$"""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public sealed partial class DeepService
            {
                [SunderMethod]
                public ValueTask<Value> EchoAsync(Value request) => ValueTask.FromResult(request);
            }
            public sealed record Value
            {
                public required {{type}} Items { get; init; }
            }
            """;
        var result = CompileSource(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var descriptor = Assert.Single(result.Contracts).Descriptor;
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(descriptor.GetCanonicalUtf8()));
        Assert.Equal(descriptor.GetCanonicalUtf8(), SunderRpcContractDescriptor.Parse(descriptor.GetCanonicalUtf8()).GetCanonicalUtf8());
    }

    [Fact]
    public void Compile_AdditionalDefinitionsRetainOnlySelectedSourceValuesAndUseTheNormalGraph()
    {
        var source = SimpleServiceSource + """

            [SunderTransparentValue, SunderValueConstraints(Minimum = "0")]
            public readonly record struct CollectionIndex { public required long Value { get; init; } }
            public sealed record Unrelated { public required string Text { get; init; } }
            """;
        var ordinary = Assert.Single(CompileSource(source).Contracts).Descriptor;
        Assert.DoesNotContain("CollectionIndex", ordinary.Definitions.Keys);
        var result = CompileSource(source.Replace("SunderServiceExposure.Package)",
            "SunderServiceExposure.Package, AdditionalDefinitions = new[] { typeof(CollectionIndex), typeof(GreetingResult) })", StringComparison.Ordinal));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var retained = Assert.Single(result.Contracts).Descriptor;
        var expected = JsonNode.Parse(ordinary.GetCanonicalUtf8())!.AsObject();
        expected["$defs"]!["CollectionIndex"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 };
        Assert.Equal(SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(expected)).GetCanonicalUtf8(), retained.GetCanonicalUtf8());
        Assert.DoesNotContain("Unrelated", retained.Definitions.Keys);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("new System.Type[] { null }")]
    [InlineData("new[] { typeof(int) }")]
    [InlineData("new[] { typeof(GreetingResult), typeof(GreetingResult) }")]
    [InlineData("new[] { typeof(System.Collections.Generic.List<string>) }")]
    public void Compile_AdditionalDefinitionsRejectInvalidOrDuplicateTypes(string value)
    {
        var result = CompileSource(SimpleServiceSource.Replace("SunderServiceExposure.Package)",
            $"SunderServiceExposure.Package, AdditionalDefinitions = {value})", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.value-invalid");
    }

    [Fact]
    public void Compile_AdditionalDefinitionsAreBoundedAndCannotBypassDefinitionNameCollisions()
    {
        var oversized = CompileSource(SimpleServiceSource.Replace("SunderServiceExposure.Package)",
            "SunderServiceExposure.Package, AdditionalDefinitions = new[] { " + string.Join(", ", Enumerable.Repeat("typeof(GreetingResult)", 257)) + " })", StringComparison.Ordinal));
        Assert.False(oversized.Success);
        Assert.Contains(oversized.Diagnostics, diagnostic => diagnostic.Message.Contains("at most 256", StringComparison.Ordinal));
        var collision = CompileSource((SimpleServiceSource + """

            [SunderValue("Retained")]
            public sealed record First { public required string Value { get; init; } }
            [SunderValue("Retained")]
            public sealed record Second { public required string Value { get; init; } }
            """).Replace("SunderServiceExposure.Package)",
                "SunderServiceExposure.Package, AdditionalDefinitions = new[] { typeof(First), typeof(Second) })", StringComparison.Ordinal));
        Assert.False(collision.Success);
        Assert.Contains(collision.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.identity-collision");
    }

    [Theory]
    [InlineData("OAuth2Service", "Service", "o-auth2", "o.auth2", "OAuth2", "oAuth2")]
    [InlineData("URLLookup", null, "url-lookup", "url.lookup", "UrlLookup", "urlLookup")]
    [InlineData("GetURLAsync", "Async", "get-url", "get.url", "GetUrl", "getUrl")]
    public void Naming_UsesTheFrozenAsciiTokenAlgorithm(
        string source,
        string? suffix,
        string lowerKebab,
        string contractSuffix,
        string pascal,
        string camel)
    {
        Assert.True(SunderRpcCSharpNaming.TryCreate(source, suffix, out var actual));
        Assert.NotNull(actual);
        Assert.Equal((lowerKebab, contractSuffix, pascal, camel),
            (actual.LowerKebab, actual.ContractSuffix, actual.Pascal, actual.Camel));
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Service")]
    [InlineData("Bad__Name")]
    [InlineData("Snowman\u2603")]
    [InlineData("9Service")]
    public void Naming_RejectsNonPortableSourceNames(string source)
        => Assert.False(SunderRpcCSharpNaming.TryCreate(source, null, out _));

    [Fact]
    public void Compile_CurrentInvalidFixtures_EmitReviewedDiagnosticsAtCurrentSourceSpans()
    {
        using var expectations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ParityRoot, "expectations.json")));
        foreach (var fixture in expectations.RootElement.GetProperty("invalidFixtures").EnumerateArray())
        {
            var relativePath = fixture.GetProperty("path").GetString()!;
            var sourcePath = Path.Combine(ParityRoot, relativePath);
            var source = File.ReadAllText(sourcePath);
            var fixtureId = fixture.GetProperty("id").GetString()!;
            if (fixture.TryGetProperty("derivedIdentityCharacters", out var identityLength))
            {
                // Independently count these deliberately simple boundary names, without
                // using the compiler's naming function to manufacture the expectation.
                var declaration = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().First();
                var name = declaration.Identifier.ValueText[..^"Service".Length];
                var prefix = name.StartsWith("Contract", StringComparison.Ordinal) ? "Contract" : "Provider";
                var suffix = name == "X" ? "x" : prefix.ToLowerInvariant() + "." + name[prefix.Length..].ToLowerInvariant();
                var identity = fixture.GetProperty("packageId").GetString() + "." + suffix
                    + (fixtureId == "source-limit-provider-identity" ? ".provider" : "");
                Assert.Equal(identityLength.GetInt32(), identity.Length);
                Assert.True(identity.Length > 128);
            }
            var baseline = fixture.TryGetProperty("acceptedIdentities", out var identities)
                ? identities.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal) : null;
            var result = new SunderRpcCSharpSourceCompiler().Compile(new(
                fixture.GetProperty("packageId").GetString()!,
                "1.0.0",
                [new(sourcePath, source)],
                CompilationReferences(),
                OwnedContractIdsByServiceMetadataName: baseline));

            var expectedDiagnostic = fixture.GetProperty("diagnosticId").GetString()!;
            Assert.False(result.Success);
            var diagnostic = Assert.Single(result.Diagnostics);
            if (fixture.TryGetProperty("message", out var message))
            {
                Assert.Equal(message.GetString(), diagnostic.Message);
                Assert.Empty(result.Contracts);
                Assert.Null(diagnostic.Path);
            }
            Assert.True(expectedDiagnostic == diagnostic.Id,
                $"{fixtureId}: expected {expectedDiagnostic}, actual {FormatDiagnostic(diagnostic)}");
            var expectedSpans = fixture.GetProperty("spans").EnumerateArray().ToArray();
            var actualSpans = new List<SunderRpcCSharpSourceSpan>();
            if (diagnostic.Path is not null)
            {
                actualSpans.Add(new(
                    diagnostic.Path,
                    diagnostic.Line!.Value,
                    diagnostic.Column!.Value,
                    diagnostic.EndLine!.Value,
                    diagnostic.EndColumn!.Value));
            }
            actualSpans.AddRange(diagnostic.RelatedSpans ?? []);
            Assert.Equal(expectedSpans.Length, actualSpans.Count);
            for (var index = 0; index < expectedSpans.Length; index++)
            {
                var expectedSpan = expectedSpans[index];
                var actualSpan = actualSpans[index];
                Assert.Equal(relativePath.Replace('\\', '/'), Path.GetRelativePath(ParityRoot, actualSpan.Path).Replace('\\', '/'));
                var line = expectedSpan.GetProperty("line").GetInt32();
                var text = expectedSpan.GetProperty("text").GetString()!;
                var column = source.Split('\n')[line - 1].IndexOf(text, StringComparison.Ordinal) + 1;
                Assert.True(column > 0, fixtureId);
                Assert.Equal(line, actualSpan.StartLine);
                Assert.Equal(line, actualSpan.EndLine);
                Assert.Equal(column, actualSpan.StartColumn);
                Assert.Equal(column + text.Length, actualSpan.EndColumn);
            }
        }
    }

    [Fact]
    public void Compile_NewPublicContractVersion_IsIndependentOfPackageVersion()
    {
        var result = CompileSource(SimpleServiceSource.Replace("Exposure.Package", "Exposure.Public", StringComparison.Ordinal), packageVersion: "9.8.7");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        Assert.Equal("1.0.0", Assert.Single(result.Contracts).Descriptor.Version);
    }

    [Fact]
    public void Compile_ExecutableOutputAcceptsTopLevelStatements()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            Console.WriteLine("starting");
            [SunderService(SunderServiceExposure.Package)]
            public sealed partial class GreetingService
            {
                [SunderMethod]
                public ValueTask<GreetingResult> GreetAsync(GreetingRequest request)
                    => ValueTask.FromResult(new GreetingResult { Message = request.Name });
            }
            public sealed record GreetingRequest
            {
                public required string Name { get; init; }
            }
            public sealed record GreetingResult
            {
                public required string Message { get; init; }
            }
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(
            "example.top.level",
            "1.0.0",
            [new("Program.cs", source)],
            CompilationReferences(),
            CompilationOutputKind: SunderRpcCSharpCompilationOutputKind.ConsoleApplication));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        Assert.Single(result.Contracts);
    }

    [Fact]
    public void Compile_AcceptedPublicContractVersion_ComesFromOwnedBaseline()
    {
        var result = CompileSource(
            SimpleServiceSource.Replace("Exposure.Package", "Exposure.Public", StringComparison.Ordinal),
            packageVersion: "9.8.7",
            baseline: new Dictionary<string, SunderRpcCSharpOwnedContractIdentity>(StringComparer.Ordinal)
            {
                ["greeting"] = new("example.versioning.greeting", "2.3.4"),
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        Assert.Equal("2.3.4", Assert.Single(result.Contracts).Descriptor.Version);
    }

    [Fact]
    public void Compile_RejectsSourceTraversalPastTheInclusiveDepthLimit()
    {
        const int definitionCount = 65;
        var source = new System.Text.StringBuilder()
            .AppendLine("using System;")
            .AppendLine("using System.Threading.Tasks;")
            .AppendLine("using Sunder.Sdk.Rpc;")
            .AppendLine("using Sunder.Sdk.Services;")
            .AppendLine("namespace Example.Depth;")
            .AppendLine("[SunderService(SunderServiceExposure.Package)]")
            .AppendLine("public sealed partial class DepthService")
            .AppendLine("{")
            .AppendLine("    [SunderMethod]")
            .AppendLine("    public ValueTask<Depth00> RunAsync(Depth00 request) => throw new NotImplementedException();")
            .AppendLine("}");
        for (var index = 0; index < definitionCount; index++)
        {
            source.Append("public sealed record Depth").Append(index.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).AppendLine()
                .AppendLine("{");
            if (index + 1 == definitionCount)
            {
                source.AppendLine("    public required bool Value { get; init; }");
            }
            else
            {
                source.Append("    public required Depth")
                    .Append((index + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                    .AppendLine(" Next { get; init; }");
            }
            source.AppendLine("}");
        }

        var result = CompileSource(source.ToString(), packageId: "example.depth");

        Assert.False(result.Success);
        Assert.Equal("rpc-authoring.source-limit-exceeded", Assert.Single(result.Diagnostics).Id);
    }

    [Fact]
    public void Compile_NumericEnumCanPreserveAClosedSetWithoutExplicitBounds()
    {
        var source = SimpleServiceSource.Replace("required string Message", "required Priority Message", StringComparison.Ordinal)
            + "\n[SunderNumericEnum] public enum Priority { Low = -1, Normal = 0, High = 7 }";
        var baseline = CompileSource(source);
        Assert.True(baseline.Success, string.Join(Environment.NewLine, baseline.Diagnostics));
        var original = Assert.Single(baseline.Contracts).Descriptor;
        var explicitDefault = CompileSource(source.Replace("[SunderNumericEnum]", "[SunderNumericEnum(IncludeRangeBounds = true)]", StringComparison.Ordinal));
        Assert.True(explicitDefault.Success, string.Join(Environment.NewLine, explicitDefault.Diagnostics));
        Assert.Equal(original.GetCanonicalUtf8(), Assert.Single(explicitDefault.Contracts).Descriptor.GetCanonicalUtf8());
        var result = CompileSource(source.Replace("[SunderNumericEnum]", "[SunderNumericEnum(IncludeRangeBounds = false)]", StringComparison.Ordinal));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var expected = JsonNode.Parse(original.GetCanonicalUtf8())!;
        var definition = expected["$defs"]!["Priority"]!.AsObject();
        Assert.True(definition.Remove("minimum"));
        Assert.True(definition.Remove("maximum"));
        Assert.Equal(SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(expected)).GetCanonicalUtf8(), Assert.Single(result.Contracts).Descriptor.GetCanonicalUtf8());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compile_RejectsNumericEnumAliasesWithoutThrowing(bool includeRangeBounds)
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            namespace Example.Numeric;
            [SunderService(SunderServiceExposure.Package)]
            public sealed partial class NumericService
            {
                [SunderMethod]
                public ValueTask<Result> RunAsync(Result request) => throw new NotImplementedException();
            }
            public sealed record Result
            {
                public required Priority Priority { get; init; }
            }
            [SunderNumericEnum]
            public enum Priority : long
            {
                First = 1,
                Alias = 1,
            }
            """;

        var result = CompileSource(source.Replace("[SunderNumericEnum]",
            $"[SunderNumericEnum(IncludeRangeBounds = {includeRangeBounds.ToString().ToLowerInvariant()})]", StringComparison.Ordinal), packageId: "example.numeric");

        Assert.False(result.Success);
        Assert.Equal("rpc-authoring.value-invalid", Assert.Single(result.Diagnostics).Id);
    }

    [Fact]
    public void ImplementationEmitter_ResolvesGeneratedRegistrationMemberOnSecondPass()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using Sunder.Sdk.Worker;
            [assembly: SunderPackage(Id = "example.generated", Name = "Generated")]
            namespace Example.Generated;
            public static class Bootstrap
            {
                public static SunderWorkerProviderRegistration Create()
                    => new GreetingService().CreateSunderRpcProviderRegistration();
            }
            [SunderService(SunderServiceExposure.Package)]
            public sealed partial class GreetingService
            {
                [SunderMethod]
                public ValueTask<GreetingResult> GreetAsync(
                    GreetingRequest request,
                    SunderRpcInvocationContext context,
                    CancellationToken cancellationToken) => ValueTask.FromResult(new GreetingResult { Message = request.Name });
            }
            public sealed record GreetingRequest
            {
                public required string Name { get; init; }
            }
            public sealed record GreetingResult
            {
                public required string Message { get; init; }
            }
            """;
        var first = new SunderRpcCSharpSourceCompiler().Compile(new(
            string.Empty,
            "7.6.5",
            [new("source.cs", source)],
            CompilationReferences()));

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(FormatDiagnostic)));
        var generated = SunderRpcCSharpImplementationEmitter.Emit(first.Contracts);
        Assert.Contains("CreateSunderRpcProviderRegistration", generated, StringComparison.Ordinal);
        Assert.Contains("example.generated.greeting.provider", generated, StringComparison.Ordinal);
        Assert.Contains("\"7.6.5\"", generated, StringComparison.Ordinal);

        var second = new SunderRpcCSharpSourceCompiler().Compile(new(
            string.Empty,
            "7.6.5",
            [new("source.cs", source)],
            CompilationReferences(),
            SupplementalSources: [new("authoring.g.cs", generated)],
            RequireGeneratedMembersResolved: true));

        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(FormatDiagnostic)));
    }

    [Fact]
    public void ImplementationEmitter_UsesFrozenContextualKeywordEscaping()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            namespace @allows.@extension;
            [SunderService(SunderServiceExposure.Package)]
            public sealed partial class @field
            {
                [SunderMethod]
                public ValueTask<Response> @args(Request request)
                    => ValueTask.FromResult(new Response { Value = request.Value });
            }
            public sealed record Request
            {
                public required string Value { get; init; }
            }
            public sealed record Response
            {
                public required string Value { get; init; }
            }
            """;
        var result = CompileSource(source, packageId: "example.keywords");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts);
        Assert.Contains("namespace @allows.@extension", generated, StringComparison.Ordinal);
        Assert.Contains("partial class @field", generated, StringComparison.Ordinal);
        Assert.Contains(").@args(", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_AllowedStringsPreservesAnInlineEnumWithoutAnExtraDefinition()
    {
        var ordinary = Assert.Single(CompileSource(SimpleServiceSource).Contracts).Descriptor;
        var result = CompileSource(SimpleServiceSource.Replace("public required string Message",
            "[SunderValueConstraints(AllowedStrings = new[] { \"dispatched\", \"result\" })] public required string Message", StringComparison.Ordinal));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var expected = JsonNode.Parse(ordinary.GetCanonicalUtf8())!.AsObject();
        expected["$defs"]!["GreetingResult"]!["properties"]!["message"]!["enum"] = new JsonArray("dispatched", "result");
        var descriptor = Assert.Single(result.Contracts).Descriptor;
        Assert.Equal(SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(expected)).GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
        Assert.Equal(ordinary.Definitions.Count, descriptor.Definitions.Count);
    }

    [Theory]
    [InlineData("null", "string")]
    [InlineData("new string[0]", "string")]
    [InlineData("new string[] { null }", "string")]
    [InlineData("new[] { \"same\", \"same\" }", "string")]
    [InlineData("new[] { \"\\uD800\" }", "string")]
    [InlineData("new[] { \"x\" }", "long")]
    [InlineData("new[] { \"x\" }", "Guid")]
    public void Compile_AllowedStringsRejectsInvalidSelectionsAndNonPlainStrings(string selection, string type)
    {
        var source = SimpleServiceSource.Replace("public required string Message",
            $"[SunderValueConstraints(AllowedStrings = {selection})] public required {type} Message", StringComparison.Ordinal);
        var result = CompileSource(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.value-invalid");
    }

    [Fact]
    public void Compile_AllowedStringsIsBoundedBeforeDescriptorConstruction()
    {
        var selection = "new[] { " + string.Join(", ", Enumerable.Range(0, 257).Select(value => "\"value" + value + "\"")) + " }";
        var result = CompileSource(SimpleServiceSource.Replace("public required string Message",
            $"[SunderValueConstraints(AllowedStrings = {selection})] public required string Message", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("1..256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("JsonObjectOnly = true")]
    [InlineData("JsonObjectOnly = true, MaxProperties = 5")]
    [InlineData("MaxProperties = 5, JsonObjectOnly = true")]
    public void Compile_JsonObjectOnlyPreservesTheRawObjectShape(string facets)
    {
        var source = SimpleServiceSource.Replace("public required string Message", $"[SunderValueConstraints({facets})] public required System.Text.Json.JsonElement Message", StringComparison.Ordinal);
        var result = CompileSource(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var descriptor = Assert.Single(result.Contracts).Descriptor;
        using var json = JsonDocument.Parse(descriptor.GetCanonicalUtf8());
        var shape = json.RootElement.GetProperty("$defs").GetProperty("GreetingResult").GetProperty("properties").GetProperty("message");
        Assert.Equal("object", shape.GetProperty("type").GetString());
        Assert.Equal(facets.Contains("MaxProperties", StringComparison.Ordinal) ? 2 : 1, shape.EnumerateObject().Count());
        Assert.True(descriptor.IsValid("#/$defs/GreetingResult", JsonSerializer.SerializeToElement(new { message = new { nested = new object?[] { 1, null, "x" } } }), out _));
        Assert.False(descriptor.IsValid("#/$defs/GreetingResult", JsonSerializer.SerializeToElement(new { message = new[] { 1 } }), out _));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("long")]
    [InlineData("System.Collections.Generic.IReadOnlyDictionary<string, string>")]
    [InlineData("SunderRpcContentReference")]
    public void Compile_JsonObjectOnlyRejectsNonRawJsonTargets(string type)
    {
        var result = CompileSource(SimpleServiceSource.Replace("public required string Message", $"[SunderValueConstraints(JsonObjectOnly = true)] public required {type} Message", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.value-invalid");
    }

    [Fact]
    public void ImplementationEmitter_InheritedConcreteProvidersCompileWithWarningsAsErrors()
    {
        var source = SimpleServiceSource.Replace("public sealed partial class GreetingService", "public partial class GreetingService", StringComparison.Ordinal)
            + """

                [SunderServiceProvider(typeof(GreetingService), ProviderId = "example.versioning.secondary")]
                public sealed partial class SecondaryProvider : GreetingService;
                """;
        var result = CompileSource(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
        var contract = Assert.Single(result.Contracts);
        Assert.Equal(2, contract.Providers.Count);
        var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts);
        var options = new CSharpParseOptions(LanguageVersion.CSharp14);
        var compilation = CSharpCompilation.Create("InheritedProviders",
            [CSharpSyntaxTree.ParseText(source, options), CSharpSyntaxTree.ParseText(generated, options)],
            CompilationReferences().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    }

    [Fact]
    public void Compile_UsesProjectAssemblyIdentityForFriendAccessAndGeneratedVerification()
    {
        var root = Directory.CreateTempSubdirectory("sunder-rpc-friend-").FullName;
        try
        {
            var references = CompilationReferences();
            var domain = CSharpCompilation.Create("FriendDomain",
                [CSharpSyntaxTree.ParseText("""
                    [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Example.Owner")]
                    internal static class FriendDomain { internal const string Value = "internal domain value"; }
                    """)], references.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var reference = Path.Combine(root, "FriendDomain.dll");
            var emitted = domain.Emit(reference);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            var source = SimpleServiceSource.Replace("throw new NotImplementedException()",
                "ValueTask.FromResult(new GreetingResult { Message = FriendDomain.Value })", StringComparison.Ordinal);
            var input = new SunderRpcCSharpCompilerInput("example.owner", "1.0.0", [new("service.cs", source)],
                [.. references, reference], AssemblyName: "Example.Owner");
            var compiler = new SunderRpcCSharpSourceCompiler();
            var result = compiler.Compile(input);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(FormatDiagnostic)));
            var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts);
            var verified = compiler.Compile(input with
            {
                SupplementalSources = [new("authoring.g.cs", generated)], RequireGeneratedMembersResolved = true,
            });
            Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics.Select(FormatDiagnostic)));
            Assert.Equal(Assert.Single(result.Contracts).Descriptor.GetCanonicalUtf8(), Assert.Single(verified.Contracts).Descriptor.GetCanonicalUtf8());
            foreach (var other in new[] { "Sunder.Rpc.Authoring", "Example.Unrelated" })
            {
                var denied = compiler.Compile(input with { AssemblyName = other });
                Assert.False(denied.Success);
                Assert.Contains(denied.Diagnostics, diagnostic => diagnostic.Message.Contains("CS0122", StringComparison.Ordinal));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("new string[] { }")]
    [InlineData("new[] { \"discover\", \"discover\" }")]
    [InlineData("new[] { \"subscribe\" }")]
    [InlineData("new[] { \"write\" }")]
    [InlineData("new string[] { null }")]
    public void Compile_AppUseRejectsInvalidOrInapplicableActions(string actions)
    {
        var source = SimpleServiceSource.Replace("[SunderService", $"[SunderAppUse(Actions = {actions})]\n[SunderService", StringComparison.Ordinal);
        var result = CompileSource(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.app-use-invalid");
    }

    [Fact]
    public void Compile_AppUseMustBeOnTheOwningService()
    {
        var result = CompileSource(SimpleServiceSource.Replace("public sealed record GreetingResult", "[SunderAppUse]\npublic sealed record GreetingResult", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.app-use-invalid");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("new string[] { }")]
    [InlineData("new[] { \"discover\", \"discover\" }")]
    [InlineData("new[] { \"subscribe\" }")]
    [InlineData("new[] { \"write\" }")]
    [InlineData("new string[] { null }")]
    public void Compile_RuntimeUseRejectsInvalidOrInapplicableActions(string actions)
    {
        var result = CompileSource(SimpleServiceSource.Replace("[SunderService", $"[SunderRuntimeUse(Actions = {actions})]\n[SunderService", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.runtime-use-invalid");
    }

    [Fact]
    public void Compile_RuntimeUseMustBeOnTheOwningService()
    {
        var result = CompileSource(SimpleServiceSource.Replace("public sealed record GreetingResult", "[SunderRuntimeUse]\npublic sealed record GreetingResult", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.runtime-use-invalid");
    }

    [Theory]
    [InlineData("[System.Text.RegularExpressions.GeneratedRegex(\"a+\", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]", true)]
    [InlineData("[System.Text.RegularExpressions.GeneratedRegex(\"a+\", System.Text.RegularExpressions.RegexOptions.IgnoreCase, 100, \"en-US\")]", true)]
    [InlineData("[System.Text.RegularExpressions.GeneratedRegex(\"[\")]", false)]
    [InlineData("[System.Text.RegularExpressions.GeneratedRegex(\"a\", (System.Text.RegularExpressions.RegexOptions)int.MaxValue)]", false)]
    [InlineData("[System.Text.RegularExpressions.GeneratedRegex(\"a\", System.Text.RegularExpressions.RegexOptions.None, 0)]", false)]
    [InlineData("", false)]
    public void Compile_DefersOnlyValidBclGeneratedRegexBodiesInBothPasses(string attribute, bool expected)
    {
        var source = SimpleServiceSource + "\npublic static partial class Patterns { " + attribute
            + " private static partial System.Text.RegularExpressions.Regex Pattern(); }";
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = new SunderRpcCSharpCompilerInput("example.versioning", "1.0.0", [new("Source.cs", source)], CompilationReferences());
        var first = compiler.Compile(input);
        Assert.Equal(expected, first.Success);
        if (!expected) { Assert.Contains(first.Diagnostics, error => error.Message.Contains("CS8795", StringComparison.Ordinal)); return; }
        var verified = compiler.Compile(input with { RequireGeneratedMembersResolved = true,
            SupplementalSources = [new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(first.Contracts))] });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        Assert.Equal(first.Contracts[0].Descriptor.GetCanonicalUtf8(), verified.Contracts[0].Descriptor.GetCanonicalUtf8());
    }

    [Fact]
    public void Compile_DoesNotTrustSourceDefinedGeneratedRegexLookalikes()
    {
        var input = new SunderRpcCSharpCompilerInput("example.versioning", "1.0.0", [new("Service.cs", SimpleServiceSource), new("Fake.cs", """
            namespace System.Text.RegularExpressions
            {
                public sealed class GeneratedRegexAttribute : System.Attribute
                {
                    public GeneratedRegexAttribute(string pattern) { }
                }
            }
            public static partial class FakePatterns
            {
                [System.Text.RegularExpressions.GeneratedRegex("a+")]
                private static partial System.Text.RegularExpressions.Regex Pattern();
            }
            """)], CompilationReferences());
        var result = new SunderRpcCSharpSourceCompiler().Compile(input);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, error => error.Message.Contains("CS8795", StringComparison.Ordinal));
    }

    private static SunderRpcCSharpCompilationResult CompileSource(
        string source,
        string packageId = "example.versioning",
        string packageVersion = "1.0.0",
        IReadOnlyDictionary<string, SunderRpcCSharpOwnedContractIdentity>? baseline = null)
        => new SunderRpcCSharpSourceCompiler().Compile(new(
            packageId,
            packageVersion,
            [new("source.cs", source)],
            CompilationReferences(),
            OwnedContractsByContractKey: baseline));

    private static string[] CompilationReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                                        ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        return trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SunderServiceAttribute).Assembly.Location)
            .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatDiagnostic(SunderRpcCSharpCompilerDiagnostic diagnostic)
        => $"{diagnostic.Id}: {diagnostic.Path}:{diagnostic.Line}:{diagnostic.Column}-{diagnostic.EndLine}:{diagnostic.EndColumn}: {diagnostic.Message}";

    private static string Exposure(SunderServiceExposure exposure)
        => exposure switch
        {
            SunderServiceExposure.Package => "package",
            SunderServiceExposure.Public => "public",
            _ => throw new ArgumentOutOfRangeException(nameof(exposure)),
        };

    private static string Audience(SunderServiceAudience audience)
        => audience switch
        {
            SunderServiceAudience.Owner => "owner",
            SunderServiceAudience.Public => "public",
            _ => throw new ArgumentOutOfRangeException(nameof(audience)),
        };

    private const string SimpleServiceSource = """
        using System;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        namespace Example.Versioning;
        [SunderService(SunderServiceExposure.Package)]
        public sealed partial class GreetingService
        {
            [SunderMethod]
            public ValueTask<GreetingResult> GreetAsync() => throw new NotImplementedException();
        }
        public sealed record GreetingResult
        {
            public required string Message { get; init; }
        }
        """;
}
