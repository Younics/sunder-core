using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderRpcPlatformBaselineTests
{
    private const string Authority = "sunder.core";
    private const string ContractId = "sunder.stack.contributor";
    private const string Key = "stack-contributor";
    private const string Release = "9.8.7";
    private static readonly Lazy<SunderRpcCSharpCompiledContract> Original = new(() => Compile(Source));
    private static readonly Lazy<SunderRpcCSharpCompiledContract> Added = new(() => Compile(Source.Replace(
        "[SunderMethod]", "[SunderMethod]\n    public ValueTask<Response> SearchAsync(Request request) => throw new System.NotImplementedException();\n    [SunderMethod]", StringComparison.Ordinal)));
    private static readonly Lazy<SunderRpcCSharpCompiledContract> Breaking = new(() => Compile(Source.Replace("ReadAsync", "FetchAsync", StringComparison.Ordinal)));
    private static readonly Lazy<SunderRpcCSharpCompiledContract> Other = new(() => Compile(Source
        .Replace("StackContributorService", "OtherService", StringComparison.Ordinal).Replace(ContractId, "sunder.core.other", StringComparison.Ordinal)));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void ReadWrite_PlatformFixtureRoundTripsExactBytesHashAndReadOnlySnapshots()
    {
        var path = Fixture("conformance/valid/platform-owned-baseline.json");
        var bytes = File.ReadAllBytes(path);
        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPlatformBaseline(path);
        Assert.Equal(bytes, Encode(JsonNode.Parse(bytes)!));
        Assert.Equal(bytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
        Assert.Equal(bytes, baseline.ExactBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), baseline.Sha256);
        var identity = Assert.Single(SunderRpcAuthoringDocuments.CompilerIdentities(baseline));
        Assert.Equal("stackContributor", identity.Key);
        Assert.Equal(ContractId, identity.Value.ContractId);
        Assert.Equal("2.0.0", identity.Value.ContractVersion);
        var map = Assert.IsAssignableFrom<IDictionary<string, SunderRpcOwnedContractBaseline>>(baseline.Contracts);
        Assert.True(map.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => map.Clear());
        var history = Assert.IsAssignableFrom<IList<SunderRpcOwnedContractSnapshot>>(Assert.Single(map).Value.History);
        Assert.True(history.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => history.Clear());
        var parsed = Parse(bytes);
        bytes[0] = (byte)'!';
        Assert.Equal((byte)'{', parsed.ExactBytes[0]);
        AssertSchema(baseline.ExactBytes);
    }

    [Fact]
    public void ReadWrite_PreservesHistoricalCatalogAndPublicationEvidenceWithoutClaimingApproval()
    {
        var published = Snapshot(Original.Value, "published", "7.0.0");
        var frozen = Snapshot(AtVersion(Added.Value, "1.1.0"), "release-frozen", "8.0.0");
        var baseline = Baseline(Snapshot(AtVersion(Added.Value, "1.2.0")), published, frozen);
        var reread = Parse(SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
        Assert.Equal(baseline.ExactBytes, reread.ExactBytes);
        AssertSnapshot(published, reread.Contracts[Key].History[0]);
        AssertSnapshot(frozen, reread.Contracts[Key].History[1]);
        var publication = reread.Contracts[Key].History[0].SnapshotJson.GetProperty("publication");
        Assert.Equal(new[] { "registryOrigin", "authorityId", "catalogRelease", "catalogContentIdentity", "publishedAtUtc", "etag" }, publication.EnumerateObject().Select(property => property.Name));
        Assert.Equal("https://registry.example/", publication.GetProperty("registryOrigin").GetString());
        Assert.Equal(Authority, publication.GetProperty("authorityId").GetString());
        Assert.Equal("7.0.0", publication.GetProperty("catalogRelease").GetString());
        Assert.Equal(new string('a', 64), publication.GetProperty("catalogContentIdentity").GetString());
        Assert.Equal("2026-09-11T12:34:56.123456789Z", publication.GetProperty("publishedAtUtc").GetString());
        Assert.Equal("\"" + published.Descriptor.Sha256 + "\"", publication.GetProperty("etag").GetString());
        AssertSchema(reread.ExactBytes);
    }

    [Theory]
    [InlineData("https://localhost/")]
    [InlineData("https://localhost:5443/")]
    public void ReadWrite_PackageAndPlatformPublishedEvidenceRetainsHttpsLocalhost(string origin)
    {
        var packageRoot = JsonNode.Parse(File.ReadAllBytes(Fixture("conformance/valid/package-owned-baseline.json")))!;
        var packageId = packageRoot["packageId"]!.GetValue<string>();
        var packageCurrent = packageRoot["contracts"]!["minimal"]!["current"]!;
        packageCurrent["state"] = "published";
        packageCurrent["packageRelease"] = new JsonObject { ["packageId"] = packageId, ["packageVersion"] = Release };
        packageCurrent["publication"] = new JsonObject
        {
            ["registryOrigin"] = origin, ["packageId"] = packageId, ["packageVersion"] = Release,
            ["packageContentIdentity"] = new string('a', 64), ["publishedAtUtc"] = "2026-09-11T12:34:56Z",
            ["etag"] = "\"" + packageCurrent["descriptorSha256"]!.GetValue<string>() + "\"",
        };
        var packageBytes = Encode(packageRoot);
        var package = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(packageBytes);
        Assert.Equal(packageBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(package));
        Assert.Equal(origin, package.Contracts["minimal"].Current!.SnapshotJson.GetProperty("publication").GetProperty("registryOrigin").GetString());
        AssertSchema(packageBytes);

        var platformRoot = JsonNode.Parse(Baseline(Snapshot(Original.Value, "published")).ExactBytes)!;
        platformRoot["contracts"]![Key]!["current"]!["publication"]!["registryOrigin"] = origin;
        var platformBytes = Encode(platformRoot);
        var platform = Parse(platformBytes);
        Assert.Equal(platformBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(platform));
        Assert.Equal(origin, platform.Contracts[Key].Current!.SnapshotJson.GetProperty("publication").GetProperty("registryOrigin").GetString());
        AssertSchema(platformBytes);
    }

    [Theory]
    [InlineData("missing-schema")]
    [InlineData("missing-authority")]
    [InlineData("missing-contracts")]
    [InlineData("schema-version")]
    [InlineData("package-authority")]
    [InlineData("unknown-authority")]
    [InlineData("other-owner")]
    [InlineData("package-id")]
    [InlineData("null-package-id")]
    [InlineData("unknown-member")]
    [InlineData("null-contracts")]
    [InlineData("root-order")]
    [InlineData("array-root")]
    public void Read_RejectsInvalidAuthorityAndClosedRoot(string mutation)
    {
        var root = FixtureRoot();
        switch (mutation)
        {
            case "missing-schema": root.Remove("schemaVersion"); break;
            case "missing-authority": root.Remove("authorityKind"); break;
            case "missing-contracts": root.Remove("contracts"); break;
            case "schema-version": root["schemaVersion"] = 2; break;
            case "package-authority": root["authorityKind"] = "package"; break;
            case "unknown-authority": root["authorityKind"] = "core"; break;
            case "other-owner": root["authorityId"] = "example.other"; break;
            case "package-id": root["packageId"] = Authority; break;
            case "null-package-id": root["packageId"] = null; break;
            case "unknown-member": root["approved"] = true; break;
            case "null-contracts": root["contracts"] = null; break;
            case "root-order": root.Remove("schemaVersion"); root["schemaVersion"] = 1; break;
            case "array-root": Assert.Throws<InvalidDataException>(() => Parse("[]\n"u8.ToArray())); return;
        }
        Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
    }

    [Theory]
    [InlineData("no-lf")]
    [InlineData("extra-lf")]
    [InlineData("crlf")]
    [InlineData("bom")]
    [InlineData("compact")]
    [InlineData("trailing-space")]
    [InlineData("duplicate")]
    [InlineData("case-collision")]
    [InlineData("exponent")]
    [InlineData("negative-zero")]
    [InlineData("invalid-utf8")]
    [InlineData("unpaired-surrogate")]
    [InlineData("depth")]
    [InlineData("structural-budget")]
    public void Read_RejectsNonCanonicalBytesAndPreflightsStructuralLimits(string mutation)
    {
        var text = File.ReadAllText(Fixture("conformance/valid/platform-owned-baseline.json"));
        var mutated = mutation switch
        {
            "no-lf" => text[..^1],
            "extra-lf" => text + "\n",
            "crlf" => text.Replace("\n", "\r\n", StringComparison.Ordinal),
            "bom" => "\ufeff" + text,
            "compact" => FixtureRoot().ToJsonString() + "\n",
            "trailing-space" => text.Replace("\n", " \n", StringComparison.Ordinal),
            "duplicate" => text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal),
            "case-collision" => text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"SchemaVersion\": 1,", StringComparison.Ordinal),
            "exponent" => text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1e0,", StringComparison.Ordinal),
            "negative-zero" => text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": -0,", StringComparison.Ordinal),
            "unpaired-surrogate" => text.Replace("\"authorityId\": \"sunder.core\"", "\"authorityId\": \"\\ud800\"", StringComparison.Ordinal),
            "depth" => new string('[', 17) + new string(']', 17) + "\n",
            "structural-budget" => "[" + string.Join(',', Enumerable.Repeat("true", 262_144)) + "]\n",
            _ => text,
        };
        var bytes = Encoding.UTF8.GetBytes(mutated);
        if (mutation == "invalid-utf8") bytes[0] = 0xff;
        var error = Assert.Throws<InvalidDataException>(() => Parse(bytes));
        if (mutation == "structural-budget") Assert.Contains("structural", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsOversizedFileBeforeParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-platform-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(268_435_457);
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadOwnedPlatformBaseline(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("conformance/invalid/platform-baseline-invalid-exposure-history.json")]
    [InlineData("conformance/semantic-invalid/platform-baseline-missing-mandatory-contract.json")]
    public void Read_RejectsAuthoritativeInvalidPlatformFixtures(string relativePath)
        => Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadOwnedPlatformBaseline(Fixture(relativePath)));

    [Theory]
    [InlineData("exposure")]
    [InlineData("state")]
    [InlineData("version")]
    [InlineData("descriptor-count")]
    [InlineData("descriptor-limit")]
    [InlineData("descriptor-text")]
    [InlineData("descriptor-invalid")]
    [InlineData("descriptor-id")]
    [InlineData("descriptor-version")]
    [InlineData("descriptor-hash")]
    [InlineData("descriptor-hash-case")]
    [InlineData("abi-count")]
    [InlineData("abi-limit")]
    [InlineData("abi-text")]
    [InlineData("abi-hash")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("order")]
    public void Read_ValidatesEveryCurrentAndHistoricalCanonicalSnapshot(string mutation)
    {
        foreach (var historical in new[] { false, true })
        {
            var issued = Snapshot(Original.Value, "published");
            var baseline = historical ? Baseline(Snapshot(AtVersion(Original.Value, "1.1.0")), issued) : Baseline(issued);
            var root = JsonNode.Parse(baseline.ExactBytes)!;
            var snapshot = historical ? root["contracts"]![Key]!["history"]![0]! : root["contracts"]![Key]!["current"]!;
            switch (mutation)
            {
                case "exposure": snapshot["exposure"] = "package"; break;
                case "state": snapshot["state"] = "approved"; break;
                case "version": snapshot["contractVersion"] = "01.0.0"; break;
                case "descriptor-count": snapshot["descriptorBytes"] = issued.Descriptor.GetCanonicalUtf8().Length + 1; break;
                case "descriptor-limit": snapshot["descriptorBytes"] = 4_194_305; break;
                case "descriptor-text": snapshot["canonicalDescriptor"] = " " + snapshot["canonicalDescriptor"]!.GetValue<string>(); break;
                case "descriptor-invalid": snapshot["canonicalDescriptor"] = "{}"; snapshot["descriptorBytes"] = 2; break;
                case "descriptor-id": snapshot["canonicalDescriptor"] = snapshot["canonicalDescriptor"]!.GetValue<string>().Replace(ContractId, "sunder.stack.contributox", StringComparison.Ordinal); break;
                case "descriptor-version": snapshot["contractVersion"] = "1.0.1"; break;
                case "descriptor-hash": snapshot["descriptorSha256"] = new string('0', 64); break;
                case "descriptor-hash-case": snapshot["descriptorSha256"] = new string('A', 64); break;
                case "abi-count": snapshot["abiProjectionBytes"] = issued.Descriptor.GetCanonicalAbiProjectionUtf8().Length + 1; break;
                case "abi-limit": snapshot["abiProjectionBytes"] = 4_194_305; break;
                case "abi-text": snapshot["canonicalAbiProjection"] = "{}"; snapshot["abiProjectionBytes"] = 2; break;
                case "abi-hash": snapshot["abiSha256"] = new string('0', 64); break;
                case "unknown": snapshot["approved"] = true; break;
                case "missing": snapshot.AsObject().Remove("abiSha256"); break;
                case "null": snapshot["abiSha256"] = null; break;
                case "order": snapshot.AsObject().Remove("state"); snapshot["state"] = "published"; break;
            }
            Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
        }
    }

    [Theory]
    [InlineData("draft", true, false)]
    [InlineData("draft", false, true)]
    [InlineData("release-frozen", false, false)]
    [InlineData("release-frozen", true, true)]
    [InlineData("published", false, true)]
    [InlineData("published", true, false)]
    public void Read_RequiresExactlyTheEvidenceAllowedByState(string state, bool hasRelease, bool hasPublication)
    {
        var root = JsonNode.Parse(Baseline(Snapshot(Original.Value, "published")).ExactBytes)!;
        var current = root["contracts"]![Key]!["current"]!;
        current["state"] = state;
        if (!hasRelease) current.AsObject().Remove("catalogRelease");
        if (!hasPublication) current.AsObject().Remove("publication");
        Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
    }

    [Theory]
    [InlineData("authorityId", "example.other")]
    [InlineData("releaseVersion", "latest")]
    [InlineData("releaseVersion", "01.0.0")]
    [InlineData("unexpected", "value")]
    public void Read_ValidatesCatalogReleaseEvidence(string member, string value)
    {
        var root = JsonNode.Parse(Baseline(Snapshot(Original.Value, "release-frozen")).ExactBytes)!;
        root["contracts"]![Key]!["current"]!["catalogRelease"]![member] = value;
        Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
    }

    [Theory]
    [InlineData("authorityId", "example.other")]
    [InlineData("catalogRelease", "9.8.8")]
    [InlineData("catalogRelease", "9.8.7+different")]
    [InlineData("catalogContentIdentity", "bad")]
    [InlineData("publishedAtUtc", "2026-02-30T12:00:00Z")]
    [InlineData("publishedAtUtc", "2026-09-11T12:00:00.10Z")]
    [InlineData("publishedAtUtc", "2026-09-11T12:00:00+00:00")]
    [InlineData("etag", "W/\"bad\"")]
    [InlineData("registryOrigin", "https://Registry.example/")]
    [InlineData("registryOrigin", "https://registry.example:443/")]
    [InlineData("registryOrigin", "https://registry.example:0444/")]
    [InlineData("registryOrigin", "https://registry.example/path")]
    [InlineData("registryOrigin", "https://user@registry.example/")]
    [InlineData("registryOrigin", "https://127.0.0.1/")]
    [InlineData("registryOrigin", "https://localhost:443/")]
    [InlineData("registryOrigin", "https://localhost:05443/")]
    [InlineData("registryOrigin", "https://localhost:65536/")]
    [InlineData("registryOrigin", "https://user@localhost:5443/")]
    [InlineData("registryOrigin", "http://localhost.example/")]
    [InlineData("registryOrigin", "http://127.0.0.1/")]
    [InlineData("packageId", "example.package")]
    [InlineData("catalogContentIdentity", null)]
    public void Read_ValidatesEveryHistoricalAndCurrentPublicationField(string member, object? value)
    {
        foreach (var historical in new[] { false, true })
        {
            var issued = Snapshot(Original.Value, "published");
            var baseline = historical ? Baseline(Snapshot(AtVersion(Original.Value, "1.1.0")), issued) : Baseline(issued);
            var root = JsonNode.Parse(baseline.ExactBytes)!;
            var snapshot = historical ? root["contracts"]![Key]!["history"]![0]! : root["contracts"]![Key]!["current"]!;
            snapshot["publication"]![member] = JsonSerializer.SerializeToNode(value);
            Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
        }
    }

    [Fact]
    public void Read_RejectsMissingNullAndReorderedEvidenceMembers()
    {
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, "1.1.0")), Snapshot(Original.Value, "published"));
        foreach (var evidenceName in new[] { "catalogRelease", "publication" })
        {
            var original = JsonNode.Parse(baseline.ExactBytes)!;
            var evidence = original["contracts"]![Key]!["history"]![0]![evidenceName]!.AsObject();
            foreach (var member in evidence.Select(pair => pair.Key).ToArray())
            {
                foreach (var useNull in new[] { false, true })
                {
                    var root = original.DeepClone();
                    var mutated = root["contracts"]![Key]!["history"]![0]![evidenceName]!.AsObject();
                    if (useNull) mutated[member] = null;
                    else mutated.Remove(member);
                    Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
                }
            }
            var first = evidence.First();
            evidence.Remove(first.Key);
            evidence[first.Key] = first.Value;
            Assert.Throws<InvalidDataException>(() => Parse(Encode(original)));
        }
    }

    [Fact]
    public void Read_AcceptsInclusiveOriginPortAndRetainedPrereleaseCatalogEvidence()
    {
        var root = JsonNode.Parse(Baseline(Snapshot(Original.Value, "published", "7.0.0-preview.1")).ExactBytes)!;
        var publication = root["contracts"]![Key]!["current"]!["publication"]!;
        publication["registryOrigin"] = "https://registry.example:65535/";
        var bytes = Encode(root);
        Assert.Equal(bytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(Parse(bytes)));
        AssertSchema(bytes);
        publication["registryOrigin"] = "https://registry.example:65536/";
        Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
    }

    [Theory]
    [InlineData("catalogContentIdentity")]
    [InlineData("publishedAtUtc")]
    public void Read_RejectsConflictingEvidenceForTheSamePublicationEvent(string member)
    {
        var baseline = WithOther(Baseline(Snapshot(Original.Value, "published")), Snapshot(Other.Value, "published"));
        var root = JsonNode.Parse(baseline.ExactBytes)!;
        var publication = root["contracts"]![Other.Value.ContractKey]!["current"]!["publication"]!;
        publication[member] = member switch
        {
            "publishedAtUtc" => JsonValue.Create("2026-09-12T12:00:00Z"),
            _ => JsonValue.Create(new string('c', 64)),
        };
        var error = Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
        Assert.Contains("conflicting evidence", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("draft-history")]
    [InlineData("null-current")]
    [InlineData("null-history")]
    [InlineData("empty-removed")]
    [InlineData("duplicate-version")]
    [InlineData("equal-precedence")]
    [InlineData("descending-history")]
    [InlineData("current-before-history")]
    [InlineData("package-evidence")]
    public void Read_RejectsInvalidHistoryAndPackageEvidence(string mutation)
    {
        var first = Snapshot(Original.Value, "published", "7.0.0");
        var second = Snapshot(AtVersion(Original.Value, "1.1.0"), "release-frozen", "8.0.0");
        var root = JsonNode.Parse(Baseline(Snapshot(AtVersion(Original.Value, "1.2.0")), first, second).ExactBytes)!;
        var contract = root["contracts"]![Key]!;
        var history = contract["history"]!.AsArray();
        switch (mutation)
        {
            case "draft-history": history[0] = JsonNode.Parse(Snapshot(Original.Value).SnapshotJson.GetRawText()); break;
            case "null-current": contract["current"] = null; break;
            case "null-history": contract["history"] = null; break;
            case "empty-removed": contract.AsObject().Remove("current"); history.Clear(); break;
            case "duplicate-version": history[1] = history[0]!.DeepClone(); break;
            case "equal-precedence": history[1] = JsonNode.Parse(Snapshot(AtVersion(Original.Value, "1.0.0+other"), "published").SnapshotJson.GetRawText()); break;
            case "descending-history": var last = history[1]!.DeepClone(); history[1] = history[0]!.DeepClone(); history[0] = last; break;
            case "current-before-history": contract["current"] = history[0]!.DeepClone(); break;
            case "package-evidence": history[0]!["packageRelease"] = new JsonObject { ["packageId"] = Authority, ["packageVersion"] = Release }; break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
    }

    [Fact]
    public void ReadWrite_EnforcesInclusiveContractAndHistoryLimits()
    {
        var baseline = Baseline(Snapshot(Original.Value));
        var contracts = baseline.Contracts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        for (var index = 0; index < 127; index++)
        {
            var candidate = Change(Other.Value, root => root["contractId"] = "sunder.core.c" + index);
            contracts["c" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)] = new(candidate.Descriptor.ContractId, Snapshot(candidate), []);
        }
        var maximum = Parse(SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline with { Contracts = contracts }));
        Assert.Equal(128, maximum.Contracts.Count);
        var extra = Change(Other.Value, root => root["contractId"] = "sunder.core.extra");
        contracts["extra"] = new(extra.Descriptor.ContractId, Snapshot(extra), []);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline with { Contracts = contracts }));
        var history = Enumerable.Range(0, 256).Select(index => Snapshot(AtVersion(Original.Value, "1.0." + index), "release-frozen")).ToArray();
        var fullHistory = Baseline(Snapshot(AtVersion(Original.Value, "1.1.0")), history);
        Assert.Equal(256, fullHistory.Contracts[Key].History.Count);
        Assert.Throws<InvalidDataException>(() => Baseline(Snapshot(AtVersion(Original.Value, "1.1.0")),
            history.Append(Snapshot(AtVersion(Original.Value, "1.0.256"), "release-frozen")).ToArray()));
        var issued = Baseline(Snapshot(AtVersion(Original.Value, "1.1.0"), "release-frozen"), history);
        Assert.Throws<InvalidDataException>(() => Accept(issued, [AtVersion(Added.Value, "1.1.0")]));
        Assert.Equal(issued.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(issued));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("other-only")]
    [InlineData("stack-history-only")]
    [InlineData("duplicate-id")]
    [InlineData("case-key")]
    [InlineData("invalid-key")]
    [InlineData("key-order")]
    [InlineData("contract-order")]
    public void Read_RequiresMandatoryCurrentStackAndUniqueOrderedKeysAndIdentities(string mutation)
    {
        var root = JsonNode.Parse(WithOther(Baseline(Snapshot(Original.Value, "release-frozen")), Snapshot(Other.Value)).ExactBytes)!;
        var map = root["contracts"]!.AsObject();
        var contract = map[Key]!;
        switch (mutation)
        {
            case "empty": map.Clear(); break;
            case "other-only": map.Remove(Key); break;
            case "stack-history-only": contract["history"]!.AsArray().Add(contract["current"]!.DeepClone()); contract.AsObject().Remove("current"); break;
            case "duplicate-id": map["zduplicate"] = contract.DeepClone(); break;
            case "case-key": map["STACK-CONTRIBUTOR"] = contract.DeepClone(); break;
            case "invalid-key": map["invalid/"] = contract.DeepClone(); break;
            case "key-order": var other = map[Other.Value.ContractKey]!.DeepClone(); map.Remove(Other.Value.ContractKey); map[Other.Value.ContractKey] = other; break;
            case "contract-order": contract.AsObject().Remove("contractId"); contract["contractId"] = ContractId; break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(Encode(root)));
    }

    [Fact]
    public void Write_SortsKeysButRejectsModelJsonDisagreementAndNeverRepairsHistoryOrEvidence()
    {
        var baseline = Baseline(Snapshot(Original.Value, "published"));
        var other = new SunderRpcOwnedContractBaseline(Other.Value.Descriptor.ContractId, Snapshot(Other.Value), []);
        var unsorted = baseline with { Contracts = new Dictionary<string, SunderRpcOwnedContractBaseline>
        {
            [Key] = baseline.Contracts[Key], [Other.Value.ContractKey] = other,
        } };
        Assert.Equal(new[] { Other.Value.ContractKey, Key }, Parse(SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(unsorted)).Contracts.Keys);
        var current = baseline.Contracts[Key].Current!;
        foreach (var invalid in new[] { current with { State = "draft" }, current with { Exposure = "package" }, current with { Descriptor = Added.Value.Descriptor } })
        {
            var malformed = baseline with { Contracts = new Dictionary<string, SunderRpcOwnedContractBaseline> { [Key] = new(ContractId, invalid, []) } };
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(malformed));
            Assert.Throws<InvalidDataException>(() => Accept(malformed, [Original.Value]));
            Assert.Throws<InvalidDataException>(() => Freeze(malformed, [Original.Value]));
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateCompiledContracts(malformed, [Original.Value]));
        }
        var json = JsonNode.Parse(current.SnapshotJson.GetRawText())!;
        json["publication"]!["etag"] = "invalid";
        Assert.Throws<InvalidDataException>(() => Baseline(current with { SnapshotJson = JsonSerializer.SerializeToElement(json) }));
        Assert.Throws<InvalidDataException>(() => Baseline(Snapshot(AtVersion(Original.Value, "2.0.0")),
            Snapshot(AtVersion(Original.Value, "1.1.0"), "release-frozen"), current));
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
    }

    [Fact]
    public void Check_UsesSharedClassificationAndCoreOnlyGuidanceWithoutMutation()
    {
        var baseline = Baseline(Snapshot(Original.Value, "release-frozen"));
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [Original.Value]);
        var error = Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [Added.Value]));
        Assert.Contains("rpc-authoring.baseline-drift", error.Message, StringComparison.Ordinal);
        Assert.Contains("classified Minor", error.Message, StringComparison.Ordinal);
        Assert.Contains("minimum next version '1.1.0'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Core platform", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet msbuild", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canonicalDescriptor", error.Message, StringComparison.Ordinal);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
    }

    [Fact]
    public void Accept_FirstBaselineDefaultsToOneUnlessAnExactKeyMigrationVersionIsReviewed()
    {
        var baseline = Parse(Accept(null, [AtVersion(Original.Value, "7.0.0")]));
        Assert.Equal("1.0.0", baseline.Contracts[Key].Current!.Descriptor.Version);
        AssertDraft(baseline.Contracts[Key].Current!);
        Assert.Empty(baseline.Contracts[Key].History);
        var reviewed = Parse(Accept(null, [Original.Value], Key, next: "2.0.0"));
        Assert.Equal("2.0.0", reviewed.Contracts[Key].Current!.Descriptor.Version);
        Assert.Empty(reviewed.Contracts[Key].History);
        var compiled = Compile(Source, reviewed);
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(reviewed, [compiled]);
        Assert.Equal(reviewed.ExactBytes, Accept(reviewed, [compiled]));
        Assert.Throws<InvalidDataException>(() => Accept(null, [Original.Value], next: "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(null, [Original.Value], Key, "1.0.0", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(null, []));
        Assert.Throws<InvalidDataException>(() => Accept(new(new Dictionary<string, SunderRpcOwnedContractBaseline>(), [], string.Empty), [Original.Value]));
        AssertSchema(reviewed.ExactBytes);
    }

    [Fact]
    public void Accept_AdditionAndCompatibleBatchAreDeterministicAndDoNotInventIssuedEvidence()
    {
        var baseline = Baseline(Snapshot(Original.Value, "published"));
        var other = AtVersion(Other.Value, "8.0.0");
        var bytes = Accept(baseline, [Added.Value, other]);
        Assert.Equal(bytes, Accept(baseline, [other, Added.Value]));
        var accepted = Parse(bytes);
        Assert.Equal("1.1.0", accepted.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal("1.0.0", accepted.Contracts[Other.Value.ContractKey].Current!.Descriptor.Version);
        AssertSnapshot(baseline.Contracts[Key].Current!, Assert.Single(accepted.Contracts[Key].History));
        Assert.All(accepted.Contracts.Values, contract => AssertDraft(contract.Current!));
        Assert.Empty(accepted.Contracts[Other.Value.ContractKey].History);
        var migration = Parse(Accept(baseline, [Original.Value, other], other.ContractKey, next: "3.0.0"));
        Assert.Equal("3.0.0", migration.Contracts[other.ContractKey].Current!.Descriptor.Version);
        AssertSnapshot(baseline.Contracts[Key].Current!, migration.Contracts[Key].Current!);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Added.Value, other], other.ContractKey, next: "3.0.0"));
        AssertSchema(bytes);
    }

    [Theory]
    [InlineData("draft", "1.0.0")]
    [InlineData("release-frozen", "1.0.0")]
    [InlineData("published", "1.0.0")]
    [InlineData("draft", "1.0.0-preview.1")]
    [InlineData("release-frozen", "1.0.0-preview.1")]
    [InlineData("published", "1.0.0-preview.1")]
    public void Accept_ExactReuseRetainsVersionStateAndAllEvidence(string state, string version)
    {
        var candidate = AtVersion(Original.Value, version);
        var baseline = Baseline(Snapshot(candidate, state));
        Assert.Equal(baseline.ExactBytes, Accept(baseline, [candidate]));
    }

    [Theory]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void Accept_CompatibleIssuedChangesAllocateMinorAndPatchUsingTheSharedHistoryGate(string state)
    {
        var baseline = Baseline(Snapshot(Original.Value, state));
        var minor = Parse(Accept(baseline, [Added.Value]));
        Assert.Equal("1.1.0", minor.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshot(baseline.Contracts[Key].Current!, Assert.Single(minor.Contracts[Key].History));
        Assert.Equal(minor.ExactBytes, Accept(baseline, [Added.Value], Key, "1.0.0", "1.1.0"));
        var annotated = Change(Original.Value, root => root["$defs"]!.AsObject().First().Value!["description"] = "Reviewed \u03bb <>& / annotation");
        Assert.Equal(Original.Value.Descriptor.GetCanonicalAbiProjectionUtf8(), annotated.Descriptor.GetCanonicalAbiProjectionUtf8());
        var patch = Parse(Accept(baseline, [annotated]));
        Assert.Equal("1.0.1", patch.Contracts[Key].Current!.Descriptor.Version);
        Assert.Contains("\u03bb <>& /", Encoding.UTF8.GetString(patch.ExactBytes), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Added.Value], Key, "1.0.0", "1.0.1"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Added.Value], Key, "1.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Added.Value], Key, next: "1.1.0"));
        Assert.Throws<InvalidDataException>(() => Accept(patch, [Original.Value], Key, "1.0.1", "1.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(patch, [Original.Value], Key, "1.0.1", "1.0.0+new"));
        AssertSchema(patch.ExactBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accept_AllocatesCompatibleVersionForDeepNullableSchema(bool platform)
    {
        // Match SunderRpcV1ConformanceTests.NestedNullableSchemaDescriptor's valid 22-wrapper recipe.
        var schema = "{\"type\":\"boolean\"}";
        for (var index = 0; index < 22; index++)
        {
            schema = "{\"oneOf\":[{\"type\":\"null\"},{\"type\":\"object\",\"properties\":{\"next\":"
                + schema + "},\"required\":[\"next\"],\"additionalProperties\":false}]}";
        }
        var json = JsonNode.Parse(Original.Value.Descriptor.GetCanonicalUtf8())!;
        if (!platform) json["contractId"] = "example.deep.stack.contributor";
        var requestDefinition = Assert.Single(Original.Value.Methods).RequestDefinition;
        json["$defs"]![requestDefinition] = JsonNode.Parse(schema, documentOptions: new JsonDocumentOptions { MaxDepth = 256 });
        var options = new JsonSerializerOptions { MaxDepth = 256 };
        var previous = Original.Value with
        {
            Descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString(options))),
        };
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(previous.Descriptor.GetCanonicalUtf8()));
        json["$defs"]![requestDefinition]!["description"] = "Reviewed compatible annotation";
        var candidate = previous with
        {
            Descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString(options))),
        };
        json["version"] = "1.0.1";
        var expected = candidate with
        {
            Descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString(options))),
        };
        SunderRpcOwnedContractBaseline issued;
        SunderRpcOwnedContractBaseline accepted;
        if (platform)
        {
            var baseline = Baseline(Snapshot(previous, "release-frozen"));
            issued = baseline.Contracts[Key];
            var result = Parse(Accept(baseline, [candidate]));
            accepted = result.Contracts[Key];
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(result, [expected]);
            Assert.Equal(result.ExactBytes, Accept(result, [expected]));
        }
        else
        {
            var draft = new SunderRpcOwnedPackageBaseline("example.deep",
                new Dictionary<string, SunderRpcOwnedContractBaseline> { [Key] = new(previous.Descriptor.ContractId, Snapshot(previous), []) }, [], string.Empty);
            var baseline = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(
                SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(draft, [previous], Release, null));
            issued = baseline.Contracts[Key];
            var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(
                SunderRpcAuthoringDocuments.PlanOwnedBaselineAcceptance(baseline, [candidate], null, null, null, false));
            accepted = result.Contracts[Key];
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(result, [expected]);
            Assert.Equal(result.ExactBytes, SunderRpcAuthoringDocuments.PlanOwnedBaselineAcceptance(result, [expected], null, null, null, false));
        }
        Assert.Equal("1.0.0", candidate.Descriptor.Version);
        Assert.Equal("1.0.1", accepted.Current!.Descriptor.Version);
        Assert.Equal(expected.Descriptor.GetCanonicalUtf8(), accepted.Current.Descriptor.GetCanonicalUtf8());
        Assert.Equal(previous.Descriptor.GetCanonicalAbiProjectionUtf8(), accepted.Current.Descriptor.GetCanonicalAbiProjectionUtf8());
        AssertSnapshot(issued.Current!, Assert.Single(accepted.History));
        AssertDraft(accepted.Current);
    }

    [Fact]
    public void Accept_UnissuedDraftCanRetainItsVersionButBreakingReplacementRequiresReview()
    {
        var baseline = Baseline(Snapshot(Original.Value));
        var compatible = Parse(Accept(baseline, [Added.Value]));
        Assert.Equal("1.0.0", compatible.Contracts[Key].Current!.Descriptor.Version);
        Assert.Empty(compatible.Contracts[Key].History);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Breaking.Value]));
        var reviewed = Parse(Accept(baseline, [Breaking.Value], Key, "1.0.0", "1.0.0", acknowledge: true));
        Assert.Equal(Breaking.Value.Descriptor.GetCanonicalUtf8(), reviewed.Contracts[Key].Current!.Descriptor.GetCanonicalUtf8());
        Assert.Empty(reviewed.Contracts[Key].History);
        AssertDraft(reviewed.Contracts[Key].Current!);
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData(null, "1.0.0", "2.0.0", true)]
    [InlineData(Key, "1.0.0", "2.0.0", false)]
    [InlineData(Key, "0.9.0", "2.0.0", true)]
    [InlineData(Key, "1.0.0", "1.1.0", true)]
    [InlineData(Key, null, "2.0.0", true)]
    [InlineData("Stack-Contributor", "1.0.0", "2.0.0", true)]
    public void Accept_BreakingIssuedChangeRequiresTargetedExactOldNewAcknowledgement(string? key, string? expected, string? next, bool acknowledged)
    {
        var baseline = Baseline(Snapshot(Original.Value, "published"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Breaking.Value], key, expected, next, acknowledged));
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
    }

    [Fact]
    public void Accept_ReviewedMajorPreservesHistoryAndCannotFanOutToOtherChanges()
    {
        var baseline = WithOther(Baseline(Snapshot(Original.Value, "published")), Snapshot(Other.Value, "release-frozen"));
        var accepted = Parse(Accept(baseline, [Breaking.Value, Other.Value], Key, "1.0.0", "2.0.0", acknowledge: true));
        Assert.Equal("2.0.0", accepted.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshot(baseline.Contracts[Key].Current!, Assert.Single(accepted.Contracts[Key].History));
        AssertSnapshot(baseline.Contracts[Other.Value.ContractKey].Current!, accepted.Contracts[Other.Value.ContractKey].Current!);
        var changedOther = Change(Other.Value, root => root["$defs"]!.AsObject().First().Value!["description"] = "Unselected change");
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Breaking.Value], Key, "1.0.0", "2.0.0", acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Breaking.Value, changedOther], Key, "1.0.0", "2.0.0", acknowledge: true));
    }

    [Fact]
    public void Accept_UnclassifiedDefinitionChangeRequiresReviewedMajorAcknowledgement()
    {
        var previous = Change(Original.Value, root => root["$defs"]!["Unused"] = new JsonObject { ["type"] = "string" });
        var candidate = Change(previous, root => root["$defs"]!["Unused"]!["maxLength"] = 10);
        var baseline = Baseline(Snapshot(previous, "published"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "1.1.0", acknowledge: true));
        var result = Parse(Accept(baseline, [candidate], Key, "1.0.0", "2.0.0", acknowledge: true));
        Assert.Equal("2.0.0", result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshot(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
    }

    [Theory]
    [InlineData("1.0.6", "1.1.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0+planned", "1.2.0+planned")]
    public void Accept_DraftFloorsAreAnchoredToImmutableSnapshots(string planned, string expected)
    {
        var history = Snapshot(AtVersion(Original.Value, "1.0.5"), "published");
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, planned)), history);
        var result = Parse(Accept(baseline, [AtVersion(Added.Value, planned)]));
        Assert.Equal(expected, result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshot(history, Assert.Single(result.Contracts[Key].History));
    }

    [Fact]
    public void CheckAcceptFreeze_ValidateAllApplicableHistoryNotOnlyTheHighestSnapshot()
    {
        var older = Snapshot(Added.Value, "published", "7.0.0");
        var highest = Snapshot(AtVersion(Original.Value, "1.1.0"), "release-frozen", "8.0.0");
        var candidate = AtVersion(Original.Value, "1.2.0");
        var baseline = Baseline(Snapshot(candidate), older, highest);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [candidate]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate]));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [candidate]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.2.0", "1.2.0", acknowledge: true));
        var reviewed = Parse(Accept(baseline, [candidate], Key, "1.2.0", "2.0.0", acknowledge: true));
        Assert.Equal("2.0.0", reviewed.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(2, reviewed.Contracts[Key].History.Count);
    }

    [Fact]
    public void Accept_ExpectedVersionIncludesMetadataAndChangedPrereleaseAllocationFailsClosed()
    {
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, "1.0.0+issued"), "published"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Added.Value], Key, "1.0.0", "1.1.0"));
        Assert.Equal("1.1.0", Parse(Accept(baseline, [Added.Value], Key, "1.0.0+issued", "1.1.0")).Contracts[Key].Current!.Descriptor.Version);
        var prerelease = Baseline(Snapshot(AtVersion(Original.Value, "1.0.0-preview.1"), "published"));
        Assert.Throws<InvalidDataException>(() => Accept(prerelease, [Added.Value], Key, "1.0.0-preview.1", "1.1.0", acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Accept(null, [Original.Value], Key, next: "1.0.0-preview.1"));
        Assert.Throws<InvalidDataException>(() => Freeze(Baseline(Snapshot(AtVersion(Original.Value, "1.0.0-preview.1"))), [AtVersion(Original.Value, "1.0.0-preview.1")]));
    }

    [Theory]
    [InlineData("draft", false)]
    [InlineData("draft", true)]
    [InlineData("release-frozen", false)]
    [InlineData("published", false)]
    public void Accept_OptionalContractRemovalRetainsOnlyIssuedHistoryAndReintroductionCannotReuseVersions(string state, bool hasHistory)
    {
        var originalOther = Snapshot(Other.Value, "published", "7.0.0");
        var candidate = AtVersion(Other.Value, "1.1.0");
        var baseline = WithOther(Baseline(Snapshot(Original.Value)), Snapshot(candidate, state), hasHistory ? [originalOther] : []);
        var key = candidate.ContractKey;
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value], key, "1.1.0", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value], key, "1.1.0", "1.2.0", acknowledge: true));
        var bytes = Accept(baseline, [Original.Value], key, "1.1.0", "2.0.0", acknowledge: true);
        var removed = Parse(bytes);
        Assert.Equal(bytes, Accept(removed, [Original.Value]));
        if (state == "draft" && !hasHistory) Assert.False(removed.Contracts.ContainsKey(key));
        else
        {
            Assert.Null(removed.Contracts[key].Current);
            Assert.DoesNotContain(key, SunderRpcAuthoringDocuments.CompilerIdentities(removed).Keys);
            var last = Assert.Single(removed.Contracts[key].History);
            AssertSnapshot(state == "draft" ? originalOther : baseline.Contracts[key].Current!, last);
            Assert.Throws<InvalidDataException>(() => Accept(removed, [Original.Value, candidate], key, last.Descriptor.Version, last.Descriptor.Version));
            var reintroduced = Parse(Accept(removed, [Original.Value, candidate]));
            AssertSnapshot(last, Assert.Single(reintroduced.Contracts[key].History));
            AssertDraft(reintroduced.Contracts[key].Current!);
            Assert.NotEqual(last.Descriptor.Version, reintroduced.Contracts[key].Current!.Descriptor.Version);
        }
        AssertSchema(bytes);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void Accept_MandatoryStackCannotBeRemovedEvenWithReviewedMajorAcknowledgement(string state)
    {
        var baseline = WithOther(Baseline(Snapshot(Original.Value, state)), Snapshot(Other.Value));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Other.Value], Key, "1.0.0", "2.0.0", acknowledge: true));
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
    }

    [Fact]
    public void CheckAcceptFreeze_RejectInvalidKeysIdentitiesDuplicatesExposureAndSourceLimits()
    {
        var baseline = WithOther(Baseline(Snapshot(Original.Value)), Snapshot(Other.Value));
        var otherId = Change(Other.Value, root => root["contractId"] = "sunder.core.changed");
        foreach (var candidates in new SunderRpcCSharpCompiledContract[][]
        {
            [Original.Value, Other.Value with { Exposure = SunderServiceExposure.Package }],
            [Original.Value, Other.Value with { Exposure = (SunderServiceExposure)99 }],
            [Original.Value, Other.Value with { ContractKey = "renamed" }],
            [Original.Value, otherId],
            [Original.Value, Other.Value, Other.Value],
            [Original.Value, Other.Value, otherId with { ContractKey = "Other" }],
            [Original.Value, Other.Value with { ContractKey = "invalid/" }],
            Enumerable.Repeat(Original.Value, 129).ToArray(),
        })
        {
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, candidates));
            Assert.Throws<InvalidDataException>(() => Accept(baseline, candidates));
            Assert.Throws<InvalidDataException>(() => Freeze(baseline, candidates));
        }
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value, Other.Value], "unknown"));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value, Other.Value], key: "unknown"));
    }

    [Fact]
    public void Freeze_UsesExactCompiledAcceptedBytesAndOnlyIndependentCatalogReleaseEvidence()
    {
        var baseline = Parse(Accept(null, [Original.Value], Key, next: "2.0.0"));
        var candidate = Compile(Source, baseline);
        var bytes = Freeze(baseline, [candidate]);
        var frozen = Parse(bytes);
        var current = frozen.Contracts[Key].Current!;
        Assert.Equal("release-frozen", current.State);
        Assert.Equal("2.0.0", current.Descriptor.Version);
        Assert.Equal(candidate.Descriptor.GetCanonicalUtf8(), current.Descriptor.GetCanonicalUtf8());
        Assert.Equal(candidate.Descriptor.GetCanonicalAbiProjectionUtf8(), current.Descriptor.GetCanonicalAbiProjectionUtf8());
        var release = current.SnapshotJson.GetProperty("catalogRelease");
        Assert.Equal(new[] { "authorityId", "releaseVersion" }, release.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Authority, release.GetProperty("authorityId").GetString());
        Assert.Equal(Release, release.GetProperty("releaseVersion").GetString());
        Assert.False(current.SnapshotJson.TryGetProperty("packageRelease", out _));
        Assert.False(current.SnapshotJson.TryGetProperty("publication", out _));
        Assert.False(JsonNode.Parse(bytes)!.AsObject().ContainsKey("packageId"));
        Assert.Empty(frozen.Contracts[Key].History);
        AssertDraft(baseline.Contracts[Key].Current!);
        Assert.Equal(bytes, Freeze(frozen, [candidate]));
        AssertSchema(bytes);
    }

    [Theory]
    [InlineData("release-frozen", "1.0.0")]
    [InlineData("published", "1.0.0")]
    [InlineData("release-frozen", "1.0.0-preview.1")]
    [InlineData("published", "1.0.0-preview.1")]
    public void Freeze_UnchangedIssuedSnapshotsAreReusableAcrossCatalogReleasesWithOriginalEvidence(string state, string version)
    {
        var candidate = AtVersion(Original.Value, version);
        var baseline = Baseline(Snapshot(candidate, state));
        foreach (var release in new[] { Release, "9.8.8", "10.0.0", "9.8.7+new" })
        {
            Assert.Equal(baseline.ExactBytes, Freeze(baseline, [candidate], release));
            Assert.Equal(baseline.ExactBytes, Freeze(baseline, [candidate], release, Key));
            Assert.Equal(baseline.ExactBytes, Accept(baseline, [candidate]));
        }
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [AtVersion(Added.Value, version)], "10.0.0"));
        AssertSnapshot(baseline.Contracts[Key].Current!, Parse(SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline)).Contracts[Key].Current!);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void CheckAndFreeze_RequireTheFullExactSourceSetEvenWhenTargetedOrAlreadyIssued(string state)
    {
        var baseline = WithOther(Baseline(Snapshot(Original.Value, state)), Snapshot(Other.Value, state));
        var annotation = Change(Other.Value, root => root["$defs"]!.AsObject().First().Value!["title"] = "Changed annotation");
        var extra = Change(Other.Value with { ContractKey = "extra" }, root => root["contractId"] = "sunder.core.extra");
        foreach (var candidates in new SunderRpcCSharpCompiledContract[][]
        {
            [], [Original.Value], [Original.Value, Other.Value, extra], [Added.Value, Other.Value],
            [Original.Value, annotation], [Original.Value, AtVersion(Other.Value, "1.0.1")],
        })
        {
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, candidates));
            Assert.Throws<InvalidDataException>(() => Freeze(baseline, candidates, key: Key));
        }
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-preview.1")]
    [InlineData("")]
    public void Freeze_RequiresStableExactCatalogReleaseEvenForUnchangedIssuedContracts(string release)
    {
        foreach (var state in new[] { "draft", "release-frozen", "published" })
        {
            var baseline = Baseline(Snapshot(Original.Value, state));
            Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value], release));
        }
    }

    [Fact]
    public void Freeze_TargetedTransitionPreservesOtherDraftsButValidatesTheirSourceAndHistory()
    {
        var baseline = WithOther(Baseline(Snapshot(Original.Value)), Snapshot(Other.Value));
        var frozen = Parse(Freeze(baseline, [Other.Value, Original.Value], key: Key));
        Assert.Equal("release-frozen", frozen.Contracts[Key].Current!.State);
        AssertSnapshot(baseline.Contracts[Other.Value.ContractKey].Current!, frozen.Contracts[Other.Value.ContractKey].Current!);
        var later = Parse(Freeze(frozen, [Original.Value, Other.Value], "9.8.8"));
        AssertSnapshot(frozen.Contracts[Key].Current!, later.Contracts[Key].Current!);
        Assert.Equal("9.8.8", later.Contracts[Other.Value.ContractKey].Current!.SnapshotJson.GetProperty("catalogRelease").GetProperty("releaseVersion").GetString());
        var invalidOther = AtVersion(Change(Other.Value, root =>
        {
            var methods = root["services"]!["other"]!["methods"]!.AsObject();
            methods["fetch"] = methods["read"]!.DeepClone();
            methods.Remove("read");
        }), "1.1.0");
        var invalid = WithOther(baseline, Snapshot(invalidOther), [Snapshot(Other.Value, "published")]);
        Assert.Throws<InvalidDataException>(() => Freeze(invalid, [Original.Value, invalidOther], key: Key));
    }

    [Theory]
    [InlineData("minimal-package-owned-baseline.json")]
    [InlineData("package-owned-baseline.json")]
    public void PackageReadersAndWritersRemainPackageOnlyAndPreserveFixtureBytes(string fileName)
    {
        var bytes = File.ReadAllBytes(Fixture("conformance/valid/" + fileName));
        var package = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);
        Assert.Equal(bytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(package));
        Assert.Throws<InvalidDataException>(() => Parse(bytes));
        var platformPath = Fixture("conformance/valid/platform-owned-baseline.json");
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(platformPath));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(File.ReadAllBytes(platformPath)));
        var platform = Baseline(Snapshot(Original.Value, "published"));
        var disguised = new SunderRpcOwnedPackageBaseline(Authority, platform.Contracts, [], string.Empty);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(disguised));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.PlanOwnedBaselineAcceptance(disguised, [Original.Value], null, null, null, false));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(disguised, [Original.Value], Release, null));
    }

    private static byte[] Accept(SunderRpcOwnedPlatformBaseline? baseline, IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string? key = null, string? expected = null, string? next = null, bool acknowledge = false)
        => SunderRpcAuthoringDocuments.PlanOwnedPlatformBaselineAcceptance(baseline, candidates, key, expected, next, acknowledge);

    private static byte[] Freeze(SunderRpcOwnedPlatformBaseline baseline, IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string release = Release, string? key = null)
        => SunderRpcAuthoringDocuments.PlanOwnedPlatformBaselineFreeze(baseline, candidates, release, key);

    private static SunderRpcOwnedPlatformBaseline Parse(byte[] bytes) => SunderRpcAuthoringDocuments.ParseOwnedPlatformBaseline(bytes);

    private static SunderRpcOwnedPlatformBaseline Baseline(SunderRpcOwnedContractSnapshot current, params SunderRpcOwnedContractSnapshot[] history)
        => Parse(SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(new(
            new Dictionary<string, SunderRpcOwnedContractBaseline> { [Key] = new(ContractId, current, Array.AsReadOnly(history)) }, [], string.Empty)));

    private static SunderRpcOwnedPlatformBaseline WithOther(SunderRpcOwnedPlatformBaseline baseline, SunderRpcOwnedContractSnapshot current,
        SunderRpcOwnedContractSnapshot[]? history = null)
    {
        var contracts = baseline.Contracts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        contracts[Other.Value.ContractKey] = new(current.Descriptor.ContractId, current, history ?? []);
        return Parse(SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline with { Contracts = contracts }));
    }

    private static SunderRpcOwnedContractSnapshot Snapshot(SunderRpcCSharpCompiledContract candidate, string state = "draft", string release = Release)
    {
        var descriptor = candidate.Descriptor;
        var json = new JsonObject
        {
            ["state"] = state, ["exposure"] = "public", ["contractVersion"] = descriptor.Version,
            ["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length,
            ["canonicalDescriptor"] = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8()),
            ["descriptorSha256"] = descriptor.Sha256,
            ["abiProjectionBytes"] = descriptor.GetCanonicalAbiProjectionUtf8().Length,
            ["canonicalAbiProjection"] = Encoding.UTF8.GetString(descriptor.GetCanonicalAbiProjectionUtf8()),
            ["abiSha256"] = descriptor.AbiSha256,
        };
        if (state != "draft") json["catalogRelease"] = new JsonObject { ["authorityId"] = Authority, ["releaseVersion"] = release };
        if (state == "published") json["publication"] = new JsonObject
        {
            ["registryOrigin"] = "https://registry.example/", ["authorityId"] = Authority, ["catalogRelease"] = release,
            ["catalogContentIdentity"] = new string('a', 64), ["publishedAtUtc"] = "2026-09-11T12:34:56.123456789Z", ["etag"] = "\"" + descriptor.Sha256 + "\"",
        };
        return new(state, "public", descriptor, JsonSerializer.SerializeToElement(json));
    }

    private static SunderRpcCSharpCompiledContract AtVersion(SunderRpcCSharpCompiledContract candidate, string version)
        => Change(candidate, root => root["version"] = version);

    private static SunderRpcCSharpCompiledContract Change(SunderRpcCSharpCompiledContract candidate, Action<JsonObject> change)
    {
        var json = JsonNode.Parse(candidate.Descriptor.GetCanonicalUtf8())!.AsObject();
        change(json);
        return candidate with { Descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())) };
    }

    private static SunderRpcCSharpCompiledContract Compile(string source, SunderRpcOwnedPlatformBaseline? baseline = null)
    {
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable."))
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SunderServiceAttribute).Assembly.Location).Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(Authority, Release, [new("Source.cs", source)], references,
            OwnedContractsByContractKey: baseline is null ? null : SunderRpcAuthoringDocuments.CompilerIdentities(baseline)));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return Assert.Single(result.Contracts);
    }

    private static void AssertSnapshot(SunderRpcOwnedContractSnapshot expected, SunderRpcOwnedContractSnapshot actual)
    {
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Exposure, actual.Exposure);
        Assert.Equal(expected.Descriptor.GetCanonicalUtf8(), actual.Descriptor.GetCanonicalUtf8());
        Assert.Equal(expected.Descriptor.GetCanonicalAbiProjectionUtf8(), actual.Descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.True(JsonElement.DeepEquals(expected.SnapshotJson, actual.SnapshotJson));
    }

    private static void AssertDraft(SunderRpcOwnedContractSnapshot snapshot)
    {
        Assert.Equal("draft", snapshot.State);
        Assert.False(snapshot.SnapshotJson.TryGetProperty("catalogRelease", out _));
        Assert.False(snapshot.SnapshotJson.TryGetProperty("packageRelease", out _));
        Assert.False(snapshot.SnapshotJson.TryGetProperty("publication", out _));
    }

    private static byte[] Encode(JsonNode value)
        => Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static JsonObject FixtureRoot()
        => JsonNode.Parse(File.ReadAllBytes(Fixture("conformance/valid/platform-owned-baseline.json")))!.AsObject();

    private static void AssertSchema(byte[] bytes)
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Fixture("owned-contracts.schema.json")));
        using var document = JsonDocument.Parse(bytes);
        Assert.True(JsonSchemaTestValidator.IsValid(schema.RootElement, document.RootElement));
    }

    private static string Fixture(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Sunder Core."), "specs/rpc/v1/authoring", relativePath);
    }

    private const string Source = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        namespace Example.PlatformBaseline;
        [SunderService(SunderServiceExposure.Public, ContractId = "sunder.stack.contributor")]
        public sealed partial class StackContributorService
        {
            [SunderMethod]
            public ValueTask<Response> ReadAsync(Request request) => throw new System.NotImplementedException();
        }
        public sealed record Request
        {
            public required string Name { get; init; }
        }
        public sealed record Response
        {
            public required string Message { get; init; }
        }
        """;
}
