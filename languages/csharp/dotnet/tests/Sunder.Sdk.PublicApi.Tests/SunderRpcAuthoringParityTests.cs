using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcAuthoringParityTests
{
    private static readonly string ParityRoot = FindParityRoot();

    [Fact]
    public void AuthoringParityCorpus_HasExactCanonicalIdentitiesAndFileIndex()
    {
        using var expectations = OpenExpectations();
        var root = expectations.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("normative-preimplementation", root.GetProperty("status").GetString());
        AssertOracleScope(root.GetProperty("oracleScope"));
        AssertLanguageBaselines(root.GetProperty("languageBaselines"));
        Assert.True(root.GetProperty("identityOracles").GetProperty("independentlyVerifiedEqual").GetBoolean());

        var indexedSources = new HashSet<string>(StringComparer.Ordinal);
        var validFixtures = root.GetProperty("validFixtures").EnumerateArray().ToArray();
        Assert.Equal(4, validFixtures.Length);
        Assert.Equal(
            new[] { "complete", "provider-forms", "naming-defaults", "identity-limits" },
            validFixtures.Select(FixtureId));

        foreach (var fixture in validFixtures)
        {
            AssertLanguageSources(fixture.GetProperty("sources"), indexedSources);
            var packageId = fixture.GetProperty("packageId").GetString()!;
            var packageVersion = fixture.GetProperty("packageVersion").GetString()!;
            var contracts = fixture.GetProperty("contracts").EnumerateArray().ToArray();
            var contractKeys = contracts.Select(ContractKey).ToArray();
            Assert.Equal(contractKeys.Order(StringComparer.Ordinal), contractKeys);
            Assert.Equal(contractKeys.Length, contractKeys.Distinct(StringComparer.Ordinal).Count());
            foreach (var contract in contracts)
            {
                Assert.Equal(packageId + "." + ContractKey(contract).Replace('-', '.'), contract.GetProperty("contractId").GetString());
                Assert.Equal(packageVersion, contract.GetProperty("contractVersion").GetString());
                AssertContractIdentity(contract);
            }

            AssertFixtureInventories(fixture, contractKeys);
        }
        AssertFrozenProviderInventory(validFixtures);

        var stableDiagnostics = root.GetProperty("stableSourceDiagnosticIds")
            .EnumerateArray()
            .Select(StringValue)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(10, stableDiagnostics.Count);

        var invalidFixtures = root.GetProperty("invalidFixtures").EnumerateArray().ToArray();
        Assert.Equal(16, invalidFixtures.Length);
        foreach (var fixture in invalidFixtures)
        {
            Assert.Contains(fixture.GetProperty("diagnosticId").GetString()!, stableDiagnostics);
            var applicable = fixture.GetProperty("applicableLanguages").EnumerateArray()
                .Select(StringValue)
                .ToHashSet(StringComparer.Ordinal);
            var sources = fixture.GetProperty("sources").EnumerateArray().ToArray();
            var sourceLanguages = sources.Select(source => source.GetProperty("language").GetString()!).ToArray();
            Assert.Equal(sourceLanguages.Length, sourceLanguages.Distinct(StringComparer.Ordinal).Count());
            Assert.Subset(sourceLanguages.ToHashSet(StringComparer.Ordinal), applicable);
            foreach (var source in sources)
            {
                var relativePath = source.GetProperty("path").GetString()!;
                Assert.True(indexedSources.Add(relativePath), $"Parity source '{relativePath}' is indexed more than once.");
                Assert.Equal(LanguageFromPath(relativePath), source.GetProperty("language").GetString());
                var spans = source.GetProperty("spans");
                if (applicable.Contains(source.GetProperty("language").GetString()!)) Assert.NotEmpty(spans.EnumerateArray());
                else Assert.Empty(spans.EnumerateArray());
                AssertSpans(relativePath, spans);
            }
        }

        AssertFileIndex(root, validFixtures, invalidFixtures, indexedSources);
    }

    [Fact]
    public void AuthoringParityCorpus_IsDeterministicAndCSharpSourcesParse()
    {
        using var expectations = OpenExpectations();
        var encoding = expectations.RootElement.GetProperty("documentEncoding");
        AssertExactProperties(encoding, "encoding", "bom", "lineEnding", "terminalLf", "indentSpaces");
        Assert.Equal("utf-8", encoding.GetProperty("encoding").GetString());
        Assert.False(encoding.GetProperty("bom").GetBoolean());
        Assert.Equal("lf", encoding.GetProperty("lineEnding").GetString());
        Assert.True(encoding.GetProperty("terminalLf").GetBoolean());
        Assert.Equal(2, encoding.GetProperty("indentSpaces").GetInt32());

        foreach (var path in Directory.EnumerateFiles(ParityRoot, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.NotEmpty(bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.DoesNotContain(bytes, value => value > 0x7f);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            if (!path.EndsWith(".cs", StringComparison.Ordinal)) continue;

            var tree = CSharpSyntaxTree.ParseText(
                Encoding.UTF8.GetString(bytes),
                new CSharpParseOptions(LanguageVersion.CSharp14));
            Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }
    }

    [Fact]
    public void AuthoringParityMetadata_HasDurableNamingOrderingAndIdentityBoundaries()
    {
        using var expectations = OpenExpectations();
        var root = expectations.RootElement;
        AssertNamingAndDefaults(root);
        AssertSourceOrdering(root);
        AssertIdentityBoundaries(root);
        AssertCollisionApplicability(root);
    }

    [Fact]
    public void AuthoringParitySourceLimits_HaveDeterministicPreimplementationMaterializations()
    {
        using var expectations = OpenExpectations();
        var root = expectations.RootElement;
        var sourceLimits = root.GetProperty("sourceLimits");
        var limits = sourceLimits.GetProperty("inclusiveLimits");
        Assert.Equal(4_194_304, limits.GetProperty("normalizedRpcSignificantUtf8Bytes").GetInt32());
        Assert.Equal(65_536, limits.GetProperty("visitedSyntaxTypeExpressionNodes").GetInt32());
        Assert.Equal(64, limits.GetProperty("traversalReferenceDepth").GetInt32());
        Assert.Equal(4_096, limits.GetProperty("resolvedLocalConstantBindings").GetInt32());
        Assert.Equal(128, limits.GetProperty("contracts").GetInt32());
        Assert.Equal(128, limits.GetProperty("localProviderBindings").GetInt32());
        Assert.Equal(128, limits.GetProperty("contractOrProviderIdentityCharacters").GetInt32());

        var semantics = sourceLimits.GetProperty("recipeSemantics");
        Assert.Equal("deterministic-language-neutral-preimplementation-materialization", semantics.GetProperty("oracleKind").GetString());
        Assert.Equal("deferred", semantics.GetProperty("frontendExecution").GetString());
        Assert.Equal("rpc-authoring.source-limit-exceeded", semantics.GetProperty("overLimitExpectedDiagnosticId").GetString());

        var seenCounters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in sourceLimits.GetProperty("recipeFixtures").EnumerateArray())
        {
            var counter = fixture.GetProperty("counter").GetString()!;
            Assert.True(seenCounters.Add(counter), counter);
            var maximum = limits.GetProperty(counter).GetInt32();
            var cases = fixture.GetProperty("cases").EnumerateArray().ToArray();
            Assert.Equal(2, cases.Length);
            AssertLimitCase(cases[0], "boundary-valid", maximum, true, null);
            AssertLimitCase(cases[1], "over-limit", maximum + 1, false, "rpc-authoring.source-limit-exceeded");
            AssertLimitMaterialization(fixture, cases);
        }
        Assert.Equal(
            new[]
            {
                "contracts",
                "localProviderBindings",
                "normalizedRpcSignificantUtf8Bytes",
                "resolvedLocalConstantBindings",
                "traversalReferenceDepth",
                "visitedSyntaxTypeExpressionNodes",
            },
            seenCounters.Order(StringComparer.Ordinal));

        var identities = sourceLimits.GetProperty("materializedIdentityFixtures");
        Assert.Equal("contractOrProviderIdentityCharacters", identities.GetProperty("counter").GetString());
        Assert.Equal("identity-limits", identities.GetProperty("boundaryValid").GetString());
        Assert.Equal(
            new[] { "source-limit-contract-identity", "source-limit-package-id-derived-contract", "source-limit-provider-identity" },
            identities.GetProperty("overLimit").EnumerateArray().Select(StringValue));
    }

    [Fact]
    public void AuthoringParityCommandUxAndSelectedPackageResolution_AreExact()
    {
        using var expectations = OpenExpectations();
        var root = expectations.RootElement;
        AssertPackageToolCommandUx(root.GetProperty("packageToolCommandUx"));
        AssertSelectedPackageProviderResolution(root);
        AssertPackagePresenceAndReadiness(root);
    }

    [Fact]
    public void CompatibleInstallation_EvaluatesReviewedRangesAndExactRpcWithoutRestoreContentPins()
    {
        using var expectations = OpenExpectations();
        var root = expectations.RootElement;
        var oracle = root.GetProperty("compatiblePackageInstallation");
        Assert.Equal("outcome-oracle", oracle.GetProperty("status").GetString());
        Assert.Equal("deferred", oracle.GetProperty("productionExecution").GetString());
        Assert.Equal("user-selected-compatible-installation", oracle.GetProperty("decision").GetString());
        Assert.Equal("one-candidate-and-contract-family-for-included-dependency-requirements", oracle.GetProperty("scope").GetString());
        Assert.Equal("selectedPackageProviderResolution", oracle.GetProperty("selectionSource").GetString());
        Assert.True(oracle.GetProperty("baseRequirement").GetProperty("reviewedRange").GetBoolean());
        Assert.Equal("required", oracle.GetProperty("baseRequirement").GetProperty("presence").GetString());
        var fields = new[] { "contractId", "contractVersion", "descriptorSha256", "abiSha256" };
        Assert.Equal(fields, oracle.GetProperty("exactRpcFields").EnumerateArray().Select(StringValue));
        Assert.Equal(new[] { "selectedPackageVersion", "selectedPackageContentIdentity" }, oracle.GetProperty("restoreOnlyFields").EnumerateArray().Select(StringValue));
        Assert.Equal("unchanged", oracle.GetProperty("executionRuntimePins").GetString());
        Assert.Equal("fail-closed-pending-authentication", oracle.GetProperty("platformAuthentication").GetString());
        var resolution = root.GetProperty("selectedPackageProviderResolution");
        var vectors = oracle.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.Equal(15, vectors.Length);
        Assert.Equal(vectors.Length, vectors.Select(FixtureId).Distinct(StringComparer.Ordinal).Count());

        // This is a candidate predicate oracle, not the still-gated production solver.
        foreach (var vector in vectors)
        {
            var requirements = vector.GetProperty("requirementOverrides").EnumerateArray().Select(overrides =>
            {
                var requirement = JsonNode.Parse(resolution.GetProperty("selection").GetRawText())!.AsObject();
                foreach (var property in oracle.GetProperty("baseRequirement").EnumerateObject().Concat(overrides.EnumerateObject()))
                    requirement[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                return requirement;
            }).ToArray();
            Assert.NotEmpty(requirements);
            foreach (var requirement in requirements)
            {
                Assert.Contains(requirement["presence"]!.GetValue<string>(), new[] { "required", "optional" });
                Assert.Matches("^[a-f0-9]{64}$", requirement["selectedPackageContentIdentity"]!.GetValue<string>());
                if (requirement["reviewedRange"]!.GetValue<bool>())
                    Assert.True(PackageVersionRange.Parse(requirement["requestedVersionRange"]!.GetValue<string>())
                        .IsSatisfiedBy(SemanticVersion.Parse(requirement["selectedPackageVersion"]!.GetValue<string>())), FixtureId(vector));
            }
            var candidate = JsonNode.Parse(resolution.GetProperty("baseProvider").GetRawText())!.AsObject();
            foreach (var property in vector.GetProperty("candidateOverrides").EnumerateObject())
                candidate[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            var presence = requirements.Any(requirement => requirement["presence"]!.GetValue<string>() == "required") ? "required" : "optional";
            var version = SemanticVersion.Parse(candidate["packageVersion"]!.GetValue<string>());
            string? reason = null;
            if (requirements.Any(requirement => !requirement["reviewedRange"]!.GetValue<bool>() || requirement["requestedVersionRange"] is null))
                reason = "unreviewed-authority-range";
            else if (requirements.Any(requirement => requirement["packageId"]!.GetValue<string>() != candidate["packageId"]!.GetValue<string>()))
                reason = "package-id-mismatch";
            else if (requirements.Any(requirement => fields.Any(field => !JsonNode.DeepEquals(requirement[field], requirements[0][field]))))
                reason = "rpc-requirement-conflict";
            else if (requirements.Any(requirement => !PackageVersionRange.Parse(requirement["requestedVersionRange"]!.GetValue<string>()).IsSatisfiedBy(version)
                || (!requirement["includePrerelease"]!.GetValue<bool>() && version.Prerelease is not null)))
                reason = "package-range-mismatch";
            else if (requirements.Any(requirement => fields.Any(field => !JsonNode.DeepEquals(requirement[field], candidate[field]))))
                reason = "rpc-identity-mismatch";
            AssertJsonEqual(new JsonObject
            {
                ["result"] = reason is null ? "success" : "rejected",
                ["reason"] = reason,
                ["presence"] = presence,
            }, vector.GetProperty("expected"), FixtureId(vector));
        }
    }

    [Fact]
    public void ArtifactLocalSource_DoesNotPreventExactOwnerAndReexportProvenanceCoalescing()
    {
        using var expectations = OpenExpectations();
        var oracle = expectations.RootElement.GetProperty("artifactLocalProvenanceCoalescing");
        Assert.Equal("outcome-oracle", oracle.GetProperty("status").GetString());
        Assert.Equal("deferred", oracle.GetProperty("productionExecution").GetString());
        Assert.Equal("local-to-each-selected-artifact", oracle.GetProperty("artifactSourceScope").GetString());
        var fields = new[] { "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256",
            "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" };
        Assert.Equal(fields, oracle.GetProperty("agreementRequired").EnumerateArray().Select(StringValue));
        using var inventory = OpenMetadata(oracle.GetProperty("inventoryPath").GetString()!);
        var owner = JsonNode.Parse(inventory.RootElement.GetProperty("contracts")[0].GetRawText())!.AsObject();
        Assert.Equal("owned", owner["artifactSource"]!.GetValue<string>());
        Assert.Equal("package", owner["authorityKind"]!.GetValue<string>());
        Assert.Equal(inventory.RootElement.GetProperty("packageId").GetString(), owner["authorityId"]!.GetValue<string>());
        Assert.Equal(inventory.RootElement.GetProperty("packageVersion").GetString(), owner["selectedAuthorityRelease"]!.GetValue<string>());
        Assert.Equal(inventory.RootElement.GetProperty("packageContentIdentity").GetString(), owner["selectedAuthorityContentIdentity"]!.GetValue<string>());
        var artifact = oracle.GetProperty("reexportArtifact");
        Assert.NotEqual(inventory.RootElement.GetProperty("packageId").GetString(), artifact.GetProperty("packageId").GetString());
        var vectors = oracle.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.Equal(5, vectors.Length);
        foreach (var vector in vectors)
        {
            var reexport = owner.DeepClone().AsObject();
            reexport["artifactSource"] = "imported";
            foreach (var property in vector.GetProperty("recordOverrides").EnumerateObject())
                reexport[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            var localSourceValid = reexport["artifactSource"]!.GetValue<string>() == "imported"
                || (reexport["artifactSource"]!.GetValue<string>() == "owned"
                    && reexport["authorityKind"]!.GetValue<string>() == "package"
                    && reexport["authorityId"]!.GetValue<string>() == artifact.GetProperty("packageId").GetString()
                    && reexport["selectedAuthorityRelease"]!.GetValue<string>() == artifact.GetProperty("packageVersion").GetString()
                    && reexport["selectedAuthorityContentIdentity"]!.GetValue<string>() == artifact.GetProperty("packageContentIdentity").GetString());
            var coalesces = localSourceValid && fields.All(field => JsonNode.DeepEquals(reexport[field], owner[field]));
            Assert.True(coalesces == vector.GetProperty("coalesces").GetBoolean(), FixtureId(vector));
            if (coalesces)
            {
                Assert.NotEqual(owner["artifactSource"]!.GetValue<string>(), reexport["artifactSource"]!.GetValue<string>());
                Assert.Equal("owned", inventory.RootElement.GetProperty("contracts")[0].GetProperty("artifactSource").GetString());
            }
        }
    }

    [Fact]
    public void AuthoringParityExecutableShapes_AreBackedBySchemasArtifactsAndExactProjections()
    {
        using var expectations = OpenExpectations();
        var root = expectations.RootElement;
        var shapes = root.GetProperty("executableShapes").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "same-package-app-use", "whole-package-public-api-import", "explicit-package-provider-import", "explicit-platform-import" },
            shapes.Select(FixtureId));

        foreach (var shape in shapes)
        {
            Assert.Equal("artifact-and-projection-oracle", shape.GetProperty("status").GetString());
            Assert.Equal("deferred", shape.GetProperty("frontendExecution").GetString());
            foreach (var property in shape.GetProperty("input").EnumerateObject().Where(property => property.Name.EndsWith("Schema", StringComparison.Ordinal)))
                AssertReferencedSchema(property.Value.GetString()!);
        }

        AssertSamePackageShape(root, shapes[0]);
        AssertWholePackageShape(shapes[1]);
        AssertPackageProviderShape(shapes[2]);
        AssertPlatformImportShape(shapes[3]);
    }

    private static void AssertFileIndex(
        JsonElement root,
        JsonElement[] validFixtures,
        JsonElement[] invalidFixtures,
        HashSet<string> indexedSources)
    {
        var index = root.GetProperty("fileIndex");
        AssertExactProperties(index, "root", "sourceFileCount", "validFixtureCount", "invalidFixtureCount", "canonicalOutputCount", "sourceFiles", "canonicalOutputs", "valid", "invalid");
        Assert.Equal("expectations.json", index.GetProperty("root").GetString());
        Assert.Equal(60, index.GetProperty("sourceFileCount").GetInt32());
        Assert.Equal(validFixtures.Length, index.GetProperty("validFixtureCount").GetInt32());
        Assert.Equal(invalidFixtures.Length, index.GetProperty("invalidFixtureCount").GetInt32());

        var indexedValid = index.GetProperty("valid").EnumerateArray().ToArray();
        Assert.Equal(validFixtures.Length, indexedValid.Length);
        for (var i = 0; i < validFixtures.Length; i++)
        {
            AssertExactProperties(indexedValid[i], "fixtureId", "sources");
            Assert.Equal(FixtureId(validFixtures[i]), indexedValid[i].GetProperty("fixtureId").GetString());
            AssertJsonEqual(validFixtures[i].GetProperty("sources"), indexedValid[i].GetProperty("sources"), "valid fileIndex projection");
        }

        var indexedInvalid = index.GetProperty("invalid").EnumerateArray().ToArray();
        Assert.Equal(invalidFixtures.Length, indexedInvalid.Length);
        for (var i = 0; i < invalidFixtures.Length; i++)
        {
            AssertExactProperties(indexedInvalid[i], "fixtureId", "diagnosticId", "sources");
            Assert.Equal(FixtureId(invalidFixtures[i]), indexedInvalid[i].GetProperty("fixtureId").GetString());
            Assert.Equal(invalidFixtures[i].GetProperty("diagnosticId").GetString(), indexedInvalid[i].GetProperty("diagnosticId").GetString());
            var projectedSources = new JsonObject();
            foreach (var source in invalidFixtures[i].GetProperty("sources").EnumerateArray())
                projectedSources[source.GetProperty("language").GetString()!] = source.GetProperty("path").GetString();
            AssertJsonEqual(projectedSources, indexedInvalid[i].GetProperty("sources"), "invalid fileIndex projection");
        }

        var sourceFiles = index.GetProperty("sourceFiles").EnumerateArray().ToArray();
        Assert.Equal(index.GetProperty("sourceFileCount").GetInt32(), sourceFiles.Length);
        Assert.Equal(indexedSources.Count, sourceFiles.Length);
        var sourcePaths = sourceFiles.Select(file => file.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(sourcePaths.Order(StringComparer.Ordinal), sourcePaths);
        Assert.Equal(sourcePaths.Length, sourcePaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(indexedSources.Order(StringComparer.Ordinal), sourcePaths);
        foreach (var source in sourceFiles)
        {
            AssertExactProperties(source, "path", "byteLength", "sha256");
            var relativePath = source.GetProperty("path").GetString()!;
            var bytes = File.ReadAllBytes(ResolveParityPath(relativePath));
            Assert.Equal(source.GetProperty("byteLength").GetInt32(), bytes.Length);
            Assert.Equal(source.GetProperty("sha256").GetString(), Sha256(bytes));
        }

        var actualSources = Directory.EnumerateFiles(ParityRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ParityRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !string.Equals(path, "expectations.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(sourcePaths, actualSources);

        var expectedOutputs = validFixtures
            .SelectMany(fixture => fixture.GetProperty("contracts").EnumerateArray().Select(contract => (Fixture: fixture, Contract: contract)))
            .ToArray();
        var indexedOutputs = index.GetProperty("canonicalOutputs").EnumerateArray().ToArray();
        Assert.Equal(7, index.GetProperty("canonicalOutputCount").GetInt32());
        Assert.Equal(7, indexedOutputs.Length);
        Assert.Equal(expectedOutputs.Length, indexedOutputs.Length);
        for (var i = 0; i < expectedOutputs.Length; i++)
        {
            var indexed = indexedOutputs[i];
            var contract = expectedOutputs[i].Contract;
            AssertExactProperties(indexed, "fixtureId", "contractKey", "canonicalDescriptorBytes", "descriptorSha256", "canonicalAbiProjectionBytes", "abiSha256");
            Assert.Equal(FixtureId(expectedOutputs[i].Fixture), indexed.GetProperty("fixtureId").GetString());
            foreach (var property in new[] { "contractKey", "canonicalDescriptorBytes", "descriptorSha256", "canonicalAbiProjectionBytes", "abiSha256" })
                AssertJsonEqual(contract.GetProperty(property), indexed.GetProperty(property), $"canonical output {property}");
        }
    }

    private static void AssertFixtureInventories(JsonElement fixture, string[] contractKeys)
    {
        var contractKeySet = contractKeys.ToHashSet(StringComparer.Ordinal);
        AssertUniqueOrdered(fixture.GetProperty("providers"), "role", "providerId");
        foreach (var provider in fixture.GetProperty("providers").EnumerateArray())
        {
            AssertExactProperties(provider, "providerId", "role", "audience", "contractKey");
            Assert.Equal("runtime", provider.GetProperty("role").GetString());
            Assert.Contains(provider.GetProperty("contractKey").GetString()!, contractKeySet);
            var providerId = provider.GetProperty("providerId").GetString()!;
            Assert.InRange(providerId.Length, 1, 128);
            var contract = fixture.GetProperty("contracts").EnumerateArray().Single(item => ContractKey(item) == provider.GetProperty("contractKey").GetString());
            if (contract.GetProperty("exposure").GetString() == "package") Assert.Equal("owner", provider.GetProperty("audience").GetString());
        }

        AssertUniqueOrdered(fixture.GetProperty("uses"), "role", "contractKey");
        Assert.Empty(fixture.GetProperty("uses").EnumerateArray());

        var bindings = fixture.GetProperty("generatedBindings").EnumerateArray().ToArray();
        Assert.Equal(contractKeys.Length, bindings.Length);
        for (var i = 0; i < bindings.Length; i++)
        {
            AssertExactProperties(bindings[i], "contractKey", "csharpLogicalName");
            Assert.Equal(contractKeys[i], bindings[i].GetProperty("contractKey").GetString());
            Assert.Equal("Sunder.Generated.Rpc." + PascalFromKebab(contractKeys[i]), bindings[i].GetProperty("csharpLogicalName").GetString());
        }
    }

    private static void AssertFrozenProviderInventory(JsonElement[] fixtures)
    {
        var actual = fixtures.SelectMany(fixture => fixture.GetProperty("providers").EnumerateArray().Select(provider =>
            string.Join('|', FixtureId(fixture), provider.GetProperty("providerId").GetString(), provider.GetProperty("role").GetString(), provider.GetProperty("audience").GetString(), provider.GetProperty("contractKey").GetString())));
        Assert.Equal(
            new[]
            {
                "complete|example.authoring.parity.provider|runtime|owner|parity",
                "provider-forms|example.authoring.extensions.clock.owner|runtime|owner|clock",
                "provider-forms|example.authoring.extensions.clock.primary|runtime|public|clock",
                "naming-defaults|example.naming.o.auth2.provider|runtime|owner|o-auth2",
                "naming-defaults|example.naming.url.lookup.provider|runtime|owner|url-lookup",
                "identity-limits|example.boundary.provider.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.provider|runtime|owner|provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            actual);
    }

    private static void AssertPackageToolCommandUx(JsonElement ux)
    {
        AssertExactProperties(ux, "status", "toolExecution", "commandOwner", "excludedOwner", "commands", "packageSelectionOptions", "msbuildProperties", "selectionRules");
        Assert.Equal("command-grammar-oracle", ux.GetProperty("status").GetString());
        Assert.Equal("deferred", ux.GetProperty("toolExecution").GetString());
        Assert.Equal("package-tools", ux.GetProperty("commandOwner").GetString());
        Assert.Equal("Sunder.Cli", ux.GetProperty("excludedOwner").GetString());

        var actualCommands = new List<string>();
        foreach (var command in ux.GetProperty("commands").EnumerateArray())
        {
            AssertExactProperties(command, "family", "operation", "positionals", "syntax", "msbuildTarget");
            var positionals = command.GetProperty("positionals").EnumerateArray().ToArray();
            foreach (var positional in positionals)
            {
                AssertExactProperties(positional, "name", "cardinality");
                Assert.Contains(positional.GetProperty("cardinality").GetString(), new[] { "required", "optional" });
            }

            var arguments = positionals.Select(positional =>
            {
                var name = positional.GetProperty("name").GetString();
                return positional.GetProperty("cardinality").GetString() == "required" ? $"<{name}>" : $"[{name}]";
            }).ToArray();
            var prefix = $"sunder-package {command.GetProperty("family").GetString()} {command.GetProperty("operation").GetString()}";
            Assert.Equal(prefix + (arguments.Length == 0 ? string.Empty : " " + string.Join(" ", arguments)), command.GetProperty("syntax").GetString());
            var positionalSummary = string.Join(',', positionals.Select(positional => positional.GetProperty("name").GetString() + ":" + positional.GetProperty("cardinality").GetString()));
            actualCommands.Add(string.Join('|', command.GetProperty("family").GetString(), command.GetProperty("operation").GetString(), positionalSummary, command.GetProperty("syntax").GetString(), command.GetProperty("msbuildTarget").GetString()));
        }
        Assert.Equal(
            new[]
            {
                "api|add|package:required|sunder-package api add <package>|AddSunderPackageApi",
                "api|update|package:optional|sunder-package api update [package]|UpdateSunderPackageApi",
                "api|remove|package:required|sunder-package api remove <package>|RemoveSunderPackageApi",
                "api|list||sunder-package api list|ListSunderPackageApis",
                "api|require|package:required,contract:required|sunder-package api require <package> <contract>|RequireSunderPackageApi",
                "api|unrequire|package:required,contract:required|sunder-package api unrequire <package> <contract>|UnrequireSunderPackageApi",
                "api|implement|package:required,contract:required|sunder-package api implement <package> <contract>|ImplementSunderPackageApi",
                "api|unimplement|package:required,contract:required|sunder-package api unimplement <package> <contract>|UnimplementSunderPackageApi",
                "platform|implement|contract:required|sunder-package platform implement <contract>|ImplementSunderPlatformApi",
                "platform|update|contract:optional|sunder-package platform update [contract]|UpdateSunderPlatformApi",
                "platform|unimplement|contract:required|sunder-package platform unimplement <contract>|UnimplementSunderPlatformApi",
                "platform|list||sunder-package platform list|ListSunderPlatformApis",
                "contracts|restore||sunder-package contracts restore|RestoreSunderContracts",
                "contracts|check||sunder-package contracts check|CheckSunderContracts",
                "contracts|accept|contract-key:optional|sunder-package contracts accept [contract-key]|AcceptSunderContracts",
                "contracts|freeze|contract-key:optional|sunder-package contracts freeze [contract-key]|FreezeSunderContracts",
                "contracts|publication import|contract-key:optional|sunder-package contracts publication import [contract-key]|ImportSunderContractPublication",
            },
            actualCommands);

        var packageSelectionOptions = ux.GetProperty("packageSelectionOptions").EnumerateArray().Select(StringValue).ToArray();
        Assert.Equal(
            new[] { "--version", "--registry", "--candidate", "--local-name", "--presence", "--include-prerelease", "--exclude-prerelease" },
            packageSelectionOptions);
        Assert.Equal(
            new[]
            {
                "SunderPackageId",
                "SunderContractId",
                "SunderContractKey",
                "SunderPackageVersionRange",
                "SunderPackageIncludePrerelease",
                "SunderRegistryOrigin",
                "SunderPackageCandidatePath",
                "SunderPlatformCatalogPath",
                "SunderPackageApiLocalName",
                "SunderPackageApiPresence",
                "SunderContractCurrentVersion",
                "SunderContractNextVersion",
                "SunderAcknowledgeBreakingChange",
            },
            ux.GetProperty("msbuildProperties").EnumerateArray().Select(StringValue));

        var rules = ux.GetProperty("selectionRules");
        AssertExactProperties(rules, "newRegistryBackedSelection", "lockedOrigin", "globalUpdate", "prerelease", "presence", "nullableMsbuildPropertyOmission");
        var selection = rules.GetProperty("newRegistryBackedSelection");
        AssertExactProperties(selection, "requiredOption", "requiredProperty", "candidateOption", "candidateWaivesRegistryRequirement");
        Assert.Equal("--registry", selection.GetProperty("requiredOption").GetString());
        Assert.Equal("SunderRegistryOrigin", selection.GetProperty("requiredProperty").GetString());
        Assert.Equal("--candidate", selection.GetProperty("candidateOption").GetString());
        Assert.False(selection.GetProperty("candidateWaivesRegistryRequirement").GetBoolean());

        var lockedOrigin = rules.GetProperty("lockedOrigin");
        AssertExactProperties(lockedOrigin, "operations", "overrideOption", "overrideProperty", "overrideAllowed");
        Assert.Equal(new[] { "update", "restore" }, lockedOrigin.GetProperty("operations").EnumerateArray().Select(StringValue));
        Assert.Equal("--registry", lockedOrigin.GetProperty("overrideOption").GetString());
        Assert.Equal("SunderRegistryOrigin", lockedOrigin.GetProperty("overrideProperty").GetString());
        Assert.False(lockedOrigin.GetProperty("overrideAllowed").GetBoolean());

        var globalUpdate = rules.GetProperty("globalUpdate");
        AssertExactProperties(globalUpdate, "command", "rejectedOptionCategories", "rejectedOptions");
        Assert.Equal("sunder-package api update", globalUpdate.GetProperty("command").GetString());
        Assert.Equal(new[] { "version", "prerelease", "origin", "candidate", "local-name", "presence" }, globalUpdate.GetProperty("rejectedOptionCategories").EnumerateArray().Select(StringValue));
        Assert.Equal(packageSelectionOptions, globalUpdate.GetProperty("rejectedOptions").EnumerateArray().Select(StringValue));

        var prerelease = rules.GetProperty("prerelease");
        AssertExactProperties(prerelease, "includeOption", "excludeOption", "optionsMutuallyExclusive", "msbuildProperty", "updateOmission", "createOmission");
        Assert.Equal("--include-prerelease", prerelease.GetProperty("includeOption").GetString());
        Assert.Equal("--exclude-prerelease", prerelease.GetProperty("excludeOption").GetString());
        Assert.True(prerelease.GetProperty("optionsMutuallyExclusive").GetBoolean());
        Assert.Equal("SunderPackageIncludePrerelease", prerelease.GetProperty("msbuildProperty").GetString());
        Assert.Equal("retain-locked-policy", prerelease.GetProperty("updateOmission").GetString());
        Assert.False(prerelease.GetProperty("createOmission").GetBoolean());

        var presence = rules.GetProperty("presence");
        AssertExactProperties(presence, "operations", "option", "allowedValues", "msbuildProperty", "addOmission", "targetedUpdateOmission", "globalUpdate", "missingLockMember", "requiredContractsIndependent");
        Assert.Equal(new[] { "api add <package>", "api update <package>" }, presence.GetProperty("operations").EnumerateArray().Select(StringValue));
        Assert.Equal("--presence", presence.GetProperty("option").GetString());
        Assert.Equal(new[] { "required", "optional" }, presence.GetProperty("allowedValues").EnumerateArray().Select(StringValue));
        Assert.Equal("SunderPackageApiPresence", presence.GetProperty("msbuildProperty").GetString());
        Assert.Equal("write-required", presence.GetProperty("addOmission").GetString());
        Assert.Equal("retain-locked-value", presence.GetProperty("targetedUpdateOmission").GetString());
        Assert.Equal("reject", presence.GetProperty("globalUpdate").GetString());
        Assert.Equal("invalid-no-default", presence.GetProperty("missingLockMember").GetString());
        Assert.True(presence.GetProperty("requiredContractsIndependent").GetBoolean());
        Assert.Equal("absent", rules.GetProperty("nullableMsbuildPropertyOmission").GetString());
    }

    private static void AssertSelectedPackageProviderResolution(JsonElement root)
    {
        var resolution = root.GetProperty("selectedPackageProviderResolution");
        AssertExactProperties(resolution, "status", "frontendExecution", "resolverMembers", "filter", "discovery", "restoreEvidence", "selection", "baseProvider", "predicateVectors", "outcomes", "neverChooseAmongMultipleMatches", "retry");
        Assert.Equal("outcome-oracle", resolution.GetProperty("status").GetString());
        Assert.Equal("deferred", resolution.GetProperty("frontendExecution").GetString());

        var members = resolution.GetProperty("resolverMembers");
        AssertExactProperties(members, "csharp", "node");
        Assert.Equal("ResolveSelectedPackageAsync", members.GetProperty("csharp").GetString());
        Assert.Equal("resolveSelectedPackage", members.GetProperty("node").GetString());
        var generatedApi = root.GetProperty("namingAndDefaults").GetProperty("generatedApiVectors");
        Assert.Contains(members.GetProperty("csharp").GetString(), generatedApi.GetProperty("csharpContractMembers").EnumerateArray().Select(StringValue));
        Assert.Contains(members.GetProperty("node").GetString(), generatedApi.GetProperty("nodeContractMembers").EnumerateArray().Select(StringValue));

        var filter = resolution.GetProperty("filter");
        AssertExactProperties(filter, "packageId", "packageVersion", "prerelease", "state", "exactContractFields", "packageContentIdentity");
        Assert.Equal("exact", filter.GetProperty("packageId").GetString());
        Assert.Equal("requestedVersionRange-semver-precedence", filter.GetProperty("packageVersion").GetString());
        Assert.Equal("includePrerelease-explicit-opt-in", filter.GetProperty("prerelease").GetString());
        Assert.Equal("Active", filter.GetProperty("state").GetString());
        var exactContractFields = new[] { "contractId", "contractVersion", "descriptorSha256", "abiSha256" };
        Assert.Equal(exactContractFields, filter.GetProperty("exactContractFields").EnumerateArray().Select(StringValue));
        Assert.Equal("not-a-routing-filter", filter.GetProperty("packageContentIdentity").GetString());
        Assert.Equal("all-authorized-exact-contract-providers-without-package-range-or-active-filter", resolution.GetProperty("discovery").GetString());
        Assert.Equal("exact-selected-artifact-content-and-inventory-not-runtime-routing", resolution.GetProperty("restoreEvidence").GetString());

        // Execute the predicate oracle, not the still-gated production resolver.
        var selection = resolution.GetProperty("selection");
        foreach (var vector in resolution.GetProperty("predicateVectors").EnumerateArray())
        {
            var provider = JsonNode.Parse(resolution.GetProperty("baseProvider").GetRawText())!.AsObject();
            foreach (var property in vector.GetProperty("providerOverrides").EnumerateObject())
                provider[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            var discoverable = provider["authorized"]!.GetValue<bool>()
                && exactContractFields.All(field => provider[field]!.GetValue<string>() == selection.GetProperty(field).GetString());
            var range = vector.TryGetProperty("requestedVersionRange", out var requestedRange)
                ? requestedRange.GetString()! : selection.GetProperty("requestedVersionRange").GetString()!;
            var includePrerelease = vector.TryGetProperty("includePrerelease", out var policy)
                ? policy.GetBoolean() : selection.GetProperty("includePrerelease").GetBoolean();
            var version = SemanticVersion.Parse(provider["packageVersion"]!.GetValue<string>());
            var state = Enum.Parse<SunderRpcProviderState>(provider["state"]!.GetValue<string>());
            var selected = discoverable
                && provider["packageId"]!.GetValue<string>() == selection.GetProperty("packageId").GetString()
                && PackageVersionRange.Parse(range).IsSatisfiedBy(version)
                && (includePrerelease || version.Prerelease is null)
                && state == SunderRpcProviderState.Active;
            Assert.True(discoverable == vector.GetProperty("discoverable").GetBoolean(), FixtureId(vector));
            Assert.True(selected == vector.GetProperty("selected").GetBoolean(), FixtureId(vector));
        }

        var outcomes = resolution.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(3, outcomes.Length);
        AssertProviderResolutionOutcome(outcomes[0], "zero", 0, 0, "error", "rpc.provider-not-found", "none");
        AssertProviderResolutionOutcome(outcomes[1], "one", 1, 1, "success", null, "sole-match");
        AssertProviderResolutionOutcome(outcomes[2], "multiple", 2, null, "error", "rpc.provider-ambiguous", "none");
        Assert.True(resolution.GetProperty("neverChooseAmongMultipleMatches").GetBoolean());
        Assert.False(resolution.GetProperty("retry").GetBoolean());
    }

    private static void AssertPackagePresenceAndReadiness(JsonElement root)
    {
        var oracle = root.GetProperty("packagePresenceAndReadiness");
        AssertExactProperties(oracle, "status", "frontendExecution", "allowedPresence", "lockToReceiptProjection", "dependencyCoalescing", "outcomes", "optionalProviderContinuity");
        Assert.Equal("outcome-and-projection-oracle", oracle.GetProperty("status").GetString());
        Assert.Equal("deferred", oracle.GetProperty("frontendExecution").GetString());
        Assert.Equal(new[] { "required", "optional" }, oracle.GetProperty("allowedPresence").EnumerateArray().Select(StringValue));

        var projection = oracle.GetProperty("lockToReceiptProjection");
        AssertExactProperties(projection, "lockPath", "receiptPath", "packageId", "lockPresence", "receiptPresence", "lockSha256");
        using var lockFile = OpenMetadata(projection.GetProperty("lockPath").GetString()!);
        using var receipt = OpenMetadata(projection.GetProperty("receiptPath").GetString()!);
        var packageId = projection.GetProperty("packageId").GetString()!;
        var lockPresence = lockFile.RootElement.GetProperty("packageImports").GetProperty(packageId).GetProperty("presence").GetString();
        var receiptPresence = FindByStringProperty(receipt.RootElement.GetProperty("packageApiImports"), "packageId", packageId).GetProperty("presence").GetString();
        Assert.Equal(projection.GetProperty("lockPresence").GetString(), lockPresence);
        Assert.Equal(projection.GetProperty("receiptPresence").GetString(), receiptPresence);
        Assert.Equal(lockPresence, receiptPresence);
        var lockSha256 = Sha256(File.ReadAllBytes(ResolveMetadataPath(projection.GetProperty("lockPath").GetString()!)));
        // The parity ledger pins the historical pre-role/projection-pin image. Keep that
        // oracle immutable and separately verify the revised current lock bytes.
        var historical = JsonNode.Parse(lockFile.RootElement.GetRawText())!;
        foreach (var import in historical["packageImports"]!.AsObject())
        {
            Assert.Equal(new[] { "runtime" }, import.Value!["consumerRoles"]!.AsArray().Select(value => value!.GetValue<string>()));
            import.Value.AsObject().Remove("consumerRoles");
        }
        foreach (var artifact in historical["packageArtifacts"]!.AsObject())
        {
            var current = lockFile.RootElement.GetProperty("packageArtifacts").GetProperty(artifact.Key);
            Assert.InRange(current.GetProperty("modelProjectionBytes").GetInt32(), 1, 4_194_304);
            Assert.Matches("^[a-f0-9]{64}$", current.GetProperty("modelProjectionSha256").GetString()!);
            var receiptArtifact = FindByStringProperty(receipt.RootElement.GetProperty("packageArtifacts"), "packageId", artifact.Key);
            Assert.Equal(current.GetProperty("modelProjectionBytes").GetInt32(), receiptArtifact.GetProperty("modelProjectionBytes").GetInt32());
            Assert.Equal(current.GetProperty("modelProjectionSha256").GetString(), receiptArtifact.GetProperty("modelProjectionSha256").GetString());
            artifact.Value!.AsObject().Remove("modelProjectionBytes");
            artifact.Value.AsObject().Remove("modelProjectionSha256");
        }
        var historicalBytes = Encoding.UTF8.GetBytes(historical.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        Assert.Equal(projection.GetProperty("lockSha256").GetString(), Sha256(historicalBytes));
        Assert.Equal("6ea46e7a2e9dcfc2b04f221224cb61434dcc181bc53f540fe3b3f9527f36a93e", lockSha256);
        Assert.Equal(lockSha256, receipt.RootElement.GetProperty("lockSha256").GetString());

        var coalescing = oracle.GetProperty("dependencyCoalescing");
        AssertExactProperties(coalescing, "agreementRequired", "rangeRule", "normalizedRangeRepresentation", "contentIdentityScope", "contributors", "normalized", "rule", "sourcePackageApiPresenceAfterCoalescing");
        Assert.Equal(new[] { "packageId", "exactRpcIdentity" }, coalescing.GetProperty("agreementRequired").EnumerateArray().Select(StringValue));
        Assert.Equal("all-reviewed-ranges-must-hold", coalescing.GetProperty("rangeRule").GetString());
        Assert.Equal("oracle-conjunction-not-manifest-shape", coalescing.GetProperty("normalizedRangeRepresentation").GetString());
        Assert.Equal("restore-provenance-only-not-install-coalescing", coalescing.GetProperty("contentIdentityScope").GetString());
        var contributors = coalescing.GetProperty("contributors").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "package-api-selected-artifact", "package-api-ultimate-authority", "provider-import" },
            contributors.Select(value => value.GetProperty("source").GetString()));
        foreach (var contributor in contributors)
        {
            AssertExactProperties(contributor, "source", "packageId", "versionRange", "reviewedRange", "selectedPackageVersion", "contentIdentity", "exactRpcIdentity", "presence");
            Assert.Contains(contributor.GetProperty("presence").GetString(), new[] { "required", "optional" });
            Assert.True(contributor.GetProperty("reviewedRange").GetBoolean());
            Assert.True(PackageVersionRange.Parse(contributor.GetProperty("versionRange").GetString()!)
                .IsSatisfiedBy(SemanticVersion.Parse(contributor.GetProperty("selectedPackageVersion").GetString()!)));
            Assert.Matches("^[a-f0-9]{64}$", contributor.GetProperty("contentIdentity").GetString()!);
        }
        Assert.NotEqual(contributors[0].GetProperty("contentIdentity").GetString(), contributors[2].GetProperty("contentIdentity").GetString());

        var contributorGroups = contributors.GroupBy(value => value.GetProperty("packageId").GetString()!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var normalized = coalescing.GetProperty("normalized").EnumerateArray().ToArray();
        Assert.Equal(contributorGroups.Length, normalized.Length);
        for (var index = 0; index < contributorGroups.Length; index++)
        {
            var group = contributorGroups[index].ToArray();
            var dependency = normalized[index];
            AssertExactProperties(dependency, "packageId", "versionRanges", "presence");
            foreach (var member in coalescing.GetProperty("agreementRequired").EnumerateArray().Select(StringValue))
            {
                foreach (var contributor in group.Skip(1))
                    AssertJsonEqual(group[0].GetProperty(member), contributor.GetProperty(member), $"dependency agreement {group[0].GetProperty("packageId").GetString()} {member}");
            }
            Assert.Equal(contributorGroups[index].Key, dependency.GetProperty("packageId").GetString());
            Assert.Equal(group.Select(value => value.GetProperty("versionRange").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                dependency.GetProperty("versionRanges").EnumerateArray().Select(StringValue));
            Assert.Equal(group.Any(value => value.GetProperty("presence").GetString() == "required") ? "required" : "optional", dependency.GetProperty("presence").GetString());
        }
        Assert.Equal("required-wins", coalescing.GetProperty("rule").GetString());
        var packageApiContributors = contributors.Where(value => value.GetProperty("source").GetString()!.StartsWith("package-api-", StringComparison.Ordinal)).ToArray();
        var sourcePresence = coalescing.GetProperty("sourcePackageApiPresenceAfterCoalescing").EnumerateArray().ToArray();
        Assert.Equal(packageApiContributors.Length, sourcePresence.Length);
        for (var index = 0; index < packageApiContributors.Length; index++)
        {
            AssertExactProperties(sourcePresence[index], "source", "packageId", "presence");
            foreach (var member in new[] { "source", "packageId", "presence" })
                AssertJsonEqual(packageApiContributors[index].GetProperty(member), sourcePresence[index].GetProperty(member), $"source package API {member}");
            Assert.Equal("optional", sourcePresence[index].GetProperty("presence").GetString());
        }
        Assert.All(contributors.Where(value => value.GetProperty("source").GetString() == "provider-import"), value => Assert.Equal("required", value.GetProperty("presence").GetString()));

        var outcomes = oracle.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(4, outcomes.Length);
        AssertPresenceOutcome(outcomes[0], "required", false, "included", false, "ready", "unavailable");
        AssertPresenceOutcome(outcomes[1], "required", true, "included", false, "blocked", "unavailable");
        AssertPresenceOutcome(outcomes[2], "optional", false, "omitted-absent-or-incompatible", false, "ready", "unavailable");
        AssertPresenceOutcome(outcomes[3], "optional", true, "omitted-absent-or-incompatible", false, "blocked", "unavailable");

        var resolution = root.GetProperty("selectedPackageProviderResolution");
        var selection = resolution.GetProperty("selection");
        var continuity = oracle.GetProperty("optionalProviderContinuity").EnumerateArray().ToArray();
        Assert.Equal(6, continuity.Length);
        foreach (var vector in continuity)
        {
            Assert.Equal("optional", vector.GetProperty("packagePresence").GetString());
            Assert.Equal("omitted", vector.GetProperty("selectedPackageDisposition").GetString());
            var provider = JsonNode.Parse(resolution.GetProperty("baseProvider").GetRawText())!.AsObject();
            foreach (var property in vector.GetProperty("providerOverrides").EnumerateObject())
                provider[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            var available = provider["authorized"]!.GetValue<bool>() && provider["state"]!.GetValue<string>() == "Active"
                && new[] { "contractId", "contractVersion", "descriptorSha256", "abiSha256" }
                    .All(field => provider[field]!.GetValue<string>() == selection.GetProperty(field).GetString());
            var version = SemanticVersion.Parse(provider["packageVersion"]!.GetValue<string>());
            var selected = available && provider["packageId"]!.GetValue<string>() == selection.GetProperty("packageId").GetString()
                && PackageVersionRange.Parse(selection.GetProperty("requestedVersionRange").GetString()!).IsSatisfiedBy(version)
                && (selection.GetProperty("includePrerelease").GetBoolean() || version.Prerelease is null);
            Assert.Equal(!vector.GetProperty("providerRequired").GetBoolean() || available ? "ready" : "blocked", vector.GetProperty("activation").GetString());
            Assert.Equal(selected ? null : "rpc.provider-not-found", vector.GetProperty("selectedPackageResolutionDiagnosticId").GetString());
        }
    }

    private static void AssertPresenceOutcome(JsonElement outcome, string packagePresence, bool providerRequired, string disposition, bool providerAvailable, string activation, string integration)
    {
        AssertExactProperties(outcome, "packagePresence", "providerRequired", "packageDisposition", "authorizedProviderAvailable", "activation", "selectedPackageIntegration");
        Assert.Equal(packagePresence, outcome.GetProperty("packagePresence").GetString());
        Assert.Equal(providerRequired, outcome.GetProperty("providerRequired").GetBoolean());
        Assert.Equal(disposition, outcome.GetProperty("packageDisposition").GetString());
        Assert.Equal(providerAvailable, outcome.GetProperty("authorizedProviderAvailable").GetBoolean());
        Assert.Equal(activation, outcome.GetProperty("activation").GetString());
        Assert.Equal(integration, outcome.GetProperty("selectedPackageIntegration").GetString());
    }

    private static void AssertProviderResolutionOutcome(JsonElement outcome, string cardinality, int minimum, int? maximum, string result, string? diagnosticId, string selection)
    {
        AssertExactProperties(outcome, "matchCardinality", "minimumMatches", "maximumMatches", "result", "diagnosticId", "selection");
        Assert.Equal(cardinality, outcome.GetProperty("matchCardinality").GetString());
        Assert.Equal(minimum, outcome.GetProperty("minimumMatches").GetInt32());
        if (maximum is null) Assert.Equal(JsonValueKind.Null, outcome.GetProperty("maximumMatches").ValueKind);
        else Assert.Equal(maximum.Value, outcome.GetProperty("maximumMatches").GetInt32());
        Assert.Equal(result, outcome.GetProperty("result").GetString());
        if (diagnosticId is null) Assert.Equal(JsonValueKind.Null, outcome.GetProperty("diagnosticId").ValueKind);
        else Assert.Equal(diagnosticId, outcome.GetProperty("diagnosticId").GetString());
        Assert.Equal(selection, outcome.GetProperty("selection").GetString());
    }

    private static void AssertContractIdentity(JsonElement expected)
    {
        var descriptorBytes = Convert.FromBase64String(expected.GetProperty("canonicalDescriptorBase64").GetString()!);
        var abiBytes = Convert.FromBase64String(expected.GetProperty("canonicalAbiProjectionBase64").GetString()!);
        var descriptor = SunderRpcContractDescriptor.Parse(descriptorBytes);

        Assert.Equal(expected.GetProperty("contractId").GetString(), descriptor.ContractId);
        Assert.Equal(expected.GetProperty("contractVersion").GetString(), descriptor.Version);
        Assert.Equal(expected.GetProperty("canonicalDescriptorBytes").GetInt32(), descriptorBytes.Length);
        Assert.Equal(expected.GetProperty("canonicalAbiProjectionBytes").GetInt32(), abiBytes.Length);
        Assert.Equal(expected.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
        Assert.Equal(expected.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
        Assert.Equal(descriptorBytes, descriptor.GetCanonicalUtf8());
        Assert.Equal(abiBytes, descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(expected.GetProperty("descriptorSha256").GetString(), Sha256(descriptorBytes));
    }

    private static void AssertNamingAndDefaults(JsonElement root)
    {
        var naming = root.GetProperty("namingAndDefaults");
        var defaults = naming.GetProperty("identityDefaults");
        AssertExactProperties(defaults, "contractId", "serviceId", "methodId", "providerId", "newContractVersion", "methodIdempotency", "methodErrors", "zeroWireParameterRequestDefinition", "multiParameterRequestDefinition", "implicitValueDefaults");
        Assert.Equal("<canonical-package-id>.<service-contract-suffix>", defaults.GetProperty("contractId").GetString());
        Assert.Equal("<service-key>", defaults.GetProperty("serviceId").GetString());
        Assert.Equal("<method-key>", defaults.GetProperty("methodId").GetString());
        Assert.Equal("<contract-id>.provider", defaults.GetProperty("providerId").GetString());
        Assert.Equal("1.0.0", defaults.GetProperty("newContractVersion").GetString());
        Assert.Equal("none", defaults.GetProperty("methodIdempotency").GetString());
        Assert.Empty(defaults.GetProperty("methodErrors").EnumerateArray());
        Assert.Equal("Unit", defaults.GetProperty("zeroWireParameterRequestDefinition").GetString());
        Assert.Equal("<ServiceDefinitionKey><MethodDefinitionKey>Request", defaults.GetProperty("multiParameterRequestDefinition").GetString());
        Assert.False(defaults.GetProperty("implicitValueDefaults").GetBoolean());

        foreach (var vector in naming.GetProperty("frozenVectors").EnumerateArray())
        {
            var source = vector.GetProperty("source").GetString()!;
            var stripped = source.EndsWith("Service", StringComparison.Ordinal) ? source[..^"Service".Length]
                : source.EndsWith("Async", StringComparison.Ordinal) ? source[..^"Async".Length]
                : source;
            Assert.Equal(stripped, vector.GetProperty("stripped").GetString());
            var tokens = Tokenize(stripped);
            Assert.Equal(string.Join('-', tokens.Select(AsciiLower)), vector.GetProperty("lowerKebab").GetString());
            Assert.Equal(string.Concat(tokens.Select(PascalToken)), vector.GetProperty("pascal").GetString());
            Assert.Equal(AsciiLower(tokens[0]) + string.Concat(tokens.Skip(1).Select(PascalToken)), vector.GetProperty("camel").GetString());
            Assert.Equal(string.Join('.', tokens.Select(AsciiLower)), vector.GetProperty("tokenDot").GetString());
        }

        var escaping = naming.GetProperty("keywordEscaping");
        using var keywords = JsonDocument.Parse(File.ReadAllBytes(ResolveMetadataPath(root.GetProperty("languageBaselines").GetProperty("keywordFixture").GetString()!)));
        var keywordRoot = keywords.RootElement;
        var authoritative = escaping.GetProperty("authoritativeArrays").EnumerateArray().Select(StringValue).ToArray();
        Assert.Equal(new[] { "csharpReservedKeywords", "csharpContextualKeywords", "ecmascriptStrictModuleBindingRestrictions" }, authoritative);
        var csharpKeywords = authoritative.Take(2).SelectMany(name => keywordRoot.GetProperty(name).EnumerateArray().Select(StringValue)).ToHashSet(StringComparer.Ordinal);
        var ecmascriptKeywords = keywordRoot.GetProperty(authoritative[2]).EnumerateArray().Select(StringValue).ToHashSet(StringComparer.Ordinal);
        foreach (var example in escaping.GetProperty("examples").EnumerateArray())
        {
            var source = example.GetProperty("sourceSymbol").GetString()!;
            Assert.Equal(csharpKeywords.Contains(source) ? "@" + source : source, example.GetProperty("csharp").GetString());
            Assert.Equal(ecmascriptKeywords.Contains(source) ? "_" + source : source, example.GetProperty("typescriptJavaScript").GetString());
        }

        var api = naming.GetProperty("generatedApiVectors");
        Assert.Equal("Sunder.Generated.Rpc", api.GetProperty("csharpNamespace").GetString());
        Assert.Equal(PascalFromCamel(api.GetProperty("packageLocalName").GetProperty("input").GetString()!) + "PackageApi", api.GetProperty("packageLocalName").GetProperty("output").GetString());
        Assert.Equal("I" + PascalFromCamel(api.GetProperty("providerImportKey").GetProperty("input").GetString()!) + "Service", api.GetProperty("providerImportKey").GetProperty("output").GetString());
        var importedNaming = api.GetProperty("importedContractNaming");
        Assert.Equal("canonical-contract-id", importedNaming.GetProperty("contractKey").GetString());
        Assert.False(importedNaming.GetProperty("perContractAliases").GetBoolean());
        Assert.Equal(new[] { "packageLocalName", "providerImportKey", "packageVersion", "packageContentIdentity" }, importedNaming.GetProperty("ignoredInputs").EnumerateArray().Select(StringValue));
        Assert.Equal("C + uppercase-first-character-per-dot-segment + underscore-join", importedNaming.GetProperty("encoding").GetString());
        var importSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in importedNaming.GetProperty("vectors").EnumerateArray())
        {
            var contractId = vector.GetProperty("contractId").GetString()!;
            var symbol = ImportedContractSymbol(contractId);
            Assert.Equal(symbol, vector.GetProperty("symbol").GetString());
            Assert.True(importSymbols.Add(symbol));
            Assert.Equal(contractId, AsciiLower(symbol[1..].Replace('_', '.')));
        }
        var shape = api.GetProperty("csharpApiShape");
        Assert.Equal("namespace-level-class", shape.GetProperty("facadeKind").GetString());
        Assert.Equal("Sunder.Generated.Rpc.Imports.<contractSymbol>", shape.GetProperty("bindingNamespace").GetString());
        Assert.Equal("<contractSymbol>", shape.GetProperty("contractProperty").GetString());
        Assert.Equal("Sunder.Generated.Rpc.ImportApis.<contractSymbol>.Contract", shape.GetProperty("contractPropertyType").GetString());
        Assert.Equal(new[] { "Provider", "CatalogSnapshot", "CatalogEvent", "Client", "Contract" }, shape.GetProperty("types").EnumerateArray().Select(StringValue));
        Assert.True(shape.GetProperty("providerImmutable").GetBoolean());
        Assert.False(shape.GetProperty("providerExposesEndpoint").GetBoolean());
        Assert.Equal(new[] { "Revision", "Sequence", "ResetRequired", "IReadOnlyList<Provider> Providers" }, shape.GetProperty("catalogSnapshotMembers").EnumerateArray().Select(StringValue));
        Assert.Equal(new[] { "Revision", "Sequence", "Kind", "Provider? Provider" }, shape.GetProperty("catalogEventMembers").EnumerateArray().Select(StringValue));
        Assert.Equal("S + UpperInitial(serviceId), then escape original _ as _5f, . as _2e, - as _2d", shape.GetProperty("clientServiceProperties").GetString());
        var serviceVectors = api.GetProperty("clientServiceNameVectors");
        AssertUniqueOrdered(serviceVectors, "serviceId");
        var serviceSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in serviceVectors.EnumerateArray())
        {
            var serviceId = vector.GetProperty("serviceId").GetString()!;
            Assert.Matches("^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$", serviceId);
            var symbol = "S" + (char.ToUpperInvariant(serviceId[0]) + serviceId[1..])
                .Replace("_", "_5f", StringComparison.Ordinal)
                .Replace(".", "_2e", StringComparison.Ordinal)
                .Replace("-", "_2d", StringComparison.Ordinal);
            Assert.Equal(symbol, vector.GetProperty("symbol").GetString());
            Assert.True(serviceSymbols.Add(symbol));
            var decoded = Regex.Replace(symbol[1..], "_(?:5f|2e|2d)",
                match => ((char)Convert.ToInt32(match.Value[1..], 16)).ToString());
            Assert.Equal(serviceId, AsciiLower(decoded));
        }
        Assert.Equal("all-descriptor-service-provider-interfaces", shape.GetProperty("providerInterfaceInherits").GetString());
        Assert.Equal(new[]
        {
            "ValueTask<CatalogSnapshot> ListAsync(CancellationToken cancellationToken = default)",
            "ValueTask<Provider?> GetAsync(Provider provider, CancellationToken cancellationToken = default)",
            "IAsyncEnumerable<CatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)",
            "Client CreateClient(ISunderRpcCallScope scope, Provider provider)",
            "ValueTask<Provider> ResolveSelectedPackageAsync(CancellationToken cancellationToken = default)",
        }, shape.GetProperty("contractSignatures").EnumerateArray().Select(StringValue));
        Assert.Equal("exact-activation-endpoint-no-replacement", shape.GetProperty("getRevalidates").GetString());
        Assert.Equal(new[] { "revision", "sequence", "resets", "lifecycle-events" }, shape.GetProperty("watchPreserves").EnumerateArray().Select(StringValue));
        Assert.False(shape.GetProperty("clientCreatesScope").GetBoolean());
        Assert.False(shape.GetProperty("clientDisposesScope").GetBoolean());
        Assert.Equal(new[] { "ListAsync", "GetAsync", "WatchAsync", "CreateClient", "ResolveSelectedPackageAsync" }, api.GetProperty("csharpContractMembers").EnumerateArray().Select(StringValue));
        Assert.Equal(new[] { "packageApis", "rpcImports" }, api.GetProperty("nodeModuleExports").EnumerateArray().Select(StringValue));
        Assert.Equal(new[] { "list", "get", "watch", "createClient", "resolveSelectedPackage" }, api.GetProperty("nodeContractMembers").EnumerateArray().Select(StringValue));
    }

    private static void AssertSourceOrdering(JsonElement root)
    {
        var ordering = root.GetProperty("sourceOrdering");
        var rules = ordering.GetProperty("descriptorArrayRules");
        Assert.Equal(new[] { "string-enum", "untagged-union-variants", "tagged-union-variants", "method-errors" }, rules.GetProperty("sourceOrderPreserved").EnumerateArray().Select(StringValue));
        Assert.Equal(new[] { "numeric-enum" }, rules.GetProperty("ascendingNumericValue").EnumerateArray().Select(StringValue));
        Assert.Equal(new[] { "method.errors", "closed-record.required", "primitive.enum", "nullable.oneOf", "tagged.oneOf" }, ordering.GetProperty("abiSetLikeArrays").EnumerateArray().Select(StringValue));

        var complete = FindFixture(root.GetProperty("validFixtures"), "complete");
        var contract = FindContract(complete, "parity");
        using var descriptor = JsonDocument.Parse(Convert.FromBase64String(contract.GetProperty("canonicalDescriptorBase64").GetString()!));
        using var abi = JsonDocument.Parse(Convert.FromBase64String(contract.GetProperty("canonicalAbiProjectionBase64").GetString()!));
        var vectors = ordering.GetProperty("completeVectors");
        var descriptorDefs = descriptor.RootElement.GetProperty("$defs");
        var abiDefs = abi.RootElement.GetProperty("$defs");

        AssertJsonEqual(vectors.GetProperty("stringEnum").GetProperty("sourceAndDescriptor"), descriptorDefs.GetProperty("StringStatus").GetProperty("enum"), "descriptor string enum order");
        AssertJsonEqual(vectors.GetProperty("stringEnum").GetProperty("abi"), abiDefs.GetProperty("StringStatus").GetProperty("enum"), "ABI string enum order");
        AssertJsonEqual(vectors.GetProperty("numericEnum").GetProperty("descriptor"), descriptorDefs.GetProperty("NumericPriority").GetProperty("enum"), "descriptor numeric enum order");
        AssertJsonEqual(vectors.GetProperty("numericEnum").GetProperty("abiCanonicalJsonByteOrder"), abiDefs.GetProperty("NumericPriority").GetProperty("enum"), "ABI numeric enum byte order");
        Assert.Equal(new[] { 10, -1, 2 }, vectors.GetProperty("numericEnum").GetProperty("source").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(vectors.GetProperty("genericUnionWithExplicitDiscriminator").GetProperty("sourceAndDescriptorTags").EnumerateArray().Select(StringValue), UnionTags(descriptorDefs.GetProperty("Choice").GetProperty("oneOf"), descriptorDefs));
        Assert.Equal(vectors.GetProperty("genericUnionWithExplicitDiscriminator").GetProperty("abiTags").EnumerateArray().Select(StringValue), UnionTags(abiDefs.GetProperty("Choice").GetProperty("oneOf"), abiDefs));
        Assert.Equal(vectors.GetProperty("taggedUnionWithSynthesizedDiscriminator").GetProperty("sourceAndDescriptorTags").EnumerateArray().Select(StringValue), UnionTags(descriptorDefs.GetProperty("Event").GetProperty("oneOf"), descriptorDefs));
        Assert.Equal(vectors.GetProperty("taggedUnionWithSynthesizedDiscriminator").GetProperty("abiTags").EnumerateArray().Select(StringValue), UnionTags(abiDefs.GetProperty("Event").GetProperty("oneOf"), abiDefs));
        AssertJsonEqual(vectors.GetProperty("methodErrors").GetProperty("sourceAndDescriptor"), descriptor.RootElement.GetProperty("services").GetProperty("parity").GetProperty("methods").GetProperty("round-trip").GetProperty("errors"), "descriptor method errors");
        AssertJsonEqual(vectors.GetProperty("methodErrors").GetProperty("abi"), abi.RootElement.GetProperty("services").GetProperty("parity").GetProperty("methods").GetProperty("round-trip").GetProperty("errors"), "ABI method errors");
    }

    private static void AssertIdentityBoundaries(JsonElement root)
    {
        const int maximum = 128;
        var valid = FindFixture(root.GetProperty("validFixtures"), "identity-limits");
        var boundaries = valid.GetProperty("boundaries");
        var contractBoundary = boundaries.GetProperty("contractId");
        var contractId = valid.GetProperty("packageId").GetString() + "." + contractBoundary.GetProperty("sourceContractKey").GetString()!.Replace('-', '.');
        Assert.Equal(contractId, contractBoundary.GetProperty("value").GetString());
        Assert.Equal(maximum, contractId.Length);
        Assert.Equal(maximum, contractBoundary.GetProperty("characters").GetInt32());
        Assert.False(contractBoundary.GetProperty("providerDeclared").GetBoolean());
        Assert.Equal(contractId, FindContract(valid, contractBoundary.GetProperty("sourceContractKey").GetString()!).GetProperty("contractId").GetString());

        var providerBoundary = boundaries.GetProperty("providerId");
        var providerContractId = valid.GetProperty("packageId").GetString() + "." + providerBoundary.GetProperty("sourceContractKey").GetString()!.Replace('-', '.');
        var providerId = providerContractId + ".provider";
        Assert.Equal(providerId, providerBoundary.GetProperty("value").GetString());
        Assert.Equal(maximum, providerId.Length);
        Assert.Equal(maximum, providerBoundary.GetProperty("characters").GetInt32());
        Assert.Contains(valid.GetProperty("providers").EnumerateArray(), provider => provider.GetProperty("providerId").GetString() == providerId);
        AssertIdentitySourcePresence(valid, contractBoundary.GetProperty("sourceContractKey").GetString()!);
        AssertIdentitySourcePresence(valid, providerBoundary.GetProperty("sourceContractKey").GetString()!);

        var overLimit = new[]
        {
            (FixtureId: "source-limit-contract-identity", Kind: "derived-contract-id-characters", Actual: 129),
            (FixtureId: "source-limit-package-id-derived-contract", Kind: "derived-contract-id-characters", Actual: 130),
            (FixtureId: "source-limit-provider-identity", Kind: "derived-provider-id-characters", Actual: 129),
        };
        foreach (var expected in overLimit)
        {
            var fixture = FindFixture(root.GetProperty("invalidFixtures"), expected.FixtureId);
            var limit = fixture.GetProperty("limit");
            Assert.Equal("rpc-authoring.source-limit-exceeded", fixture.GetProperty("diagnosticId").GetString());
            Assert.Equal(expected.Kind, limit.GetProperty("kind").GetString());
            Assert.Equal(maximum, limit.GetProperty("maximum").GetInt32());
            Assert.Equal(expected.Actual, limit.GetProperty("actual").GetInt32());
            var sourceContractKey = limit.GetProperty("sourceContractKey").GetString()!;
            var derivedContract = fixture.GetProperty("packageId").GetString() + "." + sourceContractKey.Replace('-', '.');
            var derived = limit.GetProperty("kind").GetString() == "derived-provider-id-characters" ? derivedContract + ".provider" : derivedContract;
            Assert.Equal(derived, limit.GetProperty("derivedIdentity").GetString());
            Assert.Equal(expected.Actual, derived.Length);
            Assert.True(derived.Length > maximum);
            var csharp = fixture.GetProperty("sources").EnumerateArray().Single(source => source.GetProperty("language").GetString() == "csharp");
            Assert.Equal(PascalFromKebab(sourceContractKey) + "Service", csharp.GetProperty("spans")[0].GetProperty("text").GetString());
        }

        var packageFixture = FindFixture(root.GetProperty("invalidFixtures"), "source-limit-package-id-derived-contract");
        var packageId = packageFixture.GetProperty("packageId").GetString()!;
        var packageBoundary = packageFixture.GetProperty("packageIdBoundary");
        AssertExactProperties(packageBoundary, "kind", "grammar", "maximum", "actual", "expectedValid");
        Assert.Equal("canonical-package-id-characters", packageBoundary.GetProperty("kind").GetString());
        Assert.Equal("lowercase-dot-separated-ascii", packageBoundary.GetProperty("grammar").GetString());
        Assert.Equal(maximum, packageBoundary.GetProperty("maximum").GetInt32());
        Assert.Equal(maximum, packageBoundary.GetProperty("actual").GetInt32());
        Assert.Equal(maximum, packageId.Length);
        Assert.True(packageBoundary.GetProperty("expectedValid").GetBoolean());
        Assert.All(packageId.Split('.'), segment =>
        {
            Assert.NotEmpty(segment);
            Assert.All(segment, character => Assert.True(character is (>= 'a' and <= 'z') or (>= '0' and <= '9')));
        });
    }

    private static void AssertCollisionApplicability(JsonElement root)
    {
        var naming = root.GetProperty("namingAndDefaults");
        foreach (var id in naming.GetProperty("collisionFixtureIds").EnumerateArray().Select(StringValue))
        {
            var fixture = FindFixture(root.GetProperty("invalidFixtures"), id);
            Assert.Contains(fixture.GetProperty("diagnosticId").GetString(), new[] { "rpc-authoring.identity-collision", "rpc-authoring.generated-name-collision" });
            Assert.Contains("csharp", fixture.GetProperty("applicableLanguages").EnumerateArray().Select(StringValue));
        }

        var suffixService = FindFixture(root.GetProperty("invalidFixtures"), "collision-suffix-service");
        var suffixAsync = FindFixture(root.GetProperty("invalidFixtures"), "collision-suffix-async");
        var synthetic = FindFixture(root.GetProperty("invalidFixtures"), "collision-synthetic-request");
        foreach (var fixture in new[] { suffixService, suffixAsync, synthetic })
        {
            Assert.Equal(new[] { "csharp" }, fixture.GetProperty("applicableLanguages").EnumerateArray().Select(StringValue));
            AssertExactProperties(fixture.GetProperty("nonApplicableControls"), "typescript", "javascript");
        }
        Assert.Equal("example.collision.status", suffixService.GetProperty("expectedIdentity").GetString());
        Assert.Equal("run", suffixAsync.GetProperty("expectedMethodId").GetString());
        Assert.Equal("OrderCreateRequest", synthetic.GetProperty("expectedGeneratedName").GetString());

        var precedenceIds = naming.GetProperty("sourceValidationPrecedenceFixtureIds").EnumerateArray().Select(StringValue).ToArray();
        Assert.Equal(new[] { "collision-post-escape-reserved" }, precedenceIds);
        var precedence = FindFixture(root.GetProperty("invalidFixtures"), precedenceIds[0]);
        Assert.Equal("rpc-authoring.value-invalid", precedence.GetProperty("diagnosticId").GetString());
        Assert.Equal("_await", precedence.GetProperty("expectedInvalidSourceName").GetString());
        foreach (var vectors in precedence.GetProperty("generatedNameVectors").EnumerateObject())
        {
            var invalid = vectors.Value.EnumerateArray().Single(vector => vector.GetProperty("source").GetString() == "_await");
            Assert.Equal(JsonValueKind.Null, invalid.GetProperty("generated").ValueKind);
            Assert.Equal("invalid-initial-character", invalid.GetProperty("outcome").GetString());
        }
    }

    private static void AssertLimitMaterialization(JsonElement fixture, JsonElement[] cases)
    {
        var id = FixtureId(fixture);
        var recipe = fixture.GetProperty("recipe");
        switch (id)
        {
            case "normalized-source-bytes":
                Assert.Equal(120, recipe.GetProperty("paddingByte").GetInt32());
                foreach (var language in fixture.GetProperty("languages").EnumerateArray().Select(StringValue))
                {
                    var seed = NormalizeSource(File.ReadAllText(ResolveParityPath(recipe.GetProperty("seedByLanguage").GetProperty(language).GetString()!), Encoding.UTF8));
                    var replacement = recipe.GetProperty("replacementByLanguage").GetProperty(language);
                    var find = replacement.GetProperty("findFirst").GetString()!;
                    var template = replacement.GetProperty("replaceWith").GetString()!;
                    Assert.True(seed.IndexOf(find, StringComparison.Ordinal) >= 0, language);
                    foreach (var @case in cases)
                    {
                        var target = @case.GetProperty("counterValue").GetInt32();
                        var empty = ReplaceFirst(seed, find, template.Replace("<padding>", string.Empty, StringComparison.Ordinal));
                        var paddingLength = target - Encoding.UTF8.GetByteCount(empty);
                        Assert.True(paddingLength >= 0, language);
                        var materialized = ReplaceFirst(seed, find, template.Replace("<padding>", new string('x', paddingLength), StringComparison.Ordinal));
                        Assert.Equal(target, Encoding.UTF8.GetByteCount(materialized));
                    }
                }
                break;
            case "visited-nodes":
                Assert.StartsWith("Deferred:", recipe.GetProperty("frontendMapping").GetString()!, StringComparison.Ordinal);
                foreach (var @case in cases)
                {
                    var count = @case.GetProperty("counterValue").GetInt32();
                    var buckets = new int[recipe.GetProperty("contractCount").GetInt32()];
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < count; i++)
                    {
                        buckets[i % buckets.Length]++;
                        Assert.True(keys.Add("Visit" + i.ToString("D5", CultureInfo.InvariantCulture)));
                    }
                    Assert.Equal(count, buckets.Sum());
                    Assert.True(buckets.Max() - buckets.Min() <= 1);
                }
                break;
            case "traversal-reference-depth":
                foreach (var @case in cases)
                {
                    var count = @case.GetProperty("counterValue").GetInt32();
                    var definitions = Enumerable.Range(0, count).Select(index => "Depth" + index.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
                    Assert.Equal(count, definitions.Length);
                    Assert.Equal(count, definitions.Distinct(StringComparer.Ordinal).Count());
                    Assert.Equal(count - 1, definitions.Zip(definitions.Skip(1)).Count());
                }
                break;
            case "resolved-local-constants":
                foreach (var @case in cases)
                {
                    var count = @case.GetProperty("counterValue").GetInt32();
                    var buckets = new int[recipe.GetProperty("contractCount").GetInt32()];
                    var bindings = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < count; i++)
                    {
                        buckets[i % buckets.Length]++;
                        Assert.True(bindings.Add("constant" + i.ToString("D4", CultureInfo.InvariantCulture)));
                    }
                    Assert.Equal(count, buckets.Sum());
                    Assert.True(buckets.Max() <= 256);
                }
                break;
            case "contract-count":
                foreach (var @case in cases)
                {
                    var count = @case.GetProperty("counterValue").GetInt32();
                    var contracts = Enumerable.Range(0, count).Select(index => "contract-" + index.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
                    Assert.Equal(count, contracts.Distinct(StringComparer.Ordinal).Count());
                    Assert.All(contracts, contract => Assert.InRange(("example.limit." + contract.Replace('-', '.')).Length, 1, 128));
                }
                break;
            case "provider-count":
                foreach (var @case in cases)
                {
                    var count = @case.GetProperty("counterValue").GetInt32();
                    var providers = Enumerable.Range(0, count).Select(index => "example.limit.extension.provider" + index.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
                    Assert.Equal(count, providers.Distinct(StringComparer.Ordinal).Count());
                    Assert.All(providers, provider => Assert.InRange(provider.Length, 1, 128));
                }
                break;
            default:
                Assert.Fail($"Unknown source-limit recipe '{id}'.");
                break;
        }
    }

    private static void AssertSamePackageShape(JsonElement root, JsonElement shape)
    {
        var input = shape.GetProperty("input");
        var source = input.GetProperty("sourceFixture");
        var fixture = FindFixture(root.GetProperty("validFixtures"), source.GetProperty("fixtureId").GetString()!);
        var contract = FindContract(fixture, source.GetProperty("contractKey").GetString()!);
        var export = shape.GetProperty("expectedProjectExportContracts").EnumerateArray().Single();
        AssertOwnedContractProjection(export, fixture, contract);

        using var schema = JsonDocument.Parse(File.ReadAllBytes(ResolveMetadataPath(input.GetProperty("projectExportSchema").GetString()!)));
        string[] rootMembers = ["schemaVersion", "producer", "projectIdentity", "configuration", "targetFramework", "inputFingerprint", "lockSha256", "packageId", "packageVersion", "contracts"];
        Assert.Equal(rootMembers, schema.RootElement.GetProperty("required").EnumerateArray().Select(StringValue));
        Assert.Equal(rootMembers.Prepend("roleUses"), schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(rootMembers.Length + 1, schema.RootElement.GetProperty("maxProperties").GetInt32());
        var roleUses = schema.RootElement.GetProperty("properties").GetProperty("roleUses");
        Assert.Equal("array", roleUses.GetProperty("type").GetString());
        Assert.Equal("authoring-receipt.schema.json#/$defs/roleUse", roleUses.GetProperty("items").GetProperty("$ref").GetString());
        var required = schema.RootElement.GetProperty("$defs").GetProperty("exportedContract").GetProperty("required").EnumerateArray().Select(StringValue).Order(StringComparer.Ordinal);
        Assert.False(export.TryGetProperty("modelProjection", out _)); // Frozen source/descriptor oracle remains its original image.
        Assert.Equal(export.EnumerateObject().Select(property => property.Name).Append("modelProjection").Order(StringComparer.Ordinal), required);
        var currentProperties = schema.RootElement.GetProperty("$defs").GetProperty("exportedContract").GetProperty("properties");
        Assert.Equal("rpc-model-projection.schema.json#/$defs/modelProjection", currentProperties.GetProperty("modelProjection").GetProperty("$ref").GetString());
        Assert.Contains("methodPolicies", currentProperties.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("methodPolicies", required);

        var records = shape.GetProperty("expectedRecords");
        AssertReceiptContractProjection(records.GetProperty("contracts").EnumerateArray().Single(), contract);
        Assert.Empty(records.GetProperty("packageArtifacts").EnumerateArray());
        Assert.Single(records.GetProperty("bundles").EnumerateArray());
        var bundle = records.GetProperty("bundles")[0];
        Assert.Equal("owned", bundle.GetProperty("source").GetString());
        Assert.Equal(fixture.GetProperty("packageId").GetString(), bundle.GetProperty("authorityId").GetString());
        foreach (var name in new[] { "packageArtifactImports", "packageApiImports", "providerImports", "platformImports", "providers", "generatedPackageFacades" })
            Assert.Empty(records.GetProperty(name).EnumerateArray());
        AssertJsonEqual(input.GetProperty("appPolicy"), records.GetProperty("uses")[0], "same-package app use");
        Assert.Equal(contract.GetProperty("contractKey").GetString(), records.GetProperty("generatedBindings")[0].GetProperty("contractKey").GetString());
        Assert.Equal(fixture.GetProperty("generatedBindings")[0].GetProperty("csharpLogicalName").GetString(), records.GetProperty("generatedBindings")[0].GetProperty("logicalName").GetString());
    }

    private static void AssertWholePackageShape(JsonElement shape)
    {
        var input = shape.GetProperty("input");
        using var receipt = OpenMetadata(input.GetProperty("recordDonorReceiptPath").GetString()!);
        using var lockFile = OpenMetadata(input.GetProperty("lockPath").GetString()!);
        using var inventory = OpenMetadata(input.GetProperty("publicInventoryPath").GetString()!);
        var records = shape.GetProperty("expectedRecords");
        var selectedKeys = records.GetProperty("contracts").EnumerateArray().Select(ContractKey).ToHashSet(StringComparer.Ordinal);

        AssertFilteredReceipt(receipt.RootElement, records, selectedKeys, includePackageFacade: true);
        AssertBundleAndBinding(records, records.GetProperty("contracts").EnumerateArray().Single(), "imported");
        foreach (var name in new[] { "contracts", "bundles", "generatedBindings" })
            AssertUniqueOrdered(receipt.RootElement.GetProperty(name), "contractKey");
        AssertPackageArtifactMapProjection(lockFile.RootElement.GetProperty("packageArtifacts"), records.GetProperty("packageArtifacts"));
        AssertJsonEqual(ProjectPackageArtifactImports(records.GetProperty("packageArtifacts")), records.GetProperty("packageArtifactImports"), "package artifact import projection");

        var artifact = records.GetProperty("packageArtifacts")[0];
        var packageApiImport = records.GetProperty("packageApiImports").EnumerateArray().Single();
        var lockIntent = lockFile.RootElement.GetProperty("packageImports").GetProperty(artifact.GetProperty("packageId").GetString()!);
        Assert.Equal(lockIntent.GetProperty("presence").GetString(), packageApiImport.GetProperty("presence").GetString());
        using var receiptSchema = OpenMetadata(input.GetProperty("receiptSchema").GetString()!);
        var requiredPackageApiMembers = receiptSchema.RootElement.GetProperty("$defs").GetProperty("packageApiImport").GetProperty("required").EnumerateArray().Select(StringValue).Order(StringComparer.Ordinal);
        Assert.Equal(requiredPackageApiMembers, packageApiImport.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(inventory.RootElement.GetProperty("packageId").GetString(), artifact.GetProperty("packageId").GetString());
        Assert.Equal(inventory.RootElement.GetProperty("packageVersion").GetString(), artifact.GetProperty("selectedPackageVersion").GetString());
        Assert.Equal(inventory.RootElement.GetProperty("packageContentIdentity").GetString(), artifact.GetProperty("selectedPackageContentIdentity").GetString());
        Assert.Equal(inventory.RootElement.GetProperty("publicContractCount").GetInt32(), artifact.GetProperty("publicContractCount").GetInt32());
        foreach (var expectedContract in artifact.GetProperty("contracts").EnumerateArray())
        {
            var actual = inventory.RootElement.GetProperty("contracts").EnumerateArray().Single(contract => contract.GetProperty("contractId").GetString() == expectedContract.GetProperty("contractId").GetString());
            foreach (var property in actual.EnumerateObject())
            {
                var expectedName = property.Name == "contractVersion" ? "resolvedContractVersion" : property.Name;
                AssertJsonEqual(property.Value, expectedContract.GetProperty(expectedName), $"inventory {property.Name}");
            }
        }

        var excluded = shape.GetProperty("excludedPublicInventoryContracts").EnumerateArray().ToArray();
        var platformInventory = inventory.RootElement.GetProperty("contracts").EnumerateArray().Where(contract => contract.GetProperty("authorityKind").GetString() == "platform").ToArray();
        Assert.Equal(platformInventory.Length, excluded.Length);
        for (var i = 0; i < excluded.Length; i++)
        {
            foreach (var name in new[] { "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256" })
                AssertJsonEqual(platformInventory[i].GetProperty(name), excluded[i].GetProperty(name), $"excluded inventory {name}");
            Assert.Equal("authorityKind=platform", excluded[i].GetProperty("reason").GetString());
        }
    }

    private static void AssertPackageProviderShape(JsonElement shape)
    {
        var input = shape.GetProperty("input");
        using var receipt = OpenMetadata(input.GetProperty("recordDonorReceiptPath").GetString()!);
        using var lockBase = OpenMetadata(input.GetProperty("lockBasePath").GetString()!);
        var transformed = ApplyTransformations(lockBase.RootElement, input.GetProperty("lockTransformations"));
        var records = shape.GetProperty("expectedRecords");
        var contract = records.GetProperty("contracts")[0];
        AssertJsonEqual(FindByStringProperty(HistoricalReceiptRecords(receipt.RootElement).GetProperty("contracts"), "contractKey", ContractKey(contract)), contract, "provider-import contract");
        AssertPackageArtifactMapProjection(transformed["packageArtifacts"]!.AsObject(), records.GetProperty("packageArtifacts"));
        AssertJsonEqual(ProjectPackageArtifactImports(records.GetProperty("packageArtifacts")), records.GetProperty("packageArtifactImports"), "provider artifact imports");

        var binding = input.GetProperty("providerBinding");
        var provider = records.GetProperty("providers")[0];
        Assert.Equal(binding.GetProperty("providerId").GetString(), provider.GetProperty("providerId").GetString());
        Assert.Equal(binding.GetProperty("role").GetString(), provider.GetProperty("role").GetString());
        Assert.Equal(binding.GetProperty("audience").GetString(), provider.GetProperty("audience").GetString());
        Assert.Equal(ContractKey(contract), provider.GetProperty("contractKey").GetString());

        var transformedImport = transformed["providerImports"]![binding.GetProperty("key").GetString()!]!;
        var providerImport = records.GetProperty("providerImports")[0];
        Assert.Equal(binding.GetProperty("key").GetString(), providerImport.GetProperty("key").GetString());
        Assert.Equal(transformedImport["packageId"]!.GetValue<string>(), providerImport.GetProperty("packageId").GetString());
        Assert.Equal(transformedImport["contractId"]!.GetValue<string>(), providerImport.GetProperty("contractId").GetString());
        Assert.Equal(contract.GetProperty("contractVersion").GetString(), providerImport.GetProperty("contractVersion").GetString());
        AssertExactProperties(providerImport, "key", "packageId", "contractId", "contractVersion", "bundleRef", "providerRefs");
        Assert.Equal(contract.GetProperty("contractId").GetString(), providerImport.GetProperty("bundleRef").GetProperty("contractId").GetString());
        Assert.Equal(contract.GetProperty("contractVersion").GetString(), providerImport.GetProperty("bundleRef").GetProperty("contractVersion").GetString());
        var providerRef = providerImport.GetProperty("providerRefs").EnumerateArray().Single();
        Assert.Equal(binding.GetProperty("role").GetString(), providerRef.GetProperty("role").GetString());
        Assert.Equal(binding.GetProperty("providerId").GetString(), providerRef.GetProperty("providerId").GetString());
        Assert.Equal(contract.GetProperty("contractId").GetString(), providerRef.GetProperty("contractId").GetString());
        Assert.Equal(contract.GetProperty("contractVersion").GetString(), providerRef.GetProperty("contractVersion").GetString());
        AssertBundleAndBinding(records, contract, "imported");
        AssertImportedBundleFromArtifact(records, contract);
        foreach (var name in new[] { "packageApiImports", "platformImports", "uses", "generatedPackageFacades" }) Assert.Empty(records.GetProperty(name).EnumerateArray());
    }

    private static void AssertPlatformImportShape(JsonElement shape)
    {
        var input = shape.GetProperty("input");
        using var receipt = OpenMetadata(input.GetProperty("recordDonorReceiptPath").GetString()!);
        using var lockFile = OpenMetadata(input.GetProperty("lockPath").GetString()!);
        var transformed = ApplyTransformations(lockFile.RootElement, input.GetProperty("lockTransformations"));
        var records = shape.GetProperty("expectedRecords");
        var contract = records.GetProperty("contracts")[0];
        AssertJsonEqual(FindByStringProperty(HistoricalReceiptRecords(receipt.RootElement).GetProperty("contracts"), "contractKey", ContractKey(contract)), contract, "platform-import contract");
        Assert.Empty(records.GetProperty("packageArtifacts").EnumerateArray());
        Assert.Empty(transformed["packageArtifacts"]!.AsObject());
        Assert.Empty(transformed["packageImports"]!.AsObject());

        var lockKey = input.GetProperty("lockKey").GetString()!;
        var selected = transformed["platformImports"]![lockKey]!.AsObject();
        var expected = records.GetProperty("platformImports")[0];
        Assert.Equal(lockKey, expected.GetProperty("key").GetString());
        foreach (var name in new[] { "authorityKind", "authorityId", "authorityReleaseRange", "includePrerelease", "selectedAuthorityRelease", "selectedAuthorityContentIdentity", "contractId" })
            AssertJsonEqual(selected[name]!, expected.GetProperty(name), $"platform import {name}");
        Assert.Equal(selected["resolvedContractVersion"]!.GetValue<string>(), expected.GetProperty("contractVersion").GetString());
        Assert.Equal(contract.GetProperty("contractId").GetString(), expected.GetProperty("bundleRef").GetProperty("contractId").GetString());
        Assert.Equal(contract.GetProperty("contractVersion").GetString(), expected.GetProperty("bundleRef").GetProperty("contractVersion").GetString());
        AssertBundleAndBinding(records, contract, "imported");
        AssertJsonEqual(FilterByContractKeys(receipt.RootElement.GetProperty("providers"), new[] { ContractKey(contract) }), records.GetProperty("providers"), "platform providers");
        var bundle = records.GetProperty("bundles").EnumerateArray().Single();
        foreach (var name in new[] { "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
            AssertJsonEqual(selected[name]!, bundle.GetProperty(name), $"platform bundle {name}");
        var providerRefs = expected.GetProperty("providerRefs").EnumerateArray().ToArray();
        var providers = records.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(providers.Length, providerRefs.Length);
        for (var i = 0; i < providers.Length; i++)
        {
            Assert.Equal(providers[i].GetProperty("role").GetString(), providerRefs[i].GetProperty("role").GetString());
            Assert.Equal(providers[i].GetProperty("providerId").GetString(), providerRefs[i].GetProperty("providerId").GetString());
            Assert.Equal(contract.GetProperty("contractId").GetString(), providerRefs[i].GetProperty("contractId").GetString());
            Assert.Equal(contract.GetProperty("contractVersion").GetString(), providerRefs[i].GetProperty("contractVersion").GetString());
        }
        foreach (var name in new[] { "packageArtifactImports", "packageApiImports", "providerImports", "uses", "generatedPackageFacades" }) Assert.Empty(records.GetProperty(name).EnumerateArray());
    }

    private static void AssertFilteredReceipt(JsonElement receipt, JsonElement records, HashSet<string> selectedKeys, bool includePackageFacade)
    {
        receipt = HistoricalReceiptRecords(receipt);
        foreach (var name in new[] { "contracts", "bundles", "providers", "uses", "generatedBindings" })
            AssertJsonEqual(FilterByContractKeys(receipt.GetProperty(name), selectedKeys), records.GetProperty(name), $"receipt {name} projection");
        AssertJsonEqual(receipt.GetProperty("packageArtifacts"), records.GetProperty("packageArtifacts"), "receipt packageArtifacts");
        AssertJsonEqual(receipt.GetProperty("packageArtifactImports"), records.GetProperty("packageArtifactImports"), "receipt packageArtifactImports");
        AssertJsonEqual(receipt.GetProperty("packageApiImports"), records.GetProperty("packageApiImports"), "receipt packageApiImports");
        AssertJsonEqual(receipt.GetProperty("providerImports"), records.GetProperty("providerImports"), "receipt providerImports");
        Assert.Empty(records.GetProperty("platformImports").EnumerateArray());
        if (includePackageFacade) AssertJsonEqual(receipt.GetProperty("generatedPackageFacades"), records.GetProperty("generatedPackageFacades"), "receipt generatedPackageFacades");
    }

    private static JsonElement HistoricalReceiptRecords(JsonElement current)
    {
        // Reconstruct only the frozen record shape in memory. The current donor's
        // required metadata is asserted separately; tracked oracle bytes stay intact.
        var historical = JsonNode.Parse(current.GetRawText())!;
        foreach (var contract in historical["contracts"]!.AsArray())
        {
            var projection = contract!["modelProjection"]!.AsObject();
            Assert.Equal(new[] { "models", "serviceReferences" }, projection.Select(pair => pair.Key));
            _ = projection["models"]!.AsObject(); _ = projection["serviceReferences"]!.AsObject();
            contract.AsObject().Remove("modelProjection");
            if (contract["methodPolicies"] is { } policies) Assert.NotEmpty(policies.AsObject());
            contract.AsObject().Remove("methodPolicies");
        }
        foreach (var artifact in historical["packageArtifacts"]!.AsArray())
        {
            Assert.InRange(artifact!["modelProjectionBytes"]!.GetValue<int>(), 1, 4_194_304);
            Assert.Matches("^[a-f0-9]{64}$", artifact["modelProjectionSha256"]!.GetValue<string>());
            artifact.AsObject().Remove("modelProjectionBytes"); artifact.AsObject().Remove("modelProjectionSha256");
        }
        return JsonSerializer.SerializeToElement(historical);
    }

    private static void AssertPackageArtifactMapProjection(JsonElement map, JsonElement array)
    {
        Assert.Equal(map.EnumerateObject().Count(), array.GetArrayLength());
        foreach (var record in array.EnumerateArray())
        {
            var packageId = record.GetProperty("packageId").GetString()!;
            var item = map.GetProperty(packageId);
            Assert.InRange(item.GetProperty("modelProjectionBytes").GetInt32(), 1, 4_194_304);
            Assert.Matches("^[a-f0-9]{64}$", item.GetProperty("modelProjectionSha256").GetString()!);
            foreach (var property in item.EnumerateObject().Where(property => property.Name is not ("contracts" or "modelProjectionBytes" or "modelProjectionSha256")))
                AssertJsonEqual(property.Value, record.GetProperty(property.Name), $"package artifact {packageId}/{property.Name}");
            var contractMap = item.GetProperty("contracts");
            var contracts = record.GetProperty("contracts");
            Assert.Equal(contractMap.EnumerateObject().Count(), contracts.GetArrayLength());
            foreach (var contract in contracts.EnumerateArray())
            {
                var contractId = contract.GetProperty("contractId").GetString()!;
                var mapped = contractMap.GetProperty(contractId);
                foreach (var property in mapped.EnumerateObject())
                    AssertJsonEqual(property.Value, contract.GetProperty(property.Name), $"artifact contract {contractId}/{property.Name}");
            }
        }
    }

    private static void AssertPackageArtifactMapProjection(JsonObject map, JsonElement array)
    {
        using var document = JsonDocument.Parse(map.ToJsonString());
        AssertPackageArtifactMapProjection(document.RootElement, array);
    }

    private static JsonNode ProjectPackageArtifactImports(JsonElement artifacts)
    {
        var result = new JsonArray();
        foreach (var artifact in artifacts.EnumerateArray())
        {
            var projected = new JsonObject();
            foreach (var name in new[] { "packageId", "requestedVersionRange", "includePrerelease", "selectedPackageVersion", "selectedPackageContentIdentity", "publicContractCount", "publicInventorySha256" })
                projected[name] = JsonNode.Parse(artifact.GetProperty(name).GetRawText());
            var constraints = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contract in artifact.GetProperty("contracts").EnumerateArray())
            {
                var identity = string.Join('|', contract.GetProperty("authorityKind").GetString(), contract.GetProperty("authorityId").GetString(), contract.GetProperty("selectedAuthorityRelease").GetString(), contract.GetProperty("selectedAuthorityContentIdentity").GetString());
                if (!seen.Add(identity)) continue;
                var constraint = new JsonObject();
                foreach (var name in new[] { "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
                    if (contract.TryGetProperty(name, out var value)) constraint[name] = JsonNode.Parse(value.GetRawText());
                constraints.Add(constraint);
            }
            projected["authorityConstraints"] = constraints;
            result.Add(projected);
        }
        return result;
    }

    private static void AssertBundleAndBinding(JsonElement records, JsonElement contract, string source)
    {
        var bundle = records.GetProperty("bundles").EnumerateArray().Single();
        Assert.Equal(ContractKey(contract), bundle.GetProperty("contractKey").GetString());
        Assert.Equal(source, bundle.GetProperty("source").GetString());
        var binding = records.GetProperty("generatedBindings").EnumerateArray().Single();
        Assert.Equal(ContractKey(contract), binding.GetProperty("contractKey").GetString());
        var contractId = contract.GetProperty("contractId").GetString()!;
        Assert.Equal("imported", source);
        Assert.Equal(contractId, ContractKey(contract));
        Assert.Equal("Sunder.Generated.Rpc.Imports." + ImportedContractSymbol(contractId), binding.GetProperty("logicalName").GetString());
    }

    private static string ImportedContractSymbol(string contractId)
    {
        Assert.Matches("^[a-z0-9]+(?:\\.[a-z0-9]+)*$", contractId);
        return "C" + string.Join('_', contractId.Split('.').Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
    }

    private static void AssertImportedBundleFromArtifact(JsonElement records, JsonElement contract)
    {
        var artifactContract = records.GetProperty("packageArtifacts").EnumerateArray()
            .SelectMany(artifact => artifact.GetProperty("contracts").EnumerateArray())
            .Single(item => item.GetProperty("contractId").GetString() == contract.GetProperty("contractId").GetString());
        var bundle = records.GetProperty("bundles").EnumerateArray().Single();
        foreach (var name in new[] { "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
            AssertJsonEqual(artifactContract.GetProperty(name), bundle.GetProperty(name), $"package bundle {name}");
    }

    private static void AssertOwnedContractProjection(JsonElement export, JsonElement fixture, JsonElement contract)
    {
        Assert.Equal(ContractKey(contract), export.GetProperty("contractKey").GetString());
        Assert.Equal(contract.GetProperty("contractId").GetString(), export.GetProperty("contractId").GetString());
        Assert.Equal(contract.GetProperty("contractVersion").GetString(), export.GetProperty("contractVersion").GetString());
        Assert.Equal(contract.GetProperty("exposure").GetString(), export.GetProperty("exposure").GetString());
        Assert.Equal("owned", export.GetProperty("source").GetString());
        Assert.Equal("package", export.GetProperty("authorityKind").GetString());
        Assert.Equal(fixture.GetProperty("packageId").GetString(), export.GetProperty("authorityId").GetString());
        Assert.Equal(contract.GetProperty("canonicalDescriptorBytes").GetInt32(), export.GetProperty("descriptorBytes").GetInt32());
        Assert.Equal(Encoding.UTF8.GetString(Convert.FromBase64String(contract.GetProperty("canonicalDescriptorBase64").GetString()!)), export.GetProperty("canonicalDescriptor").GetString());
        Assert.Equal(contract.GetProperty("descriptorSha256").GetString(), export.GetProperty("descriptorSha256").GetString());
        Assert.Equal(contract.GetProperty("abiSha256").GetString(), export.GetProperty("abiSha256").GetString());
    }

    private static void AssertReceiptContractProjection(JsonElement receipt, JsonElement contract)
    {
        Assert.Equal(ContractKey(contract), receipt.GetProperty("contractKey").GetString());
        Assert.Equal(contract.GetProperty("contractId").GetString(), receipt.GetProperty("contractId").GetString());
        Assert.Equal(contract.GetProperty("contractVersion").GetString(), receipt.GetProperty("contractVersion").GetString());
        Assert.Equal(contract.GetProperty("exposure").GetString(), receipt.GetProperty("exposure").GetString());
        Assert.Equal(contract.GetProperty("canonicalDescriptorBytes").GetInt32(), receipt.GetProperty("descriptorBytes").GetInt32());
        Assert.Equal(Encoding.UTF8.GetString(Convert.FromBase64String(contract.GetProperty("canonicalDescriptorBase64").GetString()!)), receipt.GetProperty("canonicalDescriptor").GetString());
        Assert.Equal(contract.GetProperty("descriptorSha256").GetString(), receipt.GetProperty("descriptorSha256").GetString());
        Assert.Equal(contract.GetProperty("abiSha256").GetString(), receipt.GetProperty("abiSha256").GetString());
    }

    private static JsonObject ApplyTransformations(JsonElement source, JsonElement transformations)
    {
        var root = JsonNode.Parse(source.GetRawText())!.AsObject();
        foreach (var transformation in transformations.EnumerateArray())
        {
            var segments = transformation.GetProperty("path").GetString()!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            JsonObject parent = root;
            for (var i = 0; i < segments.Length - 1; i++) parent = parent[segments[i]]!.AsObject();
            var leaf = segments[^1];
            switch (transformation.GetProperty("operation").GetString())
            {
                case "remove":
                    Assert.True(parent.Remove(leaf));
                    break;
                case "set":
                    parent[leaf] = JsonNode.Parse(transformation.GetProperty("value").GetRawText());
                    break;
                default:
                    Assert.Fail("Unknown lock transformation.");
                    break;
            }
        }
        return root;
    }

    private static JsonNode FilterByContractKeys(JsonElement values, IEnumerable<string> contractKeys)
    {
        var selected = contractKeys.ToHashSet(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var value in values.EnumerateArray())
            if (value.TryGetProperty("contractKey", out var key) && selected.Contains(key.GetString()!)) result.Add(JsonNode.Parse(value.GetRawText()));
        return result;
    }

    private static JsonElement FindByStringProperty(JsonElement values, string propertyName, string expected) =>
        values.EnumerateArray().Single(value => value.GetProperty(propertyName).GetString() == expected);

    private static void AssertReferencedSchema(string relativePath)
    {
        using var schema = OpenMetadata(relativePath);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.True(schema.RootElement.TryGetProperty("$id", out _));
        Assert.Equal(JsonValueKind.False, schema.RootElement.GetProperty("additionalProperties").ValueKind);
        Assert.NotEmpty(schema.RootElement.GetProperty("required").EnumerateArray());
    }

    private static void AssertOracleScope(JsonElement scope)
    {
        Assert.Equal("deferred", scope.GetProperty("sourceFrontendExecution").GetString());
        Assert.Equal(
            new[]
            {
                "source byte length and SHA-256",
                "source syntax and duplicate-key hygiene",
                "canonical descriptor and ABI identity",
                "metadata projections and referenced artifact shapes",
                "language-neutral source-limit materialization",
                "package-tool command grammar and selected-provider outcome vectors",
                "package presence projection, dependency coalescing, and readiness outcomes",
                "Compatible Installation reviewed-range and exact-RPC outcome vectors",
                "artifact-local source and exact ultimate-provenance coalescing",
            },
            scope.GetProperty("currentlyExecutable").EnumerateArray().Select(StringValue));
        Assert.Equal(
            new[]
            {
                "C# authoring source compilation",
                "TypeScript/JavaScript authoring static analysis",
                "frontend diagnostic emission",
                "frontend visited-node counter equivalence",
                "package-tool command execution",
                "selected-package provider resolver execution",
                "package presence command, solver, and readiness execution",
                "production Compatible Installation and unreviewed-authority-range migration",
            },
            scope.GetProperty("deferredUntilFrontendsExist").EnumerateArray().Select(StringValue));
    }

    private static void AssertLanguageBaselines(JsonElement baselines)
    {
        Assert.Equal("14.0", baselines.GetProperty("csharpLanguageVersion").GetString());
        Assert.Equal("6.0.3", baselines.GetProperty("typescriptVersion").GetString());
        Assert.Equal("ES2023", baselines.GetProperty("ecmascriptTarget").GetString());
        Assert.True(File.Exists(ResolveMetadataPath(baselines.GetProperty("keywordFixture").GetString()!)));
    }

    private static void AssertLanguageSources(JsonElement sources, HashSet<string> indexedSources)
    {
        AssertExactProperties(sources, "csharp", "typescript", "javascript");
        foreach (var language in new[] { "csharp", "typescript", "javascript" })
        {
            var relativePath = sources.GetProperty(language).GetString()!;
            Assert.Equal(language, LanguageFromPath(relativePath));
            Assert.True(indexedSources.Add(relativePath), $"Parity source '{relativePath}' is indexed more than once.");
            Assert.True(File.Exists(ResolveParityPath(relativePath)), $"Parity source '{relativePath}' is missing.");
        }
    }

    private static void AssertSpans(string relativePath, JsonElement spans)
    {
        var lines = File.ReadAllText(ResolveParityPath(relativePath), Encoding.UTF8).Split('\n');
        foreach (var span in spans.EnumerateArray())
        {
            var startLine = span.GetProperty("startLine").GetInt32();
            var endLine = span.GetProperty("endLine").GetInt32();
            Assert.Equal(startLine, endLine);
            var startColumn = span.GetProperty("startColumn").GetInt32();
            var endColumn = span.GetProperty("endColumn").GetInt32();
            Assert.Equal(span.GetProperty("text").GetString(), lines[startLine - 1][(startColumn - 1)..(endColumn - 1)]);
        }
    }

    private static void AssertUniqueOrdered(JsonElement values, params string[] fields)
    {
        var keys = values.EnumerateArray().Select(value => string.Join('\0', fields.Select(field => value.GetProperty(field).GetString()))).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    private static void AssertLimitCase(JsonElement value, string id, int counter, bool expectedValid, string? diagnosticId)
    {
        Assert.Equal(id, value.GetProperty("id").GetString());
        Assert.Equal(counter, value.GetProperty("counterValue").GetInt32());
        Assert.Equal(expectedValid, value.GetProperty("expectedValid").GetBoolean());
        if (diagnosticId is null) Assert.Equal(JsonValueKind.Null, value.GetProperty("expectedDiagnosticId").ValueKind);
        else Assert.Equal(diagnosticId, value.GetProperty("expectedDiagnosticId").GetString());
    }

    private static void AssertIdentitySourcePresence(JsonElement fixture, string contractKey)
    {
        Assert.Contains(PascalFromKebab(contractKey) + "Service", File.ReadAllText(ResolveParityPath(fixture.GetProperty("sources").GetProperty("csharp").GetString()!), Encoding.UTF8), StringComparison.Ordinal);
        foreach (var language in new[] { "typescript", "javascript" })
            Assert.Contains("\"" + contractKey + "\"", File.ReadAllText(ResolveParityPath(fixture.GetProperty("sources").GetProperty(language).GetString()!), Encoding.UTF8), StringComparison.Ordinal);
    }

    private static string[] UnionTags(JsonElement values, JsonElement definitions) => values.EnumerateArray()
        .Select(value => value.GetProperty("$ref").GetString()!.Split('/')[^1])
        .Select(name => definitions.GetProperty(name).GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()!)
        .ToArray();

    private static string[] Tokenize(string source)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var current = source[i];
            if (current is '_' or '-')
            {
                Assert.True(i > start && i + 1 < source.Length);
                tokens.Add(source[start..i]);
                start = i + 1;
                continue;
            }
            Assert.True(IsAsciiLetter(current) || char.IsAsciiDigit(current));
            if (i == start || !char.IsAsciiLetterUpper(current)) continue;
            var previous = source[i - 1];
            var nextIsLower = i + 1 < source.Length && char.IsAsciiLetterLower(source[i + 1]);
            if (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) || char.IsAsciiLetterUpper(previous) && nextIsLower)
            {
                tokens.Add(source[start..i]);
                start = i;
            }
        }
        tokens.Add(source[start..]);
        return tokens.ToArray();
    }

    private static string PascalFromKebab(string value) => PascalFromSource(value);

    private static string PascalFromSource(string value) => string.Concat(Tokenize(value).Select(PascalToken));

    private static string PascalFromCamel(string value) => string.Concat(Tokenize(value).Select(PascalToken));

    private static string PascalToken(string value)
    {
        var lower = AsciiLower(value);
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static string AsciiLower(string value) => string.Create(value.Length, value, static (target, source) =>
    {
        for (var i = 0; i < source.Length; i++) target[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

    private static bool IsAsciiLetter(char value) => char.IsAsciiLetterLower(value) || char.IsAsciiLetterUpper(value);

    private static string NormalizeSource(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n') + "\n";

    private static string ReplaceFirst(string value, string find, string replacement)
    {
        var index = value.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return string.Concat(value.AsSpan(0, index), replacement, value.AsSpan(index + find.Length));
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string context) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText())), context);

    private static void AssertJsonEqual(JsonNode expected, JsonElement actual, string context) =>
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual.GetRawText())), context);

    private static void AssertJsonEqual(JsonNode expected, JsonNode actual, string context) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), context);

    private static void AssertExactProperties(JsonElement value, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static string FixtureId(JsonElement fixture) => fixture.GetProperty("id").GetString()!;

    private static string ContractKey(JsonElement contract) => contract.GetProperty("contractKey").GetString()!;

    private static string StringValue(JsonElement value) => value.GetString()!;

    private static JsonElement FindFixture(JsonElement fixtures, string id) => FindByStringProperty(fixtures, "id", id);

    private static JsonElement FindContract(JsonElement fixture, string key) => FindByStringProperty(fixture.GetProperty("contracts"), "contractKey", key);

    private static string LanguageFromPath(string path) => Path.GetExtension(path) switch
    {
        ".cs" => "csharp",
        ".ts" => "typescript",
        ".js" => "javascript",
        _ => throw new InvalidOperationException($"Unsupported parity source extension '{path}'."),
    };

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonDocument OpenExpectations() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ParityRoot, "expectations.json")));

    private static JsonDocument OpenMetadata(string relativePath) => JsonDocument.Parse(File.ReadAllBytes(ResolveMetadataPath(relativePath)));

    private static string ResolveParityPath(string relativePath) => Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), ParityRoot);

    private static string ResolveMetadataPath(string relativePath) => ResolveParityPath(relativePath);

    private static string FindParityRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "specs", "rpc", "v1", "authoring", "parity");
            if (File.Exists(Path.Combine(candidate, "expectations.json"))) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "parity");
    }
}
