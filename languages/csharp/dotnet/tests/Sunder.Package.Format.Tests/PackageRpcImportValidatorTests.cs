using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sunder.Core.TestSupport;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class PackageRpcImportValidatorTests
{
    private static readonly string DescriptorHash = new('a', 64);
    private static readonly string AbiHash = new('b', 64);
    private static readonly string ContentHash = new('c', 64);
    private static readonly string InventoryHash = new('d', 64);
    private static readonly SunderPackageJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    });

    [Fact]
    public void Models_HaveNoCompatibilityDefaults()
    {
        var manifest = new SunderPackageManifest();
        Assert.Null(manifest.PackageArtifactImports);
        Assert.Null(manifest.PackageApiImports);
        Assert.Null(manifest.ProviderImports);
        Assert.Null(manifest.PlatformImports);
        Assert.Null(new SunderPackageDependencyManifest().Presence);
        Assert.Null(new SunderPackageContractBundleManifest().Source);
        Assert.Null(new SunderPackageContractBundleManifest().AuthorityKind);
        Assert.Null(new SunderPackageContractBundleManifest().AuthorityId);
        Assert.Null(new SunderPackageArtifactImportManifest().IncludePrerelease);
        Assert.Null(new SunderPackageArtifactImportManifest().PublicContractCount);
        Assert.Null(new SunderPackagePlatformImportManifest().SelectedAuthorityContentIdentity);
        Assert.Null(new SunderPackageAuthorityConstraintManifest().SelectedAuthorityRelease);
    }

    [Theory]
    [InlineData(typeof(SunderPackageArtifactImportManifest), "packageArtifactImport")]
    [InlineData(typeof(SunderPackageApiImportManifest), "packageApiImport")]
    [InlineData(typeof(SunderPackageProviderImportManifest), "providerImport")]
    [InlineData(typeof(SunderPackagePlatformImportManifest), "platformImport")]
    [InlineData(typeof(SunderPackageAuthorityConstraintManifest), "authorityConstraint")]
    [InlineData(typeof(SunderPackageContractReferenceManifest), "contractRef")]
    [InlineData(typeof(SunderPackageContractUseReferenceManifest), "useRef")]
    [InlineData(typeof(SunderPackageProviderReferenceManifest), "providerRef")]
    public void Models_ExactlyMatchReceiptDefinitionMembers(Type model, string definition)
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RpcV1Authoring", "authoring-receipt.schema.json")))!;
        var expected = schema["$defs"]![definition]!["properties"]!.AsObject().Select(static pair => pair.Key).Order().ToArray();
        var actual = model.GetProperties().Select(static property => property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .Order().ToArray();
        Assert.True(model.IsSealed);
        Assert.Equal(typeof(object), model.BaseType);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("contractBundles/0", false)]
    [InlineData("dependsOn/0", false)]
    [InlineData("packageArtifactImports/0", false)]
    [InlineData("packageArtifactImports/0/authorityConstraints/0", false)]
    [InlineData("packageApiImports/0", false)]
    [InlineData("packageApiImports/0/bundleRefs/0", false)]
    [InlineData("packageApiImports/0/useRefs/0", false)]
    [InlineData("providerImports/0", true)]
    [InlineData("providerImports/0/providerRefs/0", true)]
    public void UnknownMembers_RemainStrict(string path, bool provider)
    {
        var json = provider ? ProviderManifest() : ApiManifest();
        At(json, path).AsObject()["unexpected"] = true;
        Assert.Throws<JsonException>(() => Read(json));
    }

    [Fact]
    public void PlatformUnknownMembersAndDuplicateJsonMembers_AreRejected()
    {
        var json = PlatformManifest();
        At(json, "platformImports/0").AsObject()["unexpected"] = true;
        Assert.Throws<JsonException>(() => Read(json));
        Assert.Throws<JsonException>(() => StrictJsonSerializer.Deserialize(
            "{\"packageApiImports\":[],\"packageApiImports\":[]}", JsonContext.SunderPackageManifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalRootSections_NullModelValuesDoNotInferImports(bool explicitNull)
    {
        var json = OwnedManifest();
        foreach (var section in new[] { "packageArtifactImports", "packageApiImports", "providerImports", "platformImports" })
        {
            if (explicitNull) json[section] = null;
            else json.Remove(section);
        }
        // Nullable DTO values are distinct from explicit null at the strict JSON boundary.
        Assert.Empty(Validate(JsonSerializer.Deserialize(json.ToJsonString(), JsonContext.SunderPackageManifest)!));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("authorityKind")]
    [InlineData("authorityId")]
    public void OwnedBundles_RequireExplicitProvenance(string member)
    {
        var json = OwnedManifest();
        At(json, "contractBundles/0").AsObject().Remove(member);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("authorityKind", "\"platform\"")]
    [InlineData("authorityId", "\"example.other\"")]
    [InlineData("selectedAuthorityRelease", "\"1.2.3\"")]
    [InlineData("selectedAuthorityContentIdentity", "\"forbidden\"")]
    [InlineData("catalogSigningKeyId", "\"core-1\"")]
    [InlineData("catalogTrustGeneration", "1")]
    [InlineData("catalogTrustFingerprint", "\"forbidden\"")]
    public void OwnedBundles_RejectForeignAuthorityAndSelectedEvidence(string member, string value)
    {
        var json = OwnedManifest();
        At(json, "contractBundles/0").AsObject()[member] = JsonNode.Parse(value);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("required", false)]
    [InlineData("optional", true)]
    public void PackagePresence_IsIndependentOfContractReadiness(string presence, bool required)
    {
        var json = ApiManifest(presence);
        Set(json, "usesContracts/0/required", JsonValue.Create(required));
        Assert.Empty(Validate(json));
        var errors = new List<string>();
        PackageRpcImportValidator.Validate(Read(json), errors);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("contractBundles/0/source")]
    [InlineData("contractBundles/0/authorityKind")]
    [InlineData("contractBundles/0/authorityId")]
    [InlineData("contractBundles/0/selectedAuthorityRelease")]
    [InlineData("contractBundles/0/selectedAuthorityContentIdentity")]
    [InlineData("packageArtifactImports/0/packageId")]
    [InlineData("packageArtifactImports/0/requestedVersionRange")]
    [InlineData("packageArtifactImports/0/includePrerelease")]
    [InlineData("packageArtifactImports/0/selectedPackageVersion")]
    [InlineData("packageArtifactImports/0/selectedPackageContentIdentity")]
    [InlineData("packageArtifactImports/0/publicContractCount")]
    [InlineData("packageArtifactImports/0/publicInventorySha256")]
    [InlineData("packageArtifactImports/0/authorityConstraints")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/authorityKind")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/authorityId")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/selectedAuthorityRelease")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/selectedAuthorityContentIdentity")]
    [InlineData("packageApiImports/0/packageId")]
    [InlineData("packageApiImports/0/presence")]
    [InlineData("packageApiImports/0/bundleRefs")]
    [InlineData("packageApiImports/0/useRefs")]
    [InlineData("packageApiImports/0/bundleRefs/0/contractId")]
    [InlineData("packageApiImports/0/bundleRefs/0/contractVersion")]
    [InlineData("packageApiImports/0/useRefs/0/role")]
    [InlineData("packageApiImports/0/useRefs/0/contractId")]
    [InlineData("packageApiImports/0/useRefs/0/contractVersion")]
    [InlineData("dependsOn/0/presence")]
    public void RequiredPackageMetadata_CannotBeOmittedOrNull(string path)
    {
        var json = ApiManifest();
        Set(json, path, null);
        Assert.NotEmpty(Validate(json));
        Remove(json, path);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("contractBundles/0/source", "\"legacy\"")]
    [InlineData("contractBundles/0/exposure", "\"package\"")]
    [InlineData("contractBundles/0/authorityKind", "\"registry\"")]
    [InlineData("contractBundles/0/authorityId", "\"Example.Invalid\"")]
    [InlineData("contractBundles/0/selectedAuthorityRelease", "\"v1.2.3\"")]
    [InlineData("contractBundles/0/selectedAuthorityContentIdentity", "\"invalid\"")]
    [InlineData("contractBundles/0/catalogSigningKeyId", "\"forbidden\"")]
    [InlineData("contractBundles/0/catalogTrustGeneration", "1")]
    [InlineData("contractBundles/0/catalogTrustFingerprint", "\"forbidden\"")]
    [InlineData("packageArtifactImports/0/publicContractCount", "0")]
    [InlineData("packageArtifactImports/0/publicContractCount", "129")]
    [InlineData("packageArtifactImports/0/publicContractCount", "2")]
    [InlineData("packageArtifactImports/0/publicInventorySha256", "\"invalid\"")]
    [InlineData("packageArtifactImports/0/authorityConstraints", "[]")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/catalogSigningKeyId", "\"forbidden\"")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/catalogTrustGeneration", "1")]
    [InlineData("packageArtifactImports/0/authorityConstraints/0/catalogTrustFingerprint", "\"forbidden\"")]
    [InlineData("packageApiImports/0/presence", "\"suggested\"")]
    [InlineData("packageApiImports/0/bundleRefs", "[]")]
    [InlineData("packageApiImports/0/useRefs", "[]")]
    [InlineData("packageApiImports/0/bundleRefs/0/contractVersion", "\"1.0.1\"")]
    [InlineData("packageApiImports/0/useRefs/0/contractVersion", "\"1.0.1\"")]
    [InlineData("packageApiImports/0/useRefs/0/role", "\"app\"")]
    [InlineData("dependsOn/0/versionRange", "\"1.2.3\"")]
    [InlineData("dependsOn/0/presence", "\"optional\"")]
    [InlineData("usesContracts/0/actions", "[\"invoke\",\"discover\"]")]
    [InlineData("usesContracts/0/abiSha256", "\"invalid\"")]
    public void PackageMetadata_RejectsMalformedOrInconsistentValues(string path, string value)
    {
        var json = ApiManifest();
        Set(json, path, JsonNode.Parse(value));
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("packageArtifactImports")]
    [InlineData("packageApiImports")]
    [InlineData("contractBundles")]
    [InlineData("usesContracts")]
    [InlineData("dependsOn")]
    public void RemovingGraphSections_CannotCreateFallbackEvidence(string section)
    {
        var json = ApiManifest();
        json.Remove(section);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("packageArtifactImports")]
    [InlineData("packageApiImports")]
    [InlineData("packageArtifactImports/0/authorityConstraints")]
    [InlineData("packageApiImports/0/bundleRefs")]
    [InlineData("packageApiImports/0/useRefs")]
    [InlineData("providerImports")]
    [InlineData("providerImports/0/providerRefs")]
    [InlineData("platformImports")]
    [InlineData("platformImports/0/providerRefs")]
    public void NewCollections_RejectNullEntriesAndDuplicateTuples(string path)
    {
        var json = path.StartsWith("platform", StringComparison.Ordinal) ? PlatformManifest()
            : path.StartsWith("provider", StringComparison.Ordinal) ? ProviderManifest() : ApiManifest();
        var array = At(json, path).AsArray();
        array.Add(array[0]!.DeepClone());
        Assert.Contains(Validate(json), static error => error.Contains("duplicate", StringComparison.Ordinal));
        array.RemoveAt(1);
        array.Add((JsonNode?)null);
        Assert.Contains(Validate(json), static error => error.Contains("null", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("packageArtifactImports", "packageId")]
    [InlineData("packageApiImports", "packageId")]
    [InlineData("packageArtifactImports/0/authorityConstraints", "authorityId")]
    [InlineData("packageApiImports/0/bundleRefs", "contractId")]
    [InlineData("packageApiImports/0/useRefs", "contractId")]
    [InlineData("providerImports", "key")]
    [InlineData("providerImports/0/providerRefs", "providerId")]
    [InlineData("platformImports", "key")]
    [InlineData("platformImports/0/providerRefs", "providerId")]
    public void NewCollections_RequireOrdinalOrder(string path, string member)
    {
        var json = path.StartsWith("platform", StringComparison.Ordinal) ? PlatformManifest()
            : path.StartsWith("provider", StringComparison.Ordinal) ? ProviderManifest() : ApiManifest();
        var array = At(json, path).AsArray();
        var earlier = array[0]!.DeepClone();
        earlier[member] = "a";
        array.Add(earlier);
        Assert.Contains(Validate(json), static error => error.Contains("ordinal tuple order", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(">=1.0.0 <2.0.0", "1.2.3+different", false, true)]
    [InlineData("1.2.3+reviewed", "1.2.3+selected", false, true)]
    [InlineData(">=1.0.0 <2.0.0", "1.3.0-preview.1", true, true)]
    [InlineData(">=1.0.0 <2.0.0", "1.3.0-preview.1", false, false)]
    [InlineData(">=1.0.0 <2.0.0", "2.0.0", false, false)]
    [InlineData(">=2.0.0 <1.0.0", "1.2.3", false, false)]
    [InlineData(">=1.0.0  <2.0.0", "1.2.3", false, false)]
    [InlineData("^1.0.0", "1.2.3", false, false)]
    public void SelectedArtifacts_EnforceCanonicalReviewedRangeAndPrereleasePolicy(
        string range, string selected, bool includePrerelease, bool valid)
    {
        var json = ApiManifest();
        Set(json, "packageArtifactImports/0/requestedVersionRange", JsonValue.Create(range));
        Set(json, "dependsOn/0/versionRange", JsonValue.Create(range));
        Set(json, "packageArtifactImports/0/includePrerelease", JsonValue.Create(includePrerelease));
        Set(json, "packageArtifactImports/0/selectedPackageVersion", JsonValue.Create(selected));
        Set(json, "packageArtifactImports/0/authorityConstraints/0/selectedAuthorityRelease", JsonValue.Create(selected));
        Set(json, "contractBundles/0/selectedAuthorityRelease", JsonValue.Create(selected));
        Assert.Equal(valid, Validate(json).Count == 0);
    }

    [Fact]
    public void CompatibleInstallation_RetainsRequestedRangeRatherThanSelectedVersionOrContentPins()
    {
        var json = ApiManifest();
        var manifest = Read(json);
        Assert.Empty(Validate(json));
        Assert.True(PackageVersionRange.IsSatisfiedBy("1.9.0+new.content", manifest.DependsOn![0].VersionRange!));
        Assert.NotEqual("1.9.0+new.content", manifest.PackageArtifactImports![0]!.SelectedPackageVersion);
        Assert.DoesNotContain(typeof(SunderPackageDependencyManifest).GetProperties(),
            static property => property.Name.Contains("Content", StringComparison.Ordinal)
                               || property.Name.Contains("Selected", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderOnlyImports_RequireExplicitRuntimeUsesAndExactAuthority()
    {
        var json = ProviderManifest();
        Assert.Empty(Validate(json));
        Assert.Empty(Read(json).UsesContracts!);
        Assert.Empty(Read(json).PackageApiImports!);
        json["usesContracts"] = ApiManifest()["usesContracts"]!.DeepClone();
        Set(json, "usesContracts/0/actions", new JsonArray("discover"));
        Assert.Empty(Validate(json));
        Assert.Equal(["discover"], Assert.Single(Read(json).UsesContracts!)!.Actions);
        Set(json, "usesContracts/0/role", JsonValue.Create("app"));
        Assert.Contains(Validate(json), static error => error.Contains("do not grant consumer uses", StringComparison.Ordinal));
        Set(json, "usesContracts/0/role", JsonValue.Create("runtime"));
        Set(json, "contractBundles/0/selectedAuthorityContentIdentity", JsonValue.Create(new string('e', 64)));
        Assert.Contains(Validate(json), static error => error.Contains("authority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProviderOnlyImport_DoesNotPretendToAuthenticateCompleteInventory()
    {
        var json = ProviderManifest();
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Assert.Empty(Validate(json));
    }

    [Fact]
    public void OwnerAndReexportProviderImports_AcceptCompleteSharedProviderReferences()
    {
        var manifest = Read(ReexportProviderManifest());
        Assert.Single(manifest.ContractBundles!);
        Assert.Empty(manifest.UsesContracts!);
        Assert.Empty(manifest.PackageApiImports!);
        Assert.Equal(new[] { "public", "owner" }, manifest.Provides!.Select(static provider => provider!.Audience));
        Assert.Equal(new[] { "example.dependency", "example.reexport" },
            manifest.ProviderImports!.Select(static import => import!.PackageId));
        Assert.All(manifest.ProviderImports!, static import => Assert.Equal(
            new[] { "example.consumer.alpha", "example.consumer.zulu" },
            import!.ProviderRefs!.Select(static reference => reference!.ProviderId)));
        Assert.Empty(Validate(manifest));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SharedProviderReferences_StillRejectDuplicatesWithinEachGroup(int groupIndex)
    {
        var json = ReexportProviderManifest();
        Assert.Empty(Validate(json));

        var references = At(json, $"providerImports/{groupIndex}/providerRefs").AsArray();
        references.Insert(1, references[0]!.DeepClone());
        Assert.Equal("providerRefs contains a duplicate identity/reference tuple.", Assert.Single(Validate(json)));
    }

    [Fact]
    public void SharedProviderReferences_StillRejectDifferentLocallyResolvableContracts()
    {
        var json = ReexportProviderManifest();
        var bundle = At(OwnedManifest(), "contractBundles/0").DeepClone();
        bundle["contractId"] = "example.rpc.other";
        bundle["descriptorPath"] = "contracts/other.json";
        json["contractBundles"]!.AsArray().Add(bundle);
        var provider = At(ProviderManifest(), "provides/0").DeepClone();
        provider["providerId"] = "example.consumer.other";
        provider["contractId"] = "example.rpc.other";
        json["provides"]!.AsArray().Insert(1, provider);
        Assert.Empty(Validate(json));

        At(json, "providerImports/1/providerRefs").AsArray()[0] =
            Reference("example.rpc.other", "runtime", "example.consumer.other");
        Assert.Equal("Import providerRefs require exact matching import contract identity and local providers.",
            Assert.Single(Validate(json)));
    }

    [Fact]
    public void ProviderSelections_CannotExceedOneSelectedArtifactsInventoryCount()
    {
        var json = ProviderManifest();
        var bundle = At(json, "contractBundles/0").DeepClone();
        bundle["contractId"] = "example.rpc.second";
        bundle["descriptorPath"] = "contracts/second.json";
        json["contractBundles"]!.AsArray().Add(bundle);
        var provider = At(json, "provides/0").DeepClone();
        provider["contractId"] = "example.rpc.second";
        provider["providerId"] = "example.consumer.second";
        json["provides"]!.AsArray().Add(provider);
        json["providerImports"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "second", ["packageId"] = "example.dependency", ["contractId"] = "example.rpc.second", ["contractVersion"] = "1.0.0",
            ["bundleRef"] = Reference("example.rpc.second"),
            ["providerRefs"] = new JsonArray(Reference("example.rpc.second", "runtime", "example.consumer.second")),
        });
        Assert.Contains(Validate(json), static error => error.Contains("publicContractCount permits", StringComparison.Ordinal));
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Assert.Empty(Validate(json));
    }

    [Fact]
    public void ProviderAndApiSelections_CannotSelectDifferentVersionsFromTheSameArtifact()
    {
        var json = ApiManifest();
        var provider = ProviderManifest();
        var bundle = At(json, "contractBundles/0").DeepClone();
        bundle["version"] = "1.0.1";
        bundle["descriptorPath"] = "contracts/second.json";
        json["contractBundles"]!.AsArray().Add(bundle);
        Set(provider, "provides/0/contractVersion", JsonValue.Create("1.0.1"));
        Set(provider, "providerImports/0/contractVersion", JsonValue.Create("1.0.1"));
        Set(provider, "providerImports/0/bundleRef/contractVersion", JsonValue.Create("1.0.1"));
        Set(provider, "providerImports/0/providerRefs/0/contractVersion", JsonValue.Create("1.0.1"));
        json["provides"] = provider["provides"]!.DeepClone();
        json["providerImports"] = provider["providerImports"]!.DeepClone();
        Assert.Contains(Validate(json), static error => error.Contains("one version of each contract id across all import groups", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderOnlySelections_CannotMaterializeDifferentVersionsOfOneImportedIdGlobally()
    {
        var json = ProviderManifest();
        var second = ProviderManifest();
        foreach (var path in new[] { "dependsOn/0/packageId", "packageArtifactImports/0/packageId",
                     "packageArtifactImports/0/authorityConstraints/0/authorityId", "contractBundles/0/authorityId", "providerImports/0/packageId" })
        {
            Set(second, path, JsonValue.Create("example.second"));
        }
        foreach (var path in new[] { "contractBundles/0/contractId", "provides/0/contractId", "providerImports/0/contractId",
                     "providerImports/0/bundleRef/contractId", "providerImports/0/providerRefs/0/contractId" })
        {
            Set(second, path, JsonValue.Create("example.rpc.second"));
        }
        foreach (var path in new[] { "contractBundles/0/version", "provides/0/contractVersion", "providerImports/0/contractVersion",
                     "providerImports/0/bundleRef/contractVersion", "providerImports/0/providerRefs/0/contractVersion" })
        {
            Set(second, path, JsonValue.Create("2.0.0"));
        }
        foreach (var path in new[] { "contractBundles/0/selectedAuthorityContentIdentity", "packageArtifactImports/0/selectedPackageContentIdentity",
                     "packageArtifactImports/0/authorityConstraints/0/selectedAuthorityContentIdentity" })
        {
            Set(second, path, JsonValue.Create(new string('f', 64)));
        }
        Set(second, "contractBundles/0/descriptorPath", JsonValue.Create("contracts/second.json"));
        Set(second, "provides/0/providerId", JsonValue.Create("example.consumer.second"));
        Set(second, "providerImports/0/providerRefs/0/providerId", JsonValue.Create("example.consumer.second"));
        Set(second, "providerImports/0/key", JsonValue.Create("second"));
        foreach (var section in new[] { "contractBundles", "provides", "dependsOn", "packageArtifactImports", "providerImports" })
        {
            json[section]!.AsArray().Add(second[section]![0]!.DeepClone());
        }
        Assert.Empty(Validate(json));

        foreach (var path in new[] { "contractBundles/1/contractId", "provides/1/contractId", "providerImports/1/contractId",
                     "providerImports/1/bundleRef/contractId", "providerImports/1/providerRefs/0/contractId" })
        {
            Set(json, path, JsonValue.Create("example.rpc.api"));
        }
        Assert.Equal("Imported contract id 'example.rpc.api' must materialize only one exact version across all imports.",
            Assert.Single(Validate(json)));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("package")]
    public void OwnedOnlyBundles_MayRetainMultipleHistoricalVersions(string exposure)
    {
        var json = OwnedManifest();
        json["version"] = "7.6.5-beta.1+build.7";
        Set(json, "contractBundles/0/exposure", JsonValue.Create(exposure));
        var second = At(json, "contractBundles/0").DeepClone();
        second["version"] = "2.0.0";
        second["descriptorPath"] = "contracts/second.json";
        json["contractBundles"]!.AsArray().Add(second);
        // New private source-authoring equality must not invalidate old archive metadata.
        Assert.Empty(Validate(json));
    }

    [Fact]
    public void ConsumerIntent_DoesNotAuthorizeAnImportedProvider()
    {
        var json = ApiManifest();
        json["provides"] = ProviderManifest()["provides"]!.DeepClone();
        Assert.Contains(Validate(json), static error => error.Contains("provider 'example.consumer.provider' is orphaned", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderAndConsumerIntent_CoalesceRequiredWinsWithoutRewritingApiPresence()
    {
        var json = ApiManifest("optional");
        var provider = ProviderManifest();
        json["provides"] = provider["provides"]!.DeepClone();
        json["providerImports"] = provider["providerImports"]!.DeepClone();
        Assert.Contains(Validate(json), static error => error.Contains("coalesced presence 'required'", StringComparison.Ordinal));
        Set(json, "dependsOn/0/presence", JsonValue.Create("required"));
        Assert.Empty(Validate(json));
        Assert.Equal("optional", Read(json).PackageApiImports![0]!.Presence);
    }

    [Theory]
    [InlineData("providerImports/0/key")]
    [InlineData("providerImports/0/packageId")]
    [InlineData("providerImports/0/contractId")]
    [InlineData("providerImports/0/contractVersion")]
    [InlineData("providerImports/0/bundleRef")]
    [InlineData("providerImports/0/providerRefs")]
    [InlineData("providerImports/0/providerRefs/0/role")]
    [InlineData("providerImports/0/providerRefs/0/providerId")]
    [InlineData("providerImports/0/providerRefs/0/contractId")]
    [InlineData("providerImports/0/providerRefs/0/contractVersion")]
    public void ProviderMetadata_IsMandatory(string path)
    {
        var json = ProviderManifest();
        Remove(json, path);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("providerImports/0/key", "\"Uppercase\"")]
    [InlineData("providerImports/0/contractVersion", "\"1.0.1\"")]
    [InlineData("providerImports/0/bundleRef/contractVersion", "\"1.0.1\"")]
    [InlineData("providerImports/0/providerRefs", "[]")]
    [InlineData("providerImports/0/providerRefs/0/providerId", "\"example.missing\"")]
    [InlineData("providerImports/0/providerRefs/0/contractVersion", "\"1.0.1\"")]
    [InlineData("providerImports/0/providerRefs/0/role", "\"app\"")]
    [InlineData("provides/0/descriptorSha256", "\"invalid\"")]
    [InlineData("provides/0/abiSha256", "\"invalid\"")]
    public void ProviderReferences_RequireExactIdentityAndRuntimeRole(string path, string value)
    {
        var json = ProviderManifest();
        Set(json, path, JsonNode.Parse(value));
        Assert.NotEmpty(Validate(json));
    }

    [Fact]
    public void ImportedProvider_RequiresReverseCoverage()
    {
        var json = ProviderManifest();
        json["providerImports"] = new JsonArray();
        Assert.Contains(Validate(json), static error => error.Contains("provider 'example.consumer.provider' is orphaned", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateProviderIntentTuple_IsRejectedEvenWithDifferentKeys()
    {
        var json = ProviderManifest();
        var duplicate = At(json, "providerImports/0").DeepClone();
        duplicate["key"] = "second";
        json["providerImports"]!.AsArray().Add(duplicate);
        Assert.Contains(Validate(json), static error => error.Contains("unique (packageId, contractId)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportAliases_AreCaseInsensitiveWithinEachKindWithoutChangingOrdinalOrder(bool platform)
    {
        var json = platform ? PlatformManifest() : ProviderManifest();
        var section = platform ? "platformImports" : "providerImports";
        var contractId = platform ? "sunder.stack.second" : "example.rpc.second";
        Set(json, $"{section}/0/key", JsonValue.Create("apiZ"));
        var bundle = At(json, "contractBundles/0").DeepClone();
        bundle["contractId"] = contractId;
        bundle["descriptorPath"] = "contracts/second.json";
        json["contractBundles"]!.AsArray().Add(bundle);
        var provider = At(json, "provides/0").DeepClone();
        provider["contractId"] = contractId;
        provider["providerId"] = "example.consumer.second";
        json["provides"]!.AsArray().Add(provider);
        var import = At(json, $"{section}/0").DeepClone();
        import["key"] = "apia";
        import["contractId"] = contractId;
        import["bundleRef"]!["contractId"] = contractId;
        import["providerRefs"]![0]!["contractId"] = contractId;
        import["providerRefs"]![0]!["providerId"] = "example.consumer.second";
        json[section]!.AsArray().Add(import);
        if (!platform) Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Assert.Empty(Validate(json)); // apiZ precedes apia only in ordinal, not case-insensitive, order.

        Set(json, $"{section}/1/key", JsonValue.Create("apiz"));
        Assert.Equal("Provider and platform import key 'apiz' is duplicated under OrdinalIgnoreCase.",
            Assert.Single(Validate(json)));
    }

    [Theory]
    [InlineData("apiProvider")]
    [InlineData("apiprovider")]
    public void ProviderAndPlatformImports_ShareOneCaseInsensitiveAliasNamespace(string alias)
    {
        var json = ProviderManifest();
        var platform = PlatformManifest();
        Set(platform, "contractBundles/0/descriptorPath", JsonValue.Create("contracts/platform.json"));
        Set(platform, "provides/0/providerId", JsonValue.Create("example.consumer.stack"));
        Set(platform, "platformImports/0/providerRefs/0/providerId", JsonValue.Create("example.consumer.stack"));
        json["contractBundles"]!.AsArray().Add(platform["contractBundles"]![0]!.DeepClone());
        json["provides"]!.AsArray().Add(platform["provides"]![0]!.DeepClone());
        json["platformImports"] = platform["platformImports"]!.DeepClone();
        Assert.Empty(Validate(json));

        Set(json, "platformImports/0/key", JsonValue.Create(alias));
        Assert.Equal($"Provider and platform import key '{alias}' is duplicated under OrdinalIgnoreCase.",
            Assert.Single(Validate(json)));
    }

    [Fact]
    public void PlatformImports_AreProviderOnlyAndDoNotCreatePackageDependencies()
    {
        var json = PlatformManifest();
        Assert.Empty(Validate(json));
        Assert.Empty(Read(json).DependsOn!);
        json["usesContracts"] = ApiManifest()["usesContracts"]!.DeepClone();
        Set(json, "usesContracts/0/contractId", JsonValue.Create("sunder.stack.contributor"));
        Assert.Contains(Validate(json), static error => error.Contains("cannot have consumer uses", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("authorityKind")]
    [InlineData("authorityId")]
    [InlineData("authorityReleaseRange")]
    [InlineData("includePrerelease")]
    [InlineData("selectedAuthorityRelease")]
    [InlineData("selectedAuthorityContentIdentity")]
    [InlineData("contractId")]
    [InlineData("contractVersion")]
    [InlineData("bundleRef")]
    [InlineData("providerRefs")]
    public void PlatformMetadata_IsMandatory(string member)
    {
        var json = PlatformManifest();
        At(json, "platformImports/0").AsObject().Remove(member);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("authorityId", "\"example.platform\"")]
    [InlineData("authorityKind", "\"package\"")]
    [InlineData("authorityReleaseRange", "\">=2.0.0 <3.0.0\"")]
    [InlineData("authorityReleaseRange", "\">=1.0.0  <2.0.0\"")]
    [InlineData("includePrerelease", "true")]
    [InlineData("selectedAuthorityRelease", "\"1.3.0-preview.1\"")]
    public void PlatformMetadata_EnforcesRangeAndRegistryAuthorityEvidence(string member, string value)
    {
        var json = PlatformManifest();
        At(json, "platformImports/0").AsObject()[member] = JsonNode.Parse(value);
        Assert.NotEmpty(Validate(json));
    }

    [Theory]
    [InlineData("selectedAuthorityRelease")]
    [InlineData("selectedAuthorityContentIdentity")]
    public void PlatformBundles_RequireExactAuthorityEvenInMetadataOnlyProjection(string member)
    {
        var json = PlatformManifest();
        At(json, "contractBundles/0").AsObject().Remove(member);
        Assert.NotEmpty(Validate(json));
    }

    [Fact]
    public void PackageApi_MayRetainPlatformInventoryEvidenceWithoutConsumerProjection()
    {
        var json = ApiManifest();
        var platform = At(PlatformManifest(), "platformImports/0").AsObject();
        var constraint = new JsonObject();
        foreach (var name in new[] { "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
        {
            constraint[name] = platform[name]!.DeepClone();
        }
        At(json, "packageArtifactImports/0/authorityConstraints").AsArray().Add(constraint);
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Assert.Empty(Validate(json));
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(1));
        Assert.NotEmpty(Validate(json));
    }

    [Fact]
    public void DistinctUltimateAuthority_RequiresExplicitReviewedArtifactNotSnapshotDerivedPin()
    {
        var json = ReexportManifest("optional");
        Assert.Empty(Validate(json));
        json["packageArtifactImports"]!.AsArray().RemoveAt(0);
        Set(json, "dependsOn/0/versionRange", JsonValue.Create("1.2.3"));
        Assert.Contains(Validate(json), static error => error.Contains("unreviewed authority range", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitAuthoritySelection_ContributesDependencyWithoutImplicitConsumerGrant()
    {
        var json = ReexportManifest("optional");
        Assert.Empty(Validate(json));
        Assert.Single(Read(json).UsesContracts!);
        Assert.Single(Read(json).PackageApiImports!);
        Assert.Equal(2, Read(json).DependsOn!.Count);
        Assert.All(Read(json).DependsOn!, static dependency => Assert.Equal("optional", dependency.Presence));
    }

    [Fact]
    public void ReexportedContracts_MustFitExplicitUltimateOwnerInventoryCount()
    {
        var json = ReexportManifest("optional");
        AddSecondReexportContract(json);
        Assert.Empty(Validate(json));

        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(1));
        Assert.Equal("Package artifact import 'example.dependency' references more contracts and authorities than publicContractCount permits.",
            Assert.Single(Validate(json)));
    }

    [Fact]
    public void ReexportedContracts_RequireCoverageInExplicitUltimateOwnerApi()
    {
        var json = ReexportManifest("optional");
        AddSecondReexportContract(json);
        var ownerApi = At(json, "packageApiImports/0").DeepClone();
        ownerApi["packageId"] = "example.dependency";
        json["packageApiImports"]!.AsArray().Insert(0, ownerApi);
        At(json, "packageArtifactImports/0/authorityConstraints").AsArray().Add(new JsonObject
        {
            ["authorityKind"] = "platform", ["authorityId"] = "sunder.core",
            ["selectedAuthorityRelease"] = "1.2.3", ["selectedAuthorityContentIdentity"] = ContentHash,
        });
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(3));
        Assert.Empty(Validate(json));

        ownerApi["bundleRefs"]!.AsArray().RemoveAt(1);
        ownerApi["useRefs"]!.AsArray().RemoveAt(1);
        Assert.Equal("Package API import 'example.dependency' bundleRefs omit an exact contract required by its import graph.",
            Assert.Single(Validate(json)));
    }

    [Fact]
    public void ExplicitAuthoritySelection_CannotOmitItsOwnAuthorityConstraint()
    {
        var json = ReexportManifest("required");
        var platform = At(PlatformManifest(), "contractBundles/0");
        var constraint = new JsonObject();
        foreach (var member in new[] { "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
        {
            constraint[member] = platform[member]!.DeepClone();
        }
        Set(json, "packageArtifactImports/0/authorityConstraints", new JsonArray(constraint));
        Assert.Contains(Validate(json), static error => error.Contains("must retain its own exact authority constraint", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerAndReexport_CoalesceUltimateSnapshotNotArtifactLocalSourceOrContent()
    {
        var json = ReexportManifest("optional");
        json["packageApiImports"]!.AsArray().Insert(0, ApiManifest("optional")["packageApiImports"]![0]!.DeepClone());
        Assert.Empty(Validate(json));
        Assert.Single(Read(json).ContractBundles!);
        Assert.Single(Read(json).UsesContracts!);
        Assert.Equal("imported", Read(json).ContractBundles![0]!.Source);
        Assert.NotEqual(Read(json).PackageArtifactImports![0]!.SelectedPackageContentIdentity,
            Read(json).PackageArtifactImports![1]!.SelectedPackageContentIdentity);
    }

    [Theory]
    [InlineData("app", "runtime")]
    [InlineData("runtime", "app")]
    [InlineData("app", "app,runtime")]
    [InlineData("app,runtime", "runtime")]
    [InlineData("app,runtime", "app,runtime")]
    public void OwnerAndReexport_GrantTheirOwnCompleteRoleSubset(string ownerRoles, string reexportRoles)
    {
        var json = ReexportRoleManifest(ownerRoles, reexportRoles);
        Assert.Empty(Validate(json));
        var manifest = Read(json);
        Assert.Equal(2, manifest.ContractBundles!.Count);
        Assert.Equal(4, manifest.UsesContracts!.Count);
        Assert.Equal(ownerRoles.Split(','), manifest.PackageApiImports![0]!.UseRefs!
            .Select(static reference => reference!.Role).Distinct());
        Assert.Equal(reexportRoles.Split(','), manifest.PackageApiImports![1]!.UseRefs!
            .Select(static reference => reference!.Role).Distinct());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public void OverlappingRoleGroups_CannotBorrowMissingBundleCoverageFromAnotherGroup(int group, int reference)
    {
        var json = ReexportRoleManifest("app,runtime", "app,runtime");
        At(json, $"packageApiImports/{group}/useRefs").AsArray().RemoveAt(reference);
        Assert.Equal($"Package API import '{(group == 0 ? "example.dependency" : "example.reexport")}' useRefs must cover every bundle for each consumer role.",
            Assert.Single(Validate(json)));
    }

    [Theory]
    [InlineData("app", "runtime")]
    [InlineData("runtime", "app")]
    public void RoleScopedGroups_RejectEveryOrphanUseEvenWhenTheBundleIsCovered(string grantedRole, string orphanRole)
    {
        var json = ReexportRoleManifest(grantedRole, grantedRole);
        var use = At(json, "usesContracts/0").DeepClone();
        use["role"] = orphanRole;
        json["usesContracts"]!.AsArray().Add(use);
        Assert.Equal("Imported contract use 'example.rpc.api' is orphaned: provider imports do not grant consumer uses.",
            Assert.Single(Validate(json)));
    }

    [Theory]
    [InlineData("packageApiImports/0/useRefs/0/contractVersion", "1.0.1", "exact matching bundleRefs and local uses")]
    [InlineData("usesContracts/0/abiSha256", "hash", "ABI")]
    [InlineData("packageArtifactImports/1/authorityConstraints/0/selectedAuthorityContentIdentity", "hash", "conflicting selected authority snapshots")]
    public void DifferingRoleGroups_PreserveExactContractAndAuthorityChecks(string path, string value, string diagnostic)
    {
        var json = ReexportRoleManifest("app", "runtime");
        Assert.Empty(Validate(json));
        Set(json, path, JsonValue.Create(value == "hash" ? new string('f', 64) : value));
        Assert.Contains(Validate(json), error => error.Contains(diagnostic, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AppConsumerGrant_DoesNotAuthorizeAnAppProviderEvenWithAnAppTarget()
    {
        var json = ReexportRoleManifest("app", "runtime");
        var provider = ProviderManifest();
        json["provides"] = provider["provides"]!.DeepClone();
        json["providerImports"] = provider["providerImports"]!.DeepClone();
        // A provider makes the dependency required independently of the optional APIs.
        Set(json, "dependsOn/0/presence", JsonValue.Create("required"));
        Assert.Empty(Validate(json));
        Set(json, "provides/0/role", JsonValue.Create("app"));
        Set(json, "providerImports/0/providerRefs/0/role", JsonValue.Create("app"));
        Assert.Contains(Validate(json), static error => error.Contains("providerRefs require runtime role", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("packageArtifactImports/0/selectedPackageVersion", "1.2.4")]
    [InlineData("packageArtifactImports/0/selectedPackageContentIdentity", "hash")]
    [InlineData("packageArtifactImports/1/authorityConstraints/0/selectedAuthorityRelease", "1.2.4")]
    [InlineData("contractBundles/0/selectedAuthorityContentIdentity", "hash")]
    public void WithinOneManifest_AuthoritySnapshotsRemainExact(string path, string value)
    {
        var json = ReexportManifest("required");
        Set(json, path, JsonValue.Create(value == "hash" ? new string('f', 64) : value));
        Assert.Contains(Validate(json), static error => error.Contains("conflicting selected authority snapshots", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredWins_PropagatesThroughExplicitAuthoritySelection()
    {
        var json = ReexportManifest("optional");
        var provider = ProviderManifest();
        Set(provider, "providerImports/0/packageId", JsonValue.Create("example.reexport"));
        json["providerImports"] = provider["providerImports"]!.DeepClone();
        json["provides"] = provider["provides"]!.DeepClone();
        foreach (var dependency in json["dependsOn"]!.AsArray()) dependency!["presence"] = "required";
        Assert.Empty(Validate(json));
        Assert.Equal("optional", Read(json).PackageApiImports![0]!.Presence);
    }

    [Fact]
    public void AuthorityCycles_AreBoundedAndDoNotManufactureConsumerUses()
    {
        var json = ReexportManifest("required");
        var provider = ProviderManifest();
        json["packageApiImports"] = new JsonArray();
        json["usesContracts"] = new JsonArray();
        json["providerImports"] = provider["providerImports"]!.DeepClone();
        json["provides"] = provider["provides"]!.DeepClone();
        var reexportAuthority = new JsonObject
        {
            ["authorityKind"] = "package", ["authorityId"] = "example.reexport",
            ["selectedAuthorityRelease"] = "3.2.1", ["selectedAuthorityContentIdentity"] = InventoryHash,
        };
        At(json, "packageArtifactImports/0/authorityConstraints").AsArray().Add(reexportAuthority.DeepClone());
        At(json, "packageArtifactImports/1/authorityConstraints").AsArray().Add(reexportAuthority);
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Set(json, "packageArtifactImports/1/publicContractCount", JsonValue.Create(2));
        Assert.Empty(Validate(json));
    }

    [Fact]
    public void PackageApiUseReferences_CoverAllBundlesForEveryDeclaredRole()
    {
        var json = ApiManifest();
        var secondBundle = At(json, "contractBundles/0").DeepClone();
        secondBundle["contractId"] = "example.rpc.second";
        secondBundle["descriptorPath"] = "contracts/second.json";
        json["contractBundles"]!.AsArray().Add(secondBundle);
        At(json, "packageApiImports/0/bundleRefs").AsArray().Add(Reference("example.rpc.second"));
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Assert.Contains(Validate(json), static error => error.Contains("every bundle for each consumer role", StringComparison.Ordinal));
        var secondUse = At(json, "usesContracts/0").DeepClone();
        secondUse["contractId"] = "example.rpc.second";
        json["usesContracts"]!.AsArray().Add(secondUse);
        At(json, "packageApiImports/0/useRefs").AsArray().Add(Reference("example.rpc.second", role: "runtime"));
        Assert.Empty(Validate(json));
    }

    [Theory]
    [InlineData("unary", "discover,invoke", true, true)]
    [InlineData("unary", "discover", true, false)]
    [InlineData("unary", "discover,invoke,subscribe", true, false)]
    [InlineData("server-stream", "discover,subscribe", true, true)]
    [InlineData("server-stream", "discover,invoke", true, false)]
    [InlineData("both", "discover,invoke,subscribe", true, true)]
    [InlineData("both", "discover,invoke", true, false)]
    [InlineData("unary", "invoke", false, true)]
    [InlineData("unary", "discover", false, true)]
    [InlineData("unary", "subscribe", false, false)]
    [InlineData("server-stream", "subscribe", false, true)]
    public void DescriptorPayload_ChecksApplicableActionsWithoutWideningOwnedUses(
        string kind, string actions, bool imported, bool valid)
    {
        var json = imported ? ApiManifest() : OwnedManifest();
        var descriptorJson = JsonNode.Parse("""
            {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"example.rpc.api","version":"1.0.0",
             "errors":{},"services":{"example":{"methods":{}}},
             "$defs":{"Empty":{"type":"object","properties":{},"required":[],"additionalProperties":false}}}
            """)!;
        var methods = descriptorJson["services"]!["example"]!["methods"]!.AsObject();
        foreach (var methodKind in kind == "both" ? new[] { "unary", "server-stream" } : new[] { kind })
        {
            methods[methodKind == "unary" ? "invoke" : "watch"] = JsonNode.Parse($$"""
                {"kind":"{{methodKind}}","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Empty"},
                 "{{(methodKind == "unary" ? "responseSchema" : "eventSchema")}}":{"$ref":"#/$defs/Empty"} }
                """);
        }
        var bytes = Encoding.UTF8.GetBytes(descriptorJson.ToJsonString());
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        Set(json, "contractBundles/0/descriptorSha256", JsonValue.Create(descriptor.Sha256));
        Set(json, "contractBundles/0/abiSha256", JsonValue.Create(descriptor.AbiSha256));
        Set(json, "usesContracts/0/abiSha256", JsonValue.Create(descriptor.AbiSha256));
        Set(json, "usesContracts/0/actions", new JsonArray(actions.Split(',').Select(static action => (JsonNode?)JsonValue.Create(action)).ToArray()));
        Assert.Empty(Validate(json)); // Projection metadata cannot infer method kinds from hashes.
        using var directory = TemporaryDirectoryScope.Create("sunder-rpc-import-actions");
        var path = directory.WriteAllText("descriptor.json", descriptorJson.ToJsonString());
        var errors = new List<string>();
        PackageContractValidator.Validate(Read(json), new Dictionary<string, string>
        {
            [SunderPackageFormat.SharedPayloadRoot + "contracts/api.json"] = path,
            [SunderPackageFormat.RpcModelProjectionPath] = directory.WriteAllText("projection.json",
                Encoding.UTF8.GetString(ModelProjectionFixture.EmptyDocument(Read(json)))),
        }, errors);
        Assert.Equal(valid, errors.Count == 0);
        if (!valid) Assert.Contains(errors, static error => error.Contains("actions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4194305)]
    public void DescriptorPayload_RejectsInvalidSizeWithoutUnboundedReads(int length)
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-rpc-import-size");
        var path = directory.GetPath("descriptor.json");
        using (var stream = File.Create(path)) stream.SetLength(length);
        var errors = new List<string>();
        PackageContractValidator.Validate(Read(ApiManifest()), new Dictionary<string, string>
        {
            [SunderPackageFormat.SharedPayloadRoot + "contracts/api.json"] = path,
        }, errors);
        Assert.Contains(errors, static error => error.Contains("bytes or smaller", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDescriptorFile_IsAnErrorNotAnException()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-rpc-import-missing");
        var errors = new List<string>();
        PackageContractValidator.Validate(Read(ApiManifest()), new Dictionary<string, string>
        {
            [SunderPackageFormat.SharedPayloadRoot + "contracts/api.json"] = directory.GetPath("missing.json"),
        }, errors);
        Assert.Contains(errors, static error => error.Contains("descriptor 'contracts/api.json' is invalid", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void AggregateBundleLimit_IsEnforced(int count, bool valid)
    {
        var json = OwnedManifest();
        json["usesContracts"] = new JsonArray();
        var template = At(json, "contractBundles/0");
        var bundles = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            var bundle = template.DeepClone();
            bundle["contractId"] = $"example.rpc.c{index:D3}";
            bundle["descriptorPath"] = $"contracts/c{index:D3}.json";
            bundles.Add(bundle);
        }
        json["contractBundles"] = bundles;
        Assert.Equal(valid, Validate(json).Count == 0);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void BothConsumerRoles_CountSeparatelyAgainstUseLimit(int contractCount, bool valid)
    {
        var json = ApiManifest();
        AddAppTarget(json);
        var bundleTemplate = At(json, "contractBundles/0").DeepClone();
        var useTemplate = At(json, "usesContracts/0").DeepClone();
        var bundles = new JsonArray();
        var bundleRefs = new JsonArray();
        var uses = new JsonArray();
        var useRefs = new JsonArray();
        for (var index = 0; index < contractCount; index++)
        {
            var id = $"example.rpc.c{index:D3}";
            var bundle = bundleTemplate.DeepClone();
            bundle["contractId"] = id;
            bundle["descriptorPath"] = $"contracts/c{index:D3}.json";
            bundles.Add(bundle);
            bundleRefs.Add(Reference(id));
        }
        foreach (var role in new[] { "app", "runtime" })
            foreach (var reference in bundleRefs)
            {
                var id = reference!["contractId"]!.GetValue<string>();
                var use = useTemplate.DeepClone();
                use["role"] = role;
                use["contractId"] = id;
                uses.Add(use);
                useRefs.Add(Reference(id, role));
            }
        json["contractBundles"] = bundles;
        json["usesContracts"] = uses;
        Set(json, "packageApiImports/0/bundleRefs", bundleRefs);
        Set(json, "packageApiImports/0/useRefs", useRefs);
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(contractCount));
        var errors = Validate(json);
        Assert.Equal(valid, errors.Count == 0);
        if (!valid) Assert.Contains(errors, static error => error.Contains("limit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Required")]
    [InlineData("suggested")]
    public void DependencyPresence_IsMandatoryEvenWithoutImports(string? presence)
    {
        var errors = new List<string>();
        PackageDependencyValidator.Validate([new SunderPackageDependencyManifest
        {
            PackageId = "example.dependency", VersionRange = ">=1.0.0 <2.0.0", Presence = presence,
        }], errors);
        Assert.Contains(errors, static error => error.Contains("must declare presence", StringComparison.Ordinal));
    }

    [Fact]
    public void Dependencies_RejectNoncanonicalRangesAndDuplicateIds()
    {
        var dependency = new SunderPackageDependencyManifest
        {
            PackageId = "example.dependency", VersionRange = ">=1.0.0  <2.0.0", Presence = "required",
        };
        var errors = new List<string>();
        PackageDependencyValidator.Validate([dependency, dependency], errors);
        Assert.Contains(errors, static error => error.Contains("noncanonical", StringComparison.Ordinal));
        Assert.Contains(errors, static error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedCollections_AreBoundedAndNullSafe()
    {
        var manifest = new SunderPackageManifest
        {
            Id = "example.consumer",
            ContractBundles = new PrefixOnlyList<SunderPackageContractBundleManifest>(128),
            UsesContracts = new PrefixOnlyList<SunderPackageContractUseManifest>(128),
            Provides = new PrefixOnlyList<SunderPackageProviderManifest>(128),
            Targets = new PrefixOnlyList<SunderPackageTargetManifest>(12),
            DependsOn = new PrefixOnlyList<SunderPackageDependencyManifest>(128),
            PackageArtifactImports = new PrefixOnlyList<SunderPackageArtifactImportManifest>(128),
            PackageApiImports = new PrefixOnlyList<SunderPackageApiImportManifest>(128),
            ProviderImports = new PrefixOnlyList<SunderPackageProviderImportManifest>(128),
            PlatformImports = new PrefixOnlyList<SunderPackagePlatformImportManifest>(128),
        };
        var errors = new List<string>();
        PackageContractValidator.Validate(manifest, new Dictionary<string, string>(), errors, validateDescriptorPayload: false);
        PackageDependencyValidator.Validate(manifest.DependsOn, errors);
        Assert.Contains(errors, static error => error.Contains("limit", StringComparison.Ordinal));
        Assert.Contains(errors, static error => error.Contains("null", StringComparison.Ordinal));
    }

    [Fact]
    public void NullManifest_IsReportedByBothSharedEntryPoints()
    {
        var errors = new List<string>();
        PackageRpcImportValidator.Validate(null!, errors);
        Assert.Single(errors);
        errors.Clear();
        PackageContractValidator.Validate(null!, new Dictionary<string, string>(), errors, validateDescriptorPayload: false);
        Assert.Single(errors);
    }

    [Fact]
    public void MalformedNestedCollections_AreBoundedAndNullSafe()
    {
        var manifest = new SunderPackageManifest
        {
            PackageArtifactImports = [new() { AuthorityConstraints = new PrefixOnlyList<SunderPackageAuthorityConstraintManifest>(128) }],
            PackageApiImports = [new()
            {
                BundleRefs = new PrefixOnlyList<SunderPackageContractReferenceManifest>(128),
                UseRefs = new PrefixOnlyList<SunderPackageContractUseReferenceManifest>(128),
            }],
            ProviderImports = [new() { ProviderRefs = new PrefixOnlyList<SunderPackageProviderReferenceManifest>(128) }],
            PlatformImports = [new() { ProviderRefs = new PrefixOnlyList<SunderPackageProviderReferenceManifest>(128) }],
        };
        var errors = new List<string>();
        PackageContractValidator.Validate(manifest, new Dictionary<string, string>(), errors, validateDescriptorPayload: false);
        Assert.Contains(errors, static error => error.Contains("limit", StringComparison.Ordinal));
        Assert.Contains(errors, static error => error.Contains("null", StringComparison.Ordinal));
    }

    private static JsonObject ApiManifest(string presence = "required")
        => JsonNode.Parse($$"""
            {
              "id":"example.consumer","version":"1.0.0",
              "targets":[{"role":"runtime","rid":"linux-x64"}],
              "dependsOn":[{"packageId":"example.dependency","versionRange":">=1.0.0 <2.0.0","presence":"{{presence}}"}],
              "contractBundles":[{
                "contractId":"example.rpc.api","version":"1.0.0","descriptorPath":"contracts/api.json",
                "descriptorSha256":"{{DescriptorHash}}","abiSha256":"{{AbiHash}}","exposure":"public",
                "source":"imported","authorityKind":"package","authorityId":"example.dependency",
                "selectedAuthorityRelease":"1.2.3","selectedAuthorityContentIdentity":"{{ContentHash}}"
              }],
              "usesContracts":[{"role":"runtime","contractId":"example.rpc.api","contractVersion":"1.0.0",
                "abiSha256":"{{AbiHash}}","required":false,"actions":["discover","invoke"]}],
              "provides":[],
              "packageArtifactImports":[{
                "packageId":"example.dependency","requestedVersionRange":">=1.0.0 <2.0.0","includePrerelease":false,
                "selectedPackageVersion":"1.2.3","selectedPackageContentIdentity":"{{ContentHash}}",
                "publicContractCount":1,"publicInventorySha256":"{{InventoryHash}}",
                "authorityConstraints":[{"authorityKind":"package","authorityId":"example.dependency",
                  "selectedAuthorityRelease":"1.2.3","selectedAuthorityContentIdentity":"{{ContentHash}}"}]
              }],
              "packageApiImports":[{"packageId":"example.dependency","presence":"{{presence}}",
                "bundleRefs":[{"contractId":"example.rpc.api","contractVersion":"1.0.0"}],
                "useRefs":[{"role":"runtime","contractId":"example.rpc.api","contractVersion":"1.0.0"}]}],
              "providerImports":[],"platformImports":[]
            }
            """)!.AsObject();

    private static JsonObject OwnedManifest()
    {
        var json = ApiManifest();
        var bundle = At(json, "contractBundles/0").AsObject();
        bundle["source"] = "owned";
        bundle["authorityId"] = "example.consumer";
        bundle.Remove("selectedAuthorityRelease");
        bundle.Remove("selectedAuthorityContentIdentity");
        json["packageArtifactImports"] = new JsonArray();
        json["packageApiImports"] = new JsonArray();
        json["dependsOn"] = new JsonArray();
        return json;
    }

    private static JsonObject ProviderManifest()
    {
        var json = ApiManifest();
        json["packageApiImports"] = new JsonArray();
        json["usesContracts"] = new JsonArray();
        json["provides"] = JsonNode.Parse($$"""
            [{"providerId":"example.consumer.provider","role":"runtime","contractId":"example.rpc.api","contractVersion":"1.0.0",
              "descriptorSha256":"{{DescriptorHash}}","abiSha256":"{{AbiHash}}","audience":"public"}]
            """);
        json["providerImports"] = new JsonArray(new JsonObject
        {
            ["key"] = "apiProvider", ["packageId"] = "example.dependency", ["contractId"] = "example.rpc.api", ["contractVersion"] = "1.0.0",
            ["bundleRef"] = Reference("example.rpc.api"),
            ["providerRefs"] = new JsonArray(Reference("example.rpc.api", "runtime", "example.consumer.provider")),
        });
        return json;
    }

    private static JsonObject PlatformManifest()
    {
        var json = ProviderManifest();
        json["dependsOn"] = new JsonArray();
        json["packageArtifactImports"] = new JsonArray();
        json["providerImports"] = new JsonArray();
        var bundle = At(json, "contractBundles/0").AsObject();
        bundle["contractId"] = "sunder.stack.contributor";
        bundle["authorityKind"] = "platform";
        bundle["authorityId"] = "sunder.core";
        Set(json, "provides/0/contractId", JsonValue.Create("sunder.stack.contributor"));
        json["platformImports"] = new JsonArray(new JsonObject
        {
            ["key"] = "stack", ["authorityKind"] = "platform", ["authorityId"] = "sunder.core",
            ["authorityReleaseRange"] = ">=1.0.0 <2.0.0", ["includePrerelease"] = false,
            ["selectedAuthorityRelease"] = "1.2.3", ["selectedAuthorityContentIdentity"] = ContentHash,
            ["contractId"] = "sunder.stack.contributor", ["contractVersion"] = "1.0.0",
            ["bundleRef"] = Reference("sunder.stack.contributor"),
            ["providerRefs"] = new JsonArray(Reference("sunder.stack.contributor", "runtime", "example.consumer.provider")),
        });
        return json;
    }

    private static JsonObject ReexportManifest(string presence)
    {
        var json = ApiManifest(presence);
        var artifact = At(json, "packageArtifactImports/0").DeepClone();
        artifact["packageId"] = "example.reexport";
        artifact["requestedVersionRange"] = ">=3.0.0 <4.0.0";
        artifact["selectedPackageVersion"] = "3.2.1";
        artifact["selectedPackageContentIdentity"] = InventoryHash;
        json["packageArtifactImports"]!.AsArray().Add(artifact);
        Set(json, "packageApiImports/0/packageId", JsonValue.Create("example.reexport"));
        json["dependsOn"]!.AsArray().Add(new JsonObject
        {
            ["packageId"] = "example.reexport", ["versionRange"] = ">=3.0.0 <4.0.0", ["presence"] = presence,
        });
        return json;
    }

    private static JsonObject ReexportProviderManifest()
    {
        var json = ReexportManifest("required");
        var provider = ProviderManifest();
        json["packageApiImports"] = new JsonArray();
        json["usesContracts"] = new JsonArray();
        var alpha = At(provider, "provides/0").DeepClone();
        alpha["providerId"] = "example.consumer.alpha";
        var zulu = alpha.DeepClone();
        zulu["providerId"] = "example.consumer.zulu";
        zulu["audience"] = "owner";
        json["provides"] = new JsonArray(alpha, zulu);
        var ownerImport = At(provider, "providerImports/0").DeepClone();
        ownerImport["key"] = "minimalProvider";
        ownerImport["providerRefs"] = new JsonArray(
            Reference("example.rpc.api", "runtime", "example.consumer.alpha"),
            Reference("example.rpc.api", "runtime", "example.consumer.zulu"));
        var reexportImport = ownerImport.DeepClone();
        reexportImport["key"] = "reexportProvider";
        reexportImport["packageId"] = "example.reexport";
        json["providerImports"] = new JsonArray(ownerImport, reexportImport);
        return json;
    }

    private static JsonObject ReexportRoleManifest(string ownerRoles, string reexportRoles)
    {
        var json = ReexportManifest("optional");
        AddSecondReexportContract(json);
        AddAppTarget(json);
        var ownerApi = At(json, "packageApiImports/0").DeepClone();
        ownerApi["packageId"] = "example.dependency";
        json["packageApiImports"]!.AsArray().Insert(0, ownerApi);
        for (var group = 0; group < 2; group++)
        {
            var references = new JsonArray();
            foreach (var role in (group == 0 ? ownerRoles : reexportRoles).Split(','))
                foreach (var id in new[] { "example.rpc.api", "example.rpc.second" })
                    references.Add(Reference(id, role));
            Set(json, $"packageApiImports/{group}/useRefs", references);
        }
        var template = At(json, "usesContracts/0").DeepClone();
        var uses = new JsonArray();
        foreach (var role in (ownerRoles + "," + reexportRoles).Split(',').Distinct().Order(StringComparer.Ordinal))
            foreach (var id in new[] { "example.rpc.api", "example.rpc.second" })
            {
                var use = template.DeepClone();
                use["role"] = role;
                use["contractId"] = id;
                // Required readiness is independently coalesced within each role.
                use["required"] = role == "app";
                uses.Add(use);
            }
        json["usesContracts"] = uses;
        return json;
    }

    private static void AddAppTarget(JsonObject json)
        => json["targets"]!.AsArray().Insert(0, new JsonObject { ["role"] = "app", ["rid"] = "linux-x64" });

    private static void AddSecondReexportContract(JsonObject json)
    {
        var bundle = At(json, "contractBundles/0").DeepClone();
        bundle["contractId"] = "example.rpc.second";
        bundle["descriptorPath"] = "contracts/second.json";
        json["contractBundles"]!.AsArray().Add(bundle);
        var use = At(json, "usesContracts/0").DeepClone();
        use["contractId"] = "example.rpc.second";
        json["usesContracts"]!.AsArray().Add(use);
        At(json, "packageApiImports/0/bundleRefs").AsArray().Add(Reference("example.rpc.second"));
        At(json, "packageApiImports/0/useRefs").AsArray().Add(Reference("example.rpc.second", "runtime"));
        Set(json, "packageArtifactImports/0/publicContractCount", JsonValue.Create(2));
        Set(json, "packageArtifactImports/1/publicContractCount", JsonValue.Create(2));
    }

    private static JsonObject Reference(string contractId, string? role = null, string? providerId = null)
    {
        var reference = new JsonObject { ["contractId"] = contractId, ["contractVersion"] = "1.0.0" };
        if (role is not null) reference["role"] = role;
        if (providerId is not null) reference["providerId"] = providerId;
        return reference;
    }

    private static SunderPackageManifest Read(JsonObject json)
        => StrictJsonSerializer.Deserialize(json.ToJsonString(), JsonContext.SunderPackageManifest)!;

    private static List<string> Validate(JsonObject json)
    {
        SunderPackageManifest manifest;
        try
        {
            manifest = Read(json);
        }
        catch (JsonException exception)
        {
            return [exception.Message];
        }
        return Validate(manifest);
    }

    private static List<string> Validate(SunderPackageManifest manifest)
    {
        var errors = new List<string>();
        PackageContractValidator.Validate(manifest, new Dictionary<string, string>(), errors, validateDescriptorPayload: false);
        PackageDependencyValidator.Validate(manifest.DependsOn, errors);
        return errors;
    }

    private static JsonNode At(JsonNode json, string path)
    {
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            json = json is JsonArray array ? array[int.Parse(segment)]! : json[segment]!;
        }
        return json;
    }

    private static void Set(JsonNode json, string path, JsonNode? value)
    {
        var separator = path.LastIndexOf('/');
        At(json, path[..separator]).AsObject()[path[(separator + 1)..]] = value;
    }

    private static void Remove(JsonNode json, string path)
    {
        var separator = path.LastIndexOf('/');
        At(json, path[..separator]).AsObject().Remove(path[(separator + 1)..]);
    }

    private sealed class PrefixOnlyList<T>(int limit) : IReadOnlyList<T> where T : class
    {
        public int Count => int.MaxValue;
        public T this[int index] => index < limit ? null! : throw new InvalidOperationException("Unbounded collection access.");
        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; ; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
