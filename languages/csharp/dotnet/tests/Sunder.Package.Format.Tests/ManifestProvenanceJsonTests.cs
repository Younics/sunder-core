using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class ManifestProvenanceJsonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);

    [Theory]
    [InlineData("packageArtifactImports")]
    [InlineData("packageApiImports")]
    [InlineData("providerImports")]
    [InlineData("platformImports")]
    public void OptionalImportGroups_MayBeAbsentOrEmptyButNotExplicitNull(string section)
    {
        foreach (var manifest in ReadAll("{}"))
        {
            Assert.Null(manifest.PackageArtifactImports);
            Assert.Null(manifest.PackageApiImports);
            Assert.Null(manifest.ProviderImports);
            Assert.Null(manifest.PlatformImports);
        }
        Assert.All(ReadAll($$"""{"{{section}}":[]}"""), manifest =>
            Assert.Empty(JsonSerializer.SerializeToNode(manifest)![section]!.AsArray()));
        AssertRejected($$"""{"{{section}}":null}""");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("authorityKind")]
    [InlineData("authorityId")]
    [InlineData("selectedAuthorityRelease")]
    [InlineData("selectedAuthorityContentIdentity")]
    public void OwnedBundles_RejectExplicitNullProvenance(string member)
    {
        var json = JsonNode.Parse("""
            {"id":"example.owner","contractBundles":[
              {"source":"owned","authorityKind":"package","authorityId":"example.owner"}]}
            """)!;
        foreach (var manifest in ReadAll(json.ToJsonString()))
        {
            var bundle = Assert.Single(manifest.ContractBundles!);
            Assert.Null(bundle!.SelectedAuthorityRelease);
            Assert.Null(bundle.SelectedAuthorityContentIdentity);
        }
        json["contractBundles"]![0]![member] = null;
        AssertRejected(json.ToJsonString());
    }

    [Theory]
    [InlineData("authorityKind")]
    [InlineData("authorityId")]
    [InlineData("selectedAuthorityRelease")]
    [InlineData("selectedAuthorityContentIdentity")]
    public void PackageAuthorityConstraints_RejectExplicitNullAuthorityEvidence(string member)
    {
        var json = JsonNode.Parse("""
            {"packageArtifactImports":[{"authorityConstraints":[
              {"authorityKind":"package","authorityId":"example.owner",
               "selectedAuthorityRelease":"1.0.0","selectedAuthorityContentIdentity":"content"}]}]}
            """)!;
        Assert.All(ReadAll(json.ToJsonString()), manifest =>
            Assert.Equal("example.owner", manifest.PackageArtifactImports![0]!.AuthorityConstraints![0]!.AuthorityId));
        json["packageArtifactImports"]![0]!["authorityConstraints"]![0]![member] = null;
        AssertRejected(json.ToJsonString());
    }

    [Theory]
    [InlineData("""{"packageArtifactImports":[{MEMBER}]}""",
        "packageId,requestedVersionRange,includePrerelease,selectedPackageVersion,selectedPackageContentIdentity,publicContractCount,publicInventorySha256,authorityConstraints")]
    [InlineData("""{"packageArtifactImports":[{"authorityConstraints":[{MEMBER}]}]}""",
        "authorityKind,authorityId,selectedAuthorityRelease,selectedAuthorityContentIdentity")]
    [InlineData("""{"packageApiImports":[{MEMBER}]}""", "packageId,presence,bundleRefs,useRefs")]
    [InlineData("""{"packageApiImports":[{"bundleRefs":[{MEMBER}]}]}""", "contractId,contractVersion")]
    [InlineData("""{"packageApiImports":[{"useRefs":[{MEMBER}]}]}""", "role,contractId,contractVersion")]
    [InlineData("""{"providerImports":[{MEMBER}]}""", "key,packageId,contractId,contractVersion,bundleRef,providerRefs")]
    [InlineData("""{"providerImports":[{"bundleRef":{MEMBER}}]}""", "contractId,contractVersion")]
    [InlineData("""{"providerImports":[{"providerRefs":[{MEMBER}]}]}""", "role,providerId,contractId,contractVersion")]
    [InlineData("""{"platformImports":[{MEMBER}]}""",
        "key,authorityKind,authorityId,authorityReleaseRange,includePrerelease,selectedAuthorityRelease,selectedAuthorityContentIdentity,contractId,contractVersion,bundleRef,providerRefs")]
    [InlineData("""{"platformImports":[{"bundleRef":{MEMBER}}]}""", "contractId,contractVersion")]
    [InlineData("""{"platformImports":[{"providerRefs":[{MEMBER}]}]}""", "role,providerId,contractId,contractVersion")]
    [InlineData("""{"dependsOn":[{MEMBER}]}""", "presence")]
    public void NewMembers_RejectExplicitNullAtEveryNestingLevel(string template, string members)
    {
        foreach (var member in members.Split(','))
        {
            AssertRejected(template.Replace("MEMBER", $"\"{member}\":null", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("""{"packageArtifactImports":[null]}""")]
    [InlineData("""{"packageArtifactImports":[{"authorityConstraints":[{},null]}]}""")]
    [InlineData("""{"packageApiImports":[null]}""")]
    [InlineData("""{"packageApiImports":[{"bundleRefs":[null]}]}""")]
    [InlineData("""{"packageApiImports":[{"useRefs":[null]}]}""")]
    [InlineData("""{"providerImports":[null]}""")]
    [InlineData("""{"providerImports":[{"providerRefs":[null]}]}""")]
    [InlineData("""{"platformImports":[null]}""")]
    [InlineData("""{"platformImports":[{"providerRefs":[null]}]}""")]
    [InlineData("""{"contractBundles":[{}, {"authority\u0049d":null}]}""")]
    [InlineData("""{"dependsOn":[{}, {"pres\u0065nce":null}]}""")]
    public void NullEntriesAndEscapedPropertyNames_CannotBypassGuard(string json)
        => AssertRejected(json);

    [Theory]
    [InlineData("""{"packageApiImports":[],"icon":null,"targets":null,"contractBundles":null,"dependsOn":null}""")]
    [InlineData("""{"contractBundles":[{"source":"owned","version":null,"descriptorPath":null}],"usesContracts":null}""")]
    [InlineData("""{"dependsOn":[{"presence":"required","packageId":null,"versionRange":null}],"provides":null}""")]
    [InlineData("""{"targets":[{"executionRuntime":null,"views":[{"icon":null}],"requiredHostCapabilities":[null]}]}""")]
    [InlineData("""{"packageArtifactImports":[{"includePrerelease":false,"publicContractCount":0}],"platformImports":[{"includePrerelease":false}]}""")]
    public void Guard_DoesNotChangeLongstandingNullOrNonNullValueValidation(string json)
        => Assert.All(ReadAll(json), static manifest => Assert.NotNull(manifest));

    [Fact]
    public void Guard_DoesNotApplyManifestRulesToOtherJsonDocuments()
    {
        const string json = """{"packageApiImports":null,"contractBundles":[{"source":null}]}""";
        using var document = StrictJsonSerializer.ParseDocument(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("packageApiImports").ValueKind);
        var value = StrictJsonSerializer.Deserialize<JsonElement>(json, JsonSerializerOptions.Default);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("packageApiImports").ValueKind);
        Assert.Null(StrictJsonSerializer.Deserialize("null", JsonContext.SunderStackManifest));
    }

    [Theory]
    [InlineData("""{"id":"first","id":"second"}""")]
    [InlineData("""{"packageApiImports":[],"packageApiImports":[]}""")]
    [InlineData("""{"contractBundles":[{"source":"owned","source":"owned"}]}""")]
    [InlineData("""{"providerImports":[{"bundleRef":{"contractId":"first","contract\u0049d":"second"}}]}""")]
    public void AllDeserializerOverloads_StillRejectDuplicateProperties(string json)
        => AssertRejected(json, "Duplicate JSON property");

    [Theory]
    [InlineData("""{"PackageApiImports":[]}""")]
    [InlineData("""{"providerImports":[{"unexpected":true}]}""")]
    public void OptionsOverloads_PreserveStrictMemberMatching(string json)
        => AssertRejected(json, "could not be mapped");

    [Theory]
    [InlineData("""{"packageArtifactImports":null}""")]
    [InlineData("""{"packageApiImports":null}""")]
    [InlineData("""{"providerImports":null}""")]
    [InlineData("""{"platformImports":null}""")]
    [InlineData("""{"contractBundles":[{"source":"owned","selectedAuthorityRelease":null}]}""")]
    [InlineData("""{"contractBundles":[{"source":"owned","selectedAuthorityContentIdentity":null}]}""")]
    [InlineData("""{"packageArtifactImports":[{"authorityConstraints":[{"authorityKind":"package","selectedAuthorityContentIdentity":null}]}]}""")]
    [InlineData("""{"platformImports":[{"providerRefs":[{"role":null}]}]}""")]
    [InlineData("""{"dependsOn":[{"presence":null}]}""")]
    public async Task ArchiveReaders_RejectExplicitNullBeforeGraphValidation(string json)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-manifest-provenance-json");
        temporary.WriteAllText(SunderPackageFormat.ManifestPath, json);
        temporary.WriteAllText(SunderPackageFormat.ContentIndexPath, """{"schemaVersion":1,"files":[]}""");
        temporary.WriteAllText(SunderPackageProjectionFormat.DescriptorPath,
            JsonSerializer.Serialize(new SunderPackageProjectionDescriptor()));

        var package = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(temporary.RootPath);
        Assert.False(package.Success);
        Assert.Null(package.Manifest);
        Assert.Contains("Failed to parse package metadata: Explicit JSON null", Assert.Single(package.Errors), StringComparison.Ordinal);

        var projection = await SunderPackageProjectionArchiveInspector.ValidateExtractedProjectionAsync(temporary.RootPath);
        Assert.False(projection.Success);
        Assert.Null(projection.Manifest);
        Assert.Contains("Failed to parse projection metadata: Explicit JSON null", Assert.Single(projection.Errors), StringComparison.Ordinal);
    }

    private static SunderPackageManifest[] ReadAll(string json)
        =>
        [
            StrictJsonSerializer.Deserialize(json, JsonContext.SunderPackageManifest)!,
            StrictJsonSerializer.Deserialize<SunderPackageManifest>(json, JsonOptions)!,
            StrictJsonSerializer.Deserialize<SunderPackageManifest>(Encoding.UTF8.GetBytes(json), JsonOptions)!,
        ];

    private static void AssertRejected(string json, string expectedError = "Explicit JSON null")
    {
        var errors = new[]
        {
            Assert.Throws<JsonException>(() => StrictJsonSerializer.Deserialize(json, JsonContext.SunderPackageManifest)),
            Assert.Throws<JsonException>(() => StrictJsonSerializer.Deserialize<SunderPackageManifest>(json, JsonOptions)),
            Assert.Throws<JsonException>(() => StrictJsonSerializer.Deserialize<SunderPackageManifest>(Encoding.UTF8.GetBytes(json), JsonOptions)),
        };
        Assert.All(errors, error => Assert.Contains(expectedError, error.Message, StringComparison.Ordinal));
    }
}
