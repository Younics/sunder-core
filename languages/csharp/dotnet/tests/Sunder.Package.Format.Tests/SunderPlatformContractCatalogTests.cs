using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderPlatformContractCatalogTests
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void CreateAndVerify_RequireNoSigningMaterialAndRetainCompleteCanonicalInventory()
    {
        var pretty = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "conformance", "valid", "platform-catalog.json"));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(pretty));
        var compact = Encode(JsonNode.Parse(pretty)!);
        var catalog = SunderPlatformContractCatalog.Verify(compact);
        Assert.Equal("1.1.0", catalog.ReleaseVersion);
        Assert.Equal(Hash(compact), catalog.ContentIdentity);
        var contract = Assert.Single(catalog.Contracts);
        Assert.Equal("sunder.stack.contributor", contract.ContractId);
        Assert.Equal("2.0.0", contract.Version);
        Assert.Equal(compact, SunderPlatformContractCatalog.Create("1.1.0", catalog.Contracts));
        Assert.Equal(compact, catalog.GetCanonicalUtf8());
    }

    [Fact]
    public void Create_CurrentCoreStackBaselineRetainsItsIndependentDescriptorAndAbi()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory.FullName, "eng", "platform-contracts", "sunder-contracts.owned.json")));
        var snapshot = baseline.RootElement.GetProperty("contracts").GetProperty("stack-contributor-rpc-service-adapter").GetProperty("current");
        var bytes = Encoding.UTF8.GetBytes(snapshot.GetProperty("canonicalDescriptor").GetString()!);
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        Assert.Equal(snapshot.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
        Assert.Equal(snapshot.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
        Assert.Equal(6, Assert.Single(descriptor.Services).Methods.Count);
        var catalog = SunderPlatformContractCatalog.Verify(SunderPlatformContractCatalog.Create("1.1.0", [descriptor]));
        var actual = Assert.Single(catalog.Contracts);
        Assert.Equal(bytes, actual.GetCanonicalUtf8());
        Assert.Equal(descriptor.GetCanonicalAbiProjectionUtf8(), actual.GetCanonicalAbiProjectionUtf8());
    }

    [Theory]
    [InlineData("1.1.0-preview.1")]
    [InlineData("01.1.0")]
    [InlineData("1.1")]
    [InlineData("")]
    public void Create_RejectsNonStableOrInvalidRelease(string release)
        => Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create(release, [Descriptor()]));

    [Fact]
    public void Create_DescriptorVersionsAreIndependentAndOutputIsSorted()
    {
        var contracts = new[] { Descriptor("z.example", "3.0.0-preview.1"), Descriptor(), Descriptor("a.example", "0.1.0+local") };
        var bytes = SunderPlatformContractCatalog.Create("1.1.0+release.2", contracts);
        Assert.Equal(bytes, SunderPlatformContractCatalog.Create("1.1.0+release.2", contracts.Reverse().ToArray()));
        var catalog = SunderPlatformContractCatalog.Verify(bytes);
        Assert.Equal(new[] { "a.example", "sunder.stack.contributor", "z.example" }, catalog.Contracts.Select(contract => contract.ContractId));
        var json = JsonNode.Parse(bytes)!;
        var records = json["contracts"]!.AsArray(); var first = records[0]!;
        records.RemoveAt(0); records.Add(first);
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(json)));
    }

    [Fact]
    public void Create_EnforcesMandatoryContractUniqueIdsAndCountBoundaries()
    {
        var stack = Descriptor();
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create("1.1.0", []));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create("1.1.0", [null!]));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create("1.1.0", [Descriptor("example.other")]));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create("1.1.0", [stack, stack]));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create("1.1.0", [stack, Descriptor(version: "3.0.0")]));
        var maximum = Enumerable.Range(0, 127).Select(index => Descriptor($"example.c{index:000}")).Append(stack).ToArray();
        Assert.Equal(128, SunderPlatformContractCatalog.Verify(SunderPlatformContractCatalog.Create("1.1.0", maximum)).Contracts.Count);
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Create("1.1.0", maximum.Append(Descriptor("example.extra")).ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_RejectsMissingNullUnknownAndReorderedMembersAtEveryFixedShape(bool contract)
    {
        var original = Catalog();
        JsonObject Shape(JsonNode value) => contract ? value["contracts"]![0]!.AsObject() : value.AsObject();
        foreach (var member in Shape(original).Select(property => property.Key))
        {
            var missing = original.DeepClone(); Shape(missing).Remove(member);
            Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(missing)));
            var explicitNull = original.DeepClone(); Shape(explicitNull)[member] = null;
            Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(explicitNull)));
        }
        var unknown = original.DeepClone(); Shape(unknown)["signature"] = "obsolete";
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(unknown)));
        var reversed = Shape(original).Reverse().Select(pair => (pair.Key, Value: pair.Value!.DeepClone())).ToArray();
        Shape(original).Clear(); foreach (var (key, value) in reversed) Shape(original).Add(key, value);
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(original)));
    }

    [Theory]
    [InlineData("schemaVersion", "SchemaVersion", "1")]
    [InlineData("authorityKind", "AUTHORITYKIND", "\"platform\"")]
    [InlineData("contractId", "ContractId", "\"sunder.stack.contributor\"")]
    public void Verify_RejectsExactAndAsciiCaseFoldDuplicateMembers(string member, string foldedMember, string value)
    {
        var text = Encoding.UTF8.GetString(Encode(Catalog()));
        foreach (var duplicate in new[] { member, foldedMember })
            Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encoding.UTF8.GetBytes(
                text.Replace($"\"{member}\":", $"\"{duplicate}\":{value},\"{member}\":", StringComparison.Ordinal))));
    }

    [Fact]
    public void Verify_RejectsNonCanonicalBytesInvalidUtf8ScalarsNumbersDepthAndSize()
    {
        var bytes = Encode(Catalog()); var text = Encoding.UTF8.GetString(bytes);
        foreach (var invalid in new[] { " " + text, text + "\n", text.Replace("sunder.core", "\\u0073under.core", StringComparison.Ordinal),
            text.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1.0,", StringComparison.Ordinal),
            text.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1e0,", StringComparison.Ordinal),
            text.Replace("\"contractCount\":1,", "\"contractCount\":-0,", StringComparison.Ordinal),
            text.Replace("sunder.core", "\\ud800", StringComparison.Ordinal), text.Replace("sunder.core", "\\udc00", StringComparison.Ordinal),
            "null", "[]", "{}", text[..^1] + ",}", "/*comment*/" + text, new string('[', 17) + "0" + new string(']', 17) })
            Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify([0xef, 0xbb, 0xbf, .. bytes]));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify([.. bytes, 0xff]));
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(new byte[64 * 1024 * 1024 + 1]));
    }

    [Theory]
    [InlineData("authorityKind", "\"package\"")]
    [InlineData("authorityId", "\"example.other\"")]
    [InlineData("releaseVersion", "\"1.1.0-preview.1\"")]
    [InlineData("contractCount", "2")]
    [InlineData("contracts", "[]")]
    [InlineData("contractId", "\"example.other\"")]
    [InlineData("contractVersion", "\"3.0.0\"")]
    [InlineData("descriptorBytes", "490")]
    [InlineData("descriptorBytes", "4194305")]
    [InlineData("descriptorSha256", "\"0000000000000000000000000000000000000000000000000000000000000000\"")]
    [InlineData("abiSha256", "\"0000000000000000000000000000000000000000000000000000000000000000\"")]
    [InlineData("canonicalDescriptor", "\"{}\"")]
    public void Verify_RejectsMalformedCatalogAndDescriptorIdentity(string member, string value)
    {
        var catalog = Catalog();
        var record = catalog.ContainsKey(member) ? catalog : catalog["contracts"]![0]!.AsObject();
        record[member] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(catalog)));
    }

    [Fact]
    public void CreateAndVerify_PreserveJcsEscapingAndCountUtf8RatherThanCharacters()
    {
        var source = JsonNode.Parse(Descriptor().GetCanonicalUtf8())!;
        source["$defs"]!["Empty"]!["title"] = "<>&/\u00e9\u2028\u2029\ud83d\ude00";
        var descriptor = SunderRpcContractDescriptor.Parse(Encode(source));
        var bytes = SunderPlatformContractCatalog.Create("1.1.0", [descriptor]);
        Assert.Equal(descriptor.GetCanonicalUtf8(), Assert.Single(SunderPlatformContractCatalog.Verify(bytes).Contracts).GetCanonicalUtf8());
        Assert.Contains("<>&/\u00e9\u2028\u2029\ud83d\ude00", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var catalog = JsonNode.Parse(bytes)!; var record = catalog["contracts"]![0]!;
        record["descriptorBytes"] = record["canonicalDescriptor"]!.GetValue<string>().Length;
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.Verify(Encode(catalog)));
    }

    [Fact]
    public void VerifiedContent_DefensivelyCopiesAndChecksEveryExactPin()
    {
        var bytes = Encode(Catalog()); var original = bytes.ToArray();
        var catalog = SunderPlatformContractCatalog.Verify(bytes);
        Array.Fill(bytes, (byte)0); var copy = catalog.GetCanonicalUtf8(); Array.Fill(copy, (byte)0);
        Assert.Equal(original, catalog.GetCanonicalUtf8()); Assert.Equal(Hash(original), catalog.ContentIdentity);
        var descriptor = Assert.Single(catalog.Contracts);
        var list = Assert.IsAssignableFrom<IList<SunderRpcContractDescriptor>>(catalog.Contracts);
        Assert.True(list.IsReadOnly); Assert.Throws<NotSupportedException>(() => list[0] = Descriptor("example.other"));
        Assert.Same(descriptor, catalog.VerifyPinned(catalog.ReleaseVersion, catalog.ContentIdentity, descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256));
        for (var field = 0; field < 6; field++)
            Assert.Throws<InvalidDataException>(() => catalog.VerifyPinned(
                field == 0 ? "1.1.1" : catalog.ReleaseVersion, field == 1 ? new string('0', 64) : catalog.ContentIdentity,
                field == 2 ? "example.other" : descriptor.ContractId, field == 3 ? "3.0.0" : descriptor.Version,
                field == 4 ? new string('0', 64) : descriptor.Sha256, field == 5 ? new string('0', 64) : descriptor.AbiSha256));
        SunderPlatformContractCatalog.ValidateCacheOwnership(original, catalog.ContentIdentity);
        Assert.Throws<InvalidDataException>(() => SunderPlatformContractCatalog.ValidateCacheOwnership(original, new string('0', 64)));
    }

    private static JsonObject Catalog() => JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "RpcV1Authoring", "conformance", "valid", "platform-catalog.json")))!.AsObject();
    private static SunderRpcContractDescriptor Descriptor(string id = "sunder.stack.contributor", string version = "2.0.0")
    {
        var source = JsonNode.Parse(Catalog()["contracts"]![0]!["canonicalDescriptor"]!.GetValue<string>())!;
        source["contractId"] = id; source["version"] = version;
        return SunderRpcContractDescriptor.Parse(Encode(source));
    }
    private static byte[] Encode(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString(Json));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
