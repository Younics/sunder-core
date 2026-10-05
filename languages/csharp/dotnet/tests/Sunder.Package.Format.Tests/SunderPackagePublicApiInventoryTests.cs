using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderPackagePublicApiInventoryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void ParseVerifyHash_PreservesEveryFrozenInventoryPreimageAndDigest()
    {
        using var expectations = JsonDocument.Parse(File.ReadAllBytes(Fixture("expectations.json")));
        foreach (var evidence in expectations.RootElement.GetProperty("immutableArtifactEvidence").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Fixture(evidence.GetProperty("inventoryFixturePath").GetString()!));
            var expectedHash = evidence.GetProperty("publicInventorySha256").GetString()!;
            var inventory = SunderPackagePublicApiInventory.ParseVerifyHash(bytes, expectedHash);
            using var source = JsonDocument.Parse(bytes);
            var expectedCanonical = JsonSerializer.SerializeToUtf8Bytes(source.RootElement, JsonOptions);

            Assert.Equal(expectedCanonical, inventory.ToCanonicalUtf8());
            Assert.Equal(expectedHash, inventory.PublicInventorySha256);
            Assert.Equal(expectedHash, SunderPackagePublicApiInventory.Parse(expectedCanonical).PublicInventorySha256);
            Assert.Equal(evidence.GetProperty("publicContractCount").GetInt32(), inventory.Payload.GetProperty("contracts").GetArrayLength());
            Assert.Equal(Encoding.UTF8.GetString(expectedCanonical), inventory.Payload.GetRawText());
            Assert.Equal((byte)'}', expectedCanonical[^1]);
            Assert.NotEqual(LowerHash(expectedCanonical), expectedHash);
            Assert.NotEqual(InventoryHash([.. expectedCanonical, (byte)'\n']), expectedHash);
        }
    }

    [Theory]
    [InlineData("invalid/package-inventory-missing-required.json")]
    [InlineData("semantic-invalid/package-inventory-count-mismatch.json")]
    public void Parse_RejectsFrozenInvalidInventories(string path)
        => Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(File.ReadAllBytes(Fixture(path))));

    [Fact]
    public void Parse_AcceptsEmptyAndPlatformOnlyEvidenceWithoutSelectingAnApi()
    {
        var empty = Parse(Read("empty"));
        var platform = Parse(Read("platform-only"));

        Assert.Empty(empty.Payload.GetProperty("contracts").EnumerateArray());
        var contract = Assert.Single(platform.Payload.GetProperty("contracts").EnumerateArray());
        Assert.Equal("platform", contract.GetProperty("authorityKind").GetString());
        Assert.Equal("imported", contract.GetProperty("artifactSource").GetString());
    }

    [Fact]
    public void Parse_RejectsReorderedRootMembersContractMembersAndContracts()
    {
        var root = Read();
        var version = root["packageVersion"]!.DeepClone();
        root.Remove("packageVersion");
        root.Add("packageVersion", version);
        AssertRejected(root);

        root = Read();
        var contract = root["contracts"]![0]!.AsObject();
        var id = contract["contractId"]!.DeepClone();
        contract.Remove("contractId");
        contract.Add("contractId", id);
        AssertRejected(root);

        root = Read("mixed-authority");
        root["contracts"] = new JsonArray(root["contracts"]![1]!.DeepClone(), root["contracts"]![0]!.DeepClone());
        AssertRejected(root);
    }

    [Theory]
    [InlineData("schemaVersion", "schemaVersion")]
    [InlineData("schemaVersion", "SchemaVersion")]
    [InlineData("contractId", "contractId")]
    [InlineData("contractId", "CONTRACTID")]
    public void Parse_RejectsDuplicateAndAsciiCaseCollidingMembers(string member, string duplicate)
    {
        var json = Encoding.UTF8.GetString(Serialize(Read()));
        json = json.Replace($"\"{member}\":", $"\"{duplicate}\":1,\"{member}\":", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_RejectsDuplicateIdsEvenWhenTheirVersionsDiffer(bool changeVersion)
    {
        var root = Read();
        var contracts = root["contracts"]!.AsArray();
        var duplicate = contracts[0]!.DeepClone();
        if (changeVersion) duplicate["contractVersion"] = "3.0.0";
        contracts.Add(duplicate);
        root["publicContractCount"] = 2;

        AssertRejected(root);
    }

    [Theory]
    [InlineData("exposure")]
    [InlineData("private")]
    [InlineData("canonicalDescriptor")]
    [InlineData("descriptor")]
    [InlineData("firstPublisherPackageId")]
    [InlineData("descriptorDownloadUrl")]
    [InlineData("publishedAtUtc")]
    [InlineData("etag")]
    [InlineData("publicInventorySha256")]
    public void Parse_RejectsNonPayloadMembersAtEitherLevel(string member)
    {
        var root = Read();
        root[member] = "not-in-the-payload";
        AssertRejected(root);

        root = Read();
        root["contracts"]![0]![member] = "not-in-the-payload";
        AssertRejected(root);
    }

    [Fact]
    public void Parse_RejectsEveryMissingOrNullMemberIncludingConditionalTrust()
    {
        var source = Read("mixed-authority");
        foreach (var member in source.Select(static property => property.Key))
        {
            var root = source.DeepClone().AsObject();
            root.Remove(member);
            AssertRejected(root);
            root = source.DeepClone().AsObject();
            root[member] = null;
            AssertRejected(root);
        }
        for (var index = 0; index < 2; index++)
        {
            foreach (var member in source["contracts"]![index]!.AsObject().Select(static property => property.Key))
            {
                var root = source.DeepClone().AsObject();
                root["contracts"]![index]!.AsObject().Remove(member);
                AssertRejected(root);
                root = source.DeepClone().AsObject();
                root["contracts"]![index]![member] = null;
                AssertRejected(root);
            }
        }
        source["contracts"]![0] = null;
        AssertRejected(source);
    }

    [Theory]
    [InlineData("authorityId", "example.other")]
    [InlineData("selectedAuthorityRelease", "1.2.4")]
    [InlineData("selectedAuthorityRelease", "1.2.3+different")]
    [InlineData("selectedAuthorityContentIdentity", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Parse_RejectsOwnedMetadataThatDoesNotExactlyMatchItsArtifact(string member, string value)
    {
        var root = Read();
        root["contracts"]![0]![member] = value;
        var bytes = Serialize(root);
        var error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.ParseVerifyHash(bytes, InventoryHash(bytes)));

        Assert.StartsWith("rpc-authoring.authority-mismatch:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PreservesOwnerAndReexportArtifactSourceWithoutChangingUltimateEvidence()
    {
        var owner = Parse(Read());
        var reexport = Read();
        reexport["packageId"] = "example.reexport";
        reexport["packageVersion"] = "1.4.0";
        reexport["packageContentIdentity"] = new string('2', 64);
        reexport["contracts"]![0]!["artifactSource"] = "imported";
        var imported = Parse(reexport);
        var ownerContract = owner.Payload.GetProperty("contracts")[0];
        var importedContract = imported.Payload.GetProperty("contracts")[0];

        foreach (var member in ownerContract.EnumerateObject().Where(static property => property.Name != "artifactSource"))
        {
            Assert.Equal(member.Value.GetRawText(), importedContract.GetProperty(member.Name).GetRawText());
        }
        Assert.NotEqual(owner.PublicInventorySha256, imported.PublicInventorySha256);
        reexport["contracts"]![0]!["artifactSource"] = "owned";
        var error = Assert.Throws<InvalidDataException>(() =>
            SunderPackagePublicApiInventory.ParseVerifyHash(Serialize(reexport), imported.PublicInventorySha256));
        Assert.StartsWith("rpc-authoring.hash-mismatch:", error.Message, StringComparison.Ordinal);
        AssertRejected(reexport);
    }

    [Fact]
    public void Parse_EnforcesPlatformAuthorityAndImportedSource()
    {
        var platform = Read("platform-only");
        platform["contracts"]![0]!["artifactSource"] = "owned";
        AssertRejected(platform);
        platform = Read("platform-only");
        platform["contracts"]![0]!["authorityId"] = "example.platform";
        AssertRejected(platform);

        foreach (var member in new[] { "catalogSigningKeyId", "catalogTrustGeneration", "catalogTrustFingerprint" })
        {
            foreach (var kind in new[] { "nonempty", "platform-only" })
            {
                var inventory = Read(kind);
                inventory["contracts"]![0]![member] = "obsolete";
                AssertRejected(inventory);
            }
        }
    }

    [Theory]
    [InlineData("contractId", "Example.rpc")]
    [InlineData("contractId", "example..rpc")]
    [InlineData("contractId", "example-rpc")]
    [InlineData("contractId", "")]
    [InlineData("contractVersion", "latest")]
    [InlineData("contractVersion", ">=1.0.0")]
    [InlineData("contractVersion", "1.0")]
    [InlineData("contractVersion", "01.0.0")]
    [InlineData("contractVersion", "1.0.0-01")]
    [InlineData("contractVersion", "1.0.0\n")]
    [InlineData("descriptorSha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("abiSha256", "bad")]
    [InlineData("artifactSource", "reexport")]
    [InlineData("authorityKind", "publisher")]
    [InlineData("authorityId", "Example.owner")]
    [InlineData("selectedAuthorityRelease", "*")]
    [InlineData("selectedAuthorityContentIdentity", "")]
    public void Parse_RejectsNoncanonicalContractValues(string member, string value)
    {
        var root = Read();
        root["contracts"]![0]![member] = value;
        AssertRejected(root);
    }

    [Theory]
    [InlineData("packageId", "Example.owner")]
    [InlineData("packageId", "")]
    [InlineData("packageVersion", "latest")]
    [InlineData("packageVersion", "1.0.0-01")]
    [InlineData("packageContentIdentity", "bad")]
    public void Parse_RejectsNoncanonicalPackageValues(string member, string value)
    {
        var root = Read("empty");
        root[member] = value;
        AssertRejected(root);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4_194_304)]
    public void Parse_AcceptsDescriptorByteBoundariesWithoutClaimingToVerifyDescriptorBodies(int size)
    {
        var root = Read();
        root["contracts"]![0]!["descriptorBytes"] = size;
        Assert.Equal(size, Parse(root).Payload.GetProperty("contracts")[0].GetProperty("descriptorBytes").GetInt32());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("4194305")]
    [InlineData("2.0")]
    [InlineData("2e0")]
    [InlineData("-0")]
    [InlineData("02")]
    [InlineData("+2")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    public void Parse_RejectsInvalidDescriptorSizesAndNoncanonicalIntegerTokens(string token)
    {
        var json = Encoding.UTF8.GetString(Serialize(Read())).Replace("\"descriptorBytes\":487", $"\"descriptorBytes\":{token}", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Parse_EnforcesIdAndVersionBudgetsWithoutLimitingSemverNumericComponentsToMachineIntegers()
    {
        var root = Read("empty");
        root["packageId"] = new string('a', 128);
        root["packageVersion"] = new string('9', 252) + ".0.0";
        _ = Parse(root);
        root["packageVersion"] = new string('9', 253) + ".0.0";
        AssertRejected(root);
        root["packageVersion"] = "1.0.0";
        root["packageId"] = new string('a', 129);
        AssertRejected(root);

        root = Read("platform-only");
        root["contracts"]![0]!["contractId"] = new string('a', 128);
        root["contracts"]![0]!["contractVersion"] = "1.0.0-rc.1+".PadRight(256, 'a');
        _ = Parse(root);
        foreach (var member in new[] { "contractId", "contractVersion" })
        {
            var invalid = root.DeepClone().AsObject();
            invalid["contracts"]![0]![member] = new string('a', member == "contractVersion" ? 257 : 129);
            AssertRejected(invalid);
        }
    }

    [Fact]
    public void Parse_EnforcesCompleteCountAndCollectionBoundaries()
    {
        var root = Read();
        var template = root["contracts"]![0]!.DeepClone();
        var contracts = new JsonArray();
        for (var index = 0; index < 128; index++)
        {
            var contract = template.DeepClone();
            contract["contractId"] = $"example.contract{index:D3}";
            contracts.Add(contract);
        }
        root["contracts"] = contracts;
        root["publicContractCount"] = 128;
        Assert.Equal(128, Parse(root).Payload.GetProperty("contracts").GetArrayLength());
        root["publicContractCount"] = 127;
        AssertRejected(root);
        root["publicContractCount"] = 129;
        var extra = template.DeepClone();
        extra["contractId"] = "example.contract128";
        contracts.Add(extra);
        AssertRejected(root);
        root["publicContractCount"] = 128;
        AssertRejected(root);
    }

    [Fact]
    public void Parse_RejectsMalformedUtf8BomEscapesNonAsciiAndNonJsonInput()
    {
        var bytes = Serialize(Read());
        var json = Encoding.UTF8.GetString(bytes);
        foreach (var invalid in new[]
        {
            Array.Empty<byte>(),
            new byte[] { (byte)'{' },
            new byte[] { 0xff, 0xfe },
            new byte[] { 0xc0, 0xaf },
            new byte[] { 0xed, 0xa0, 0x80 },
            new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray(),
            Encoding.UTF8.GetBytes(json.Replace("example.dependency", "\\u0065xample.dependency", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace("example.dependency", "\\uD800", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace("example.dependency", "\\uDC00", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace("example.dependency", "ex\u00e9mple.dependency", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes("/*comment*/" + json),
            Encoding.UTF8.GetBytes(json + "{}"),
            Encoding.UTF8.GetBytes("null"),
            Encoding.UTF8.GetBytes("[]"),
        })
        {
            Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(invalid));
        }
        var corruptString = bytes.ToArray();
        corruptString[Array.IndexOf(corruptString, (byte)'e')] = 0xff;
        Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(corruptString));
    }

    [Fact]
    public void Parse_BoundsRawBytesIncludingWhitespaceAndNestingBeforeDomParsing()
    {
        var bytes = Serialize(Read("empty"));
        var padded = Enumerable.Repeat((byte)' ', SunderPackagePublicApiInventory.MaximumPayloadBytes).ToArray();
        bytes.CopyTo(padded, 0);
        Assert.Equal(Parse(Read("empty")).PublicInventorySha256, SunderPackagePublicApiInventory.Parse(padded).PublicInventorySha256);
        Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse([.. padded, (byte)' ']));

        var nested = Encoding.UTF8.GetBytes(new string('[', 17) + "0" + new string(']', 17));
        var error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(nested));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Parse_BoundsWideDocumentsBeforeDomParsing(string item)
    {
        var json = "{\"contracts\":[" + string.Join(',', Enumerable.Repeat(item, 100_000)) + "]}";
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.InRange(bytes.Length, 1, SunderPackagePublicApiInventory.MaximumPayloadBytes);

        var error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(bytes));
        Assert.Contains("structural value/property budget", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.ParseVerifyHash(bytes, new string('0', 64)));
        Assert.Contains("structural value/property budget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_StructuralBudgetCountsPropertyNamesAndIncludesTheRoot()
    {
        var properties = "{" + string.Join(',', Enumerable.Range(0, 2_048).Select(static index => $"\"p{index}\":0")) + "}";
        var error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(Encoding.UTF8.GetBytes(properties)));
        Assert.Contains("structural value/property budget", error.Message, StringComparison.Ordinal);

        var boundary = "[" + string.Join(',', Enumerable.Repeat("0", 4_095)) + "]";
        error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(Encoding.UTF8.GetBytes(boundary)));
        Assert.DoesNotContain("structural", error.Message, StringComparison.Ordinal);
        var overflow = "[" + string.Join(',', Enumerable.Repeat("0", 4_096)) + "]";
        error = Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.Parse(Encoding.UTF8.GetBytes(overflow)));
        Assert.Contains("structural value/property budget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_StructuralBudgetAdmitsTheLargestValidInventoryShape()
    {
        var root = Read("platform-only");
        var template = root["contracts"]![0]!;
        var contracts = new JsonArray();
        for (var index = 0; index < 128; index++)
        {
            var contract = template.DeepClone();
            contract["contractId"] = $"example.contract{index:D3}";
            contracts.Add(contract);
        }
        root["contracts"] = contracts;
        root["publicContractCount"] = 128;
        var bytes = Serialize(root);
        var reader = new Utf8JsonReader(bytes);
        var valuesAndProperties = 0;
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject)) valuesAndProperties++;
        }
        Assert.Equal(2_701, valuesAndProperties);
        Assert.Equal(128, SunderPackagePublicApiInventory.ParseVerifyHash(bytes, InventoryHash(bytes)).Payload.GetProperty("contracts").GetArrayLength());
    }

    [Fact]
    public void ParseVerifyHash_RejectsTamperingAndNoncanonicalExpectedDigests()
    {
        var root = Read();
        var expected = Parse(root).PublicInventorySha256;
        root["contracts"]![0]!["abiSha256"] = new string('a', 64);
        var bytes = Serialize(root);
        foreach (var hash in new[] { expected, expected.ToUpperInvariant(), new string('0', 64), "bad", "" })
        {
            Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.ParseVerifyHash(bytes, hash));
        }
        Assert.Throws<InvalidDataException>(() => SunderPackagePublicApiInventory.ParseVerifyHash(bytes, null!));
        root["publicContractCount"] = 0;
        AssertRejected(root);
    }

    [Fact]
    public void ParseVerifyHash_ReturnsAnImmutableDetachedSnapshotAndDefensiveByteCopies()
    {
        var root = Read("mixed-authority");
        var bytes = Serialize(root);
        var expectedBytes = bytes.ToArray();
        var expectedHash = InventoryHash(bytes);
        var inventory = SunderPackagePublicApiInventory.ParseVerifyHash(bytes, expectedHash);
        Array.Fill(bytes, (byte)'x');
        root["packageId"] = "changed";
        var output = inventory.ToCanonicalUtf8();
        Array.Fill(output, (byte)'x');

        Assert.Equal(expectedBytes, inventory.ToCanonicalUtf8());
        Assert.Equal(expectedHash, inventory.PublicInventorySha256);
        Assert.Equal("example.dependency", inventory.Payload.GetProperty("packageId").GetString());
        Assert.Equal(2, inventory.Payload.GetProperty("contracts").GetArrayLength());
        Assert.Equal(Encoding.UTF8.GetString(expectedBytes), inventory.Payload.GetRawText());
    }

    private static JsonObject Read(string name = "nonempty")
        => JsonNode.Parse(File.ReadAllBytes(Fixture($"valid/package-public-inventory-{name}.json")))!.AsObject();

    private static byte[] Serialize(JsonObject root) => JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
    private static SunderPackagePublicApiInventory Parse(JsonObject root) => SunderPackagePublicApiInventory.Parse(Serialize(root));
    private static void AssertRejected(JsonObject root) => Assert.Throws<InvalidDataException>(() => Parse(root));
    private static string Fixture(string path) => Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "conformance", path);
    private static string LowerHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string InventoryHash(byte[] canonical)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("sunder.package-public-api-inventory.v1\0"u8);
        hash.AppendData(canonical);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
