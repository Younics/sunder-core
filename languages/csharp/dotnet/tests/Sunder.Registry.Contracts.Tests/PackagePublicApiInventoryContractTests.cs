using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Registry.Contracts.Tests;

public sealed class PackagePublicApiInventoryContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Theory]
    [InlineData("empty", "d974cc19869d591bda825aaaaa74a5a4a7aa295003e8f42b42a25ce9aeb4fa30", 0)]
    [InlineData("nonempty", "1d1c5dbcae220e7052df3f87f9b4fe9e5feb3fd2a23c70f2b41453039ccb9fbe", 1)]
    [InlineData("mixed-authority", "6f69988fd727caa6b6eeab5a8f0635264a3c663b7a03cfd865784fa4ef022329", 2)]
    [InlineData("platform-only", "aad59a4a18b4d2e4052698580bc863fc2de8c2aabe2a8343216fae3ba82b64a7", 1)]
    public void InventoryDtos_RoundTripFrozenPayloadBytesAndHashes(string name, string expectedHash, int count)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", $"package-public-inventory-{name}.json"));
        var inventory = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(expected, JsonOptions)!;
        Assert.Equal(expected, JsonSerializer.Serialize(inventory, JsonOptions) + "\n");
        Assert.Equal(count, inventory.PublicContractCount);
        Assert.Equal(count, inventory.Contracts.Count);

        var canonical = JsonSerializer.SerializeToUtf8Bytes(inventory, new JsonSerializerOptions(JsonOptions) { WriteIndented = false });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("sunder.package-public-api-inventory.v1\0"u8);
        hash.AppendData(canonical);
        Assert.Equal(expectedHash, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());

        using var document = JsonDocument.Parse(canonical);
        Assert.Equal(
            new[] { "schemaVersion", "packageId", "packageVersion", "packageContentIdentity", "publicContractCount", "contracts" },
            document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.DoesNotContain("publicInventorySha256", Encoding.UTF8.GetString(canonical), StringComparison.Ordinal);
        foreach (var contract in document.RootElement.GetProperty("contracts").EnumerateArray())
        {
            var required = new[]
            {
                "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "artifactSource",
                "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
            };
            Assert.Equal(
                required,
                contract.EnumerateObject().Select(static property => property.Name));
        }
    }

    [Fact]
    public void InventoryResponse_UsesOnlyTheChosenEnvelopeMembersAndRoundTripsItsGolden()
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "package-public-inventory-response.json"));
        var response = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(expected, JsonOptions)!;
        Assert.Equal(expected, JsonSerializer.Serialize(response, JsonOptions) + "\n");
        Assert.Equal("example.dependency", response.Inventory.PackageId);
        Assert.Equal("1d1c5dbcae220e7052df3f87f9b4fe9e5feb3fd2a23c70f2b41453039ccb9fbe", response.PublicInventorySha256);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.Equal(new[] { "inventory", "publicInventorySha256" }, document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(6, document.RootElement.GetProperty("inventory").EnumerateObject().Count());
    }

    [Fact]
    public void InventoryDtos_DefensivelyFreezeCallerCollectionsAndDeserializedCollections()
    {
        var contract = OwnedContract();
        var source = new List<RegistryPackagePublicApiContractV1> { contract };
        var inventory = new RegistryPackagePublicApiInventoryV1(1, "example.owner", "1.0.0", new string('1', 64), 1, source);
        source[0] = contract with { ArtifactSource = "imported" };
        source.Clear();

        Assert.Equal(contract, Assert.Single(inventory.Contracts));
        Assert.Throws<NotSupportedException>(() => ((IList)inventory.Contracts)[0] = source);
        var roundTrip = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(JsonSerializer.Serialize(inventory), JsonOptions)!;
        Assert.Equal(contract, Assert.Single(roundTrip.Contracts));
        Assert.Throws<NotSupportedException>(() => ((IList)roundTrip.Contracts).Clear());
        Assert.Equal(contract, Assert.Single((inventory with { }).Contracts));
    }

    [Fact]
    public void InventoryDtos_PreserveArtifactLocalSourceAndUltimateAuthorityWithoutInferringFromPublisherMetadata()
    {
        var contract = OwnedContract();
        var imported = contract with { ArtifactSource = "imported" };
        var owner = new RegistryPackagePublicApiInventoryV1(1, "example.owner", "1.0.0", new string('1', 64), 1, [contract]);
        var reexport = new RegistryPackagePublicApiInventoryV1(1, "example.reexport", "2.0.0", new string('2', 64), 1, [imported]);
        var roundTrip = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(JsonSerializer.Serialize(reexport), JsonOptions)!;

        Assert.Equal("owned", Assert.Single(owner.Contracts).ArtifactSource);
        Assert.Equal(imported, Assert.Single(roundTrip.Contracts));
        Assert.Equal("example.owner", imported.AuthorityId);
        Assert.Equal("1.0.0", imported.SelectedAuthorityRelease);
        Assert.Equal(new string('1', 64), imported.SelectedAuthorityContentIdentity);
    }

    [Fact]
    public void InventoryDtos_SpellWireMembersIndependentlyOfSerializerNamingPolicyWithoutSigningMetadata()
    {
        var inventory = new RegistryPackagePublicApiInventoryV1(1, "example.owner", "1.0.0", new string('1', 64), 1, [OwnedContract()]);
        var response = new RegistryPackagePublicApiInventoryResponseV1(inventory, new string('2', 64));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, options));

        Assert.True(document.RootElement.TryGetProperty("publicInventorySha256", out _));
        var contract = document.RootElement.GetProperty("inventory").GetProperty("contracts")[0];
        Assert.True(contract.TryGetProperty("descriptorSha256", out _));
        Assert.False(contract.TryGetProperty("catalogSigningKeyId", out _));
        Assert.False(contract.TryGetProperty("catalogTrustGeneration", out _));
        Assert.False(contract.TryGetProperty("catalogTrustFingerprint", out _));
    }

    [Fact]
    public void InventoryDtos_DisallowUnknownTransportMembersWithoutIntroducingAuthorityLogic()
    {
        var response = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "package-public-inventory-response.json"));
        foreach (var invalid in new[]
        {
            response.Replace("\"inventory\":", "\"payload\":", StringComparison.Ordinal),
            response.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\"etag\":\"unexpected\",", StringComparison.Ordinal),
            response.Replace("\"artifactSource\": \"owned\",", "\"artifactSource\": \"owned\",\"exposure\":\"private\",", StringComparison.Ordinal),
        })
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(invalid, JsonOptions));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_RequiresEveryMemberIncludingEmptyInventoryCountWithDefaultAndWebOptions(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        foreach (var (type, source) in WireObjects())
        {
            Assert.NotNull(JsonSerializer.Deserialize(source.ToJsonString(), type, options));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("null", type, options));
            foreach (var member in source.Select(static property => property.Key))
            {
                var missing = source.DeepClone().AsObject();
                missing.Remove(member);
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(missing.ToJsonString(), type, options));

                var explicitNull = source.DeepClone();
                explicitNull[member] = null;
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(explicitNull.ToJsonString(), type, options));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_RejectsMissingEmptyCountAndNullContractItemsInsideResponses(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        var inventory = WireObjects()[0].Value;
        var response = new JsonObject
        {
            ["inventory"] = inventory,
            ["publicInventorySha256"] = "d974cc19869d591bda825aaaaa74a5a4a7aa295003e8f42b42a25ce9aeb4fa30",
        };
        inventory.Remove("publicContractCount");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(response.ToJsonString(), options));

        inventory["publicContractCount"] = 1;
        inventory["contracts"] = new JsonArray { (JsonNode?)null };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(inventory.ToJsonString(), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(response.ToJsonString(), options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_DoesNotCoerceScalarOrContainerTypes(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        foreach (var (index, member, value) in new (int, string, object)[]
        {
            (0, "schemaVersion", "1"), (0, "publicContractCount", "0"), (0, "packageId", 123), (0, "contracts", "[]"),
            (1, "descriptorBytes", "2"), (1, "authorityId", true),
            (2, "inventory", "{}"), (2, "publicInventorySha256", 0),
        })
        {
            var (type, source) = WireObjects()[index];
            source[member] = JsonSerializer.SerializeToNode(value);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(source.ToJsonString(), type, options));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_RejectsRetiredSigningMetadata(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        foreach (var member in new[] { "catalogSigningKeyId", "catalogTrustGeneration", "catalogTrustFingerprint" })
        {
            var response = WireObjects()[2].Value;
            var contract = response["inventory"]!["contracts"]![0]!;
            Assert.NotNull(JsonSerializer.Deserialize<RegistryPackagePublicApiContractV1>(contract.ToJsonString(), options));

            contract[member] = null;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiContractV1>(contract.ToJsonString(), options));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(response["inventory"]!.ToJsonString(), options));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(response.ToJsonString(), options));

            var platform = JsonNode.Parse(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "RpcV1Authoring", "package-public-inventory-platform-only.json")))!;
            platform["contracts"]![0]![member] = null;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(platform.ToJsonString(), options));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_RejectsUnknownCaseMismatchedAndDuplicateMembersRegardlessOfOptions(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        foreach (var (type, source) in WireObjects())
        {
            foreach (var member in new[] { "unexpected", "exposure", "firstPublisherPackageId", "etag" })
            {
                var unknown = source.DeepClone();
                unknown[member] = "must-not-be-discarded";
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(unknown.ToJsonString(), type, options));
            }
            var json = source.ToJsonString();
            foreach (var member in source.Select(static property => property.Key))
            {
                var property = JsonSerializer.Serialize(member) + ":";
                var upperCase = JsonSerializer.Serialize(member.ToUpperInvariant());
                var mismatch = json.Replace(property, upperCase + ":", StringComparison.Ordinal);
                Assert.NotEqual(json, mismatch);
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(mismatch, type, options));

                foreach (var duplicateName in new[]
                {
                    JsonSerializer.Serialize(member), upperCase, $"\"\\u{(int)member[0]:x4}{member[1..]}\"",
                })
                {
                    var duplicate = json.Replace(property, $"{duplicateName}:{source[member]!.ToJsonString()},{property}", StringComparison.Ordinal);
                    Assert.NotEqual(json, duplicate);
                    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(duplicate, type, options));
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_DoesNotLaunderNestedDuplicateProvenanceThroughResponses(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        var json = WireObjects()[2].Value.ToJsonString();
        foreach (var member in new[]
        {
            "artifactSource", "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
        })
        {
            var property = JsonSerializer.Serialize(member) + ":";
            foreach (var duplicateName in new[] { member, member.ToUpperInvariant() })
            {
                var invalid = json.Replace(property, $"\"{duplicateName}\":\"untrusted-shadow-value\",{property}", StringComparison.Ordinal);
                Assert.NotEqual(json, invalid);
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(invalid, options));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_RemainsDistinctFromGrammarAndAuthorityValidation(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        var root = WireObjects()[0].Value;
        root["packageId"] = "not a canonical ID";
        root["packageVersion"] = "not a SemVer";
        root["packageContentIdentity"] = "not a digest";
        root["publicContractCount"] = 17;
        var inventory = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(root.ToJsonString(), options)!;
        Assert.Equal("not a canonical ID", inventory.PackageId);
        Assert.Equal("not a SemVer", inventory.PackageVersion);
        Assert.Equal("not a digest", inventory.PackageContentIdentity);
        Assert.Equal(17, inventory.PublicContractCount);

        var contract = OwnedContract() with { AuthorityKind = "unverified", AuthorityId = "another package" };
        Assert.Equal(contract, JsonSerializer.Deserialize<RegistryPackagePublicApiContractV1>(JsonSerializer.Serialize(contract, options), options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_DoesNotWriteNullRootsOrNullRequiredRecordMembers(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        foreach (var (type, _) in WireObjects())
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize((object?)null, type, options));
        }
        var contract = OwnedContract() with { AuthorityId = null! };
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(contract, options));
        var inventory = new RegistryPackagePublicApiInventoryV1(1, "example.empty", "1.0.0", new string('1', 64), 0, []);
        var response = new RegistryPackagePublicApiInventoryResponseV1(inventory, new string('2', 64));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(response with { Inventory = null! }, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(response with { PublicInventorySha256 = null! }, options));
    }

    [Fact]
    public void InventoryConstructor_RejectsNullRequiredReferencesAndCopiesReadOnlyCollections()
    {
        Assert.Throws<ArgumentNullException>(() => new RegistryPackagePublicApiInventoryV1(1, null!, "1.0.0", "hash", 0, []));
        Assert.Throws<ArgumentNullException>(() => new RegistryPackagePublicApiInventoryV1(1, "example.empty", null!, "hash", 0, []));
        Assert.Throws<ArgumentNullException>(() => new RegistryPackagePublicApiInventoryV1(1, "example.empty", "1.0.0", null!, 0, []));
        Assert.Throws<ArgumentNullException>(() => new RegistryPackagePublicApiInventoryV1(1, "example.empty", "1.0.0", "hash", 0, null!));
        Assert.Throws<ArgumentException>(() => new RegistryPackagePublicApiInventoryV1(1, "example.empty", "1.0.0", "hash", 1, [null!]));

        var source = new[] { OwnedContract() };
        var inventory = new RegistryPackagePublicApiInventoryV1(1, "example.owner", "1.0.0", "hash", 1, Array.AsReadOnly(source));
        source[0] = null!;
        Assert.Equal(OwnedContract(), Assert.Single(inventory.Contracts));
        Assert.All(typeof(RegistryPackagePublicApiInventoryV1).GetProperties(), static property => Assert.Null(property.SetMethod));
        Assert.DoesNotContain(typeof(RegistryPackagePublicApiInventoryV1).Assembly.GetExportedTypes(),
            static type => type.Name.StartsWith("RegistryPackagePublicApiInventoryJsonConverter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_BoundsContainerCountsBeforeAllocatingADocument(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        var json = "{\"contracts\":[" + string.Join(',', Enumerable.Repeat("{}", 100_000)) + "]}";
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(json, options));
        Assert.Contains("structural value/property budget", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireShape_StructuralBudgetAdmitsACompletePlatformInventoryResponse(bool web)
    {
        JsonSerializerOptions? options = web ? new(JsonSerializerDefaults.Web) : null;
        var fixture = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "RpcV1Authoring", "package-public-inventory-platform-only.json"));
        var inventory = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryV1>(fixture, options)!;
        var template = Assert.Single(inventory.Contracts);
        var contracts = Enumerable.Range(0, 128)
            .Select(index => template with { ContractId = $"example.contract{index:D3}" }).ToArray();
        inventory = new(inventory.SchemaVersion, inventory.PackageId, inventory.PackageVersion, inventory.PackageContentIdentity, 128, contracts);
        var response = new RegistryPackagePublicApiInventoryResponseV1(inventory, new string('0', 64));
        var roundTrip = JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(JsonSerializer.Serialize(response, options), options)!;

        Assert.Equal(128, roundTrip.Inventory.Contracts.Count);
        Assert.All(roundTrip.Inventory.Contracts, contract => Assert.Equal(template.SelectedAuthorityContentIdentity, contract.SelectedAuthorityContentIdentity));
    }

    [Fact]
    public void DescriptorMetadata_EtagIsDerivedReadOnlyEvidenceAndPreservesThePositionalConstructor()
    {
        var metadata = new RegistryContractDescriptorMetadata(
            "example.rpc", "1.0.0", new string('a', 64), new string('b', 64), "public", 42,
            "https://registry.example/descriptor", "example.publisher", "2.0.0", DateTimeOffset.UnixEpoch);
        var (id, version, descriptorHash, _, _, _, _, publisherId, publisherVersion, _) = metadata;
        Assert.Equal("example.rpc", id);
        Assert.Equal("1.0.0", version);
        Assert.Equal("example.publisher", publisherId);
        Assert.Equal("2.0.0", publisherVersion);
        Assert.Equal($"\"{descriptorHash}\"", metadata.ETag);
        Assert.Null(typeof(RegistryContractDescriptorMetadata).GetProperty(nameof(RegistryContractDescriptorMetadata.ETag))!.SetMethod);

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(metadata.ETag, document.RootElement.GetProperty("etag").GetString());
        Assert.False(document.RootElement.TryGetProperty("eTag", out _));
        var tampered = json.Replace("\"etag\": \"\\\"" + descriptorHash + "\\\"\"", "\"etag\": \"untrusted\"", StringComparison.Ordinal);
        Assert.NotEqual(json, tampered);
        Assert.Equal(metadata.ETag, JsonSerializer.Deserialize<RegistryContractDescriptorMetadata>(tampered, JsonOptions)!.ETag);
        Assert.Equal($"\"{new string('c', 64)}\"", (metadata with { DescriptorSha256 = new string('c', 64) }).ETag);
    }

    private static RegistryPackagePublicApiContractV1 OwnedContract()
        => new("example.rpc", "1.0.0", 2, new string('a', 64), new string('b', 64), "owned", "package",
            "example.owner", "1.0.0", new string('1', 64));

    private static (Type Type, JsonObject Value)[] WireObjects()
    {
        var empty = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "RpcV1Authoring", "package-public-inventory-empty.json")))!.AsObject();
        var response = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "package-public-inventory-response.json")))!.AsObject();
        return
        [
            (typeof(RegistryPackagePublicApiInventoryV1), empty),
            (typeof(RegistryPackagePublicApiContractV1), response["inventory"]!["contracts"]![0]!.AsObject()),
            (typeof(RegistryPackagePublicApiInventoryResponseV1), response),
        ];
    }
}
