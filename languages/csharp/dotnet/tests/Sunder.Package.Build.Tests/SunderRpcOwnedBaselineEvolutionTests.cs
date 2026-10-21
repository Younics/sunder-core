using System.Security.Cryptography;
using System.Text;
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

public sealed partial class SunderRpcOwnedBaselineEvolutionTests
{
    private const string Owner = "example.evolution";
    private const string Key = "greeting";
    private const string ReleaseVersion = "9.8.7";
    private static readonly Lazy<SunderRpcCSharpCompiledContract> Original = new(() => Compile(Source));
    // A new unused error declaration is source-compatible; a required provider method is not.
    private static readonly Lazy<SunderRpcCSharpCompiledContract> AddedError = new(() => AddError(Original.Value, "example.added"));
    private static readonly Lazy<SunderRpcCSharpCompiledContract> RenamedMethod = new(() => Compile(Source.Replace("ReadAsync", "FetchAsync", StringComparison.Ordinal)));

    [Fact]
    public void Check_ReportsClassificationMinimumAndExecutableAcceptanceCommand()
    {
        var baseline = Baseline(Snapshot(Original.Value, "release-frozen"));
        var error = Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [AddedError.Value]));

        Assert.Contains("rpc-authoring.baseline-drift", error.Message, StringComparison.Ordinal);
        Assert.Contains("classified Minor", error.Message, StringComparison.Ordinal);
        Assert.Contains("minimum next version '1.1.0'", error.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet msbuild -t:AcceptSunderContracts", error.Message, StringComparison.Ordinal);
        Assert.Contains("-p:SunderContractKey=greeting", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canonicalDescriptor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RejectsExactSourceWhenTheDraftVersionViolatesImmutableHistory()
    {
        var candidate = AtVersion(RenamedMethod.Value, "1.0.1");
        var baseline = Baseline(Snapshot(candidate), Snapshot(Original.Value, "published"));

        var error = Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [candidate]));

        Assert.Contains("immutable history", error.Message, StringComparison.Ordinal);
        Assert.Contains("contract-version.bump-required", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("minimal-package-owned-baseline.json")]
    [InlineData("package-owned-baseline.json")]
    public void ReadWrite_ExistingSchemaFixturesRoundTripExactly(string fileName)
    {
        var path = Fixture("conformance/valid/" + fileName);
        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(path);

        var bytes = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline);

        Assert.Equal(File.ReadAllBytes(path), bytes);
        Assert.Equal(baseline.ExactBytes, bytes);
        AssertSchema(bytes);
        Assert.Equal(baseline.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void ReadWrite_RetainsCurrentHistoryAndEveryReleaseAndPublicationField()
    {
        var published = Snapshot(AtVersion(Original.Value, "1.0.0"), "published", "3.4.5");
        var frozen = Snapshot(AtVersion(AddedError.Value, "1.1.0"), "release-frozen", "6.7.8");
        var draft = Snapshot(AtVersion(AddedError.Value, "2.0.0") with { Exposure = SunderServiceExposure.Public });
        var baseline = Baseline(draft, published, frozen);
        var bytes = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline);

        var reread = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);
        var contract = reread.Contracts[Key];

        Assert.Equal("draft", contract.Current!.State);
        Assert.Equal("public", contract.Current.Exposure);
        Assert.Equal("2.0.0", contract.Current.Descriptor.Version);
        Assert.Equal(new[] { "1.0.0", "1.1.0" }, contract.History.Select(snapshot => snapshot.Descriptor.Version));
        AssertSnapshotEqual(published, contract.History[0]);
        AssertSnapshotEqual(frozen, contract.History[1]);
        AssertSnapshotEqual(draft, contract.Current);
        var identity = Assert.Single(SunderRpcAuthoringDocuments.CompilerIdentities(reread));
        Assert.Equal(Key, identity.Key);
        Assert.Equal(contract.ContractId, identity.Value.ContractId);
        Assert.Equal("2.0.0", identity.Value.ContractVersion);
        var evidence = contract.History[0].SnapshotJson.GetProperty("publication");
        Assert.Equal(new[] { "registryOrigin", "packageId", "packageVersion", "packageContentIdentity", "publishedAtUtc", "etag" },
            evidence.EnumerateObject().Select(property => property.Name));
        Assert.Equal("https://registry.example/", evidence.GetProperty("registryOrigin").GetString());
        Assert.Equal(Owner, evidence.GetProperty("packageId").GetString());
        Assert.Equal("3.4.5", evidence.GetProperty("packageVersion").GetString());
        Assert.Equal(new string('a', 64), evidence.GetProperty("packageContentIdentity").GetString());
        Assert.Equal("2026-09-11T12:34:56.123456789Z", evidence.GetProperty("publishedAtUtc").GetString());
        Assert.Equal("\"" + published.Descriptor.Sha256 + "\"", evidence.GetProperty("etag").GetString());
        var history = Assert.IsAssignableFrom<IList<SunderRpcOwnedContractSnapshot>>(contract.History);
        Assert.True(history.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => history.Add(published));
        bytes[0] = (byte)'!';
        Assert.Equal((byte)'{', reread.ExactBytes[0]);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(reread));
        AssertSchema(reread.ExactBytes);
    }

    [Fact]
    public void Write_SortsKeysButNeverReordersHistoryOrRepairsInvalidSnapshotModels()
    {
        var first = Snapshot(Original.Value, "published");
        var second = Snapshot(AtVersion(AddedError.Value, "1.1.0"), "release-frozen");
        var baseline = Baseline(null, first, second);
        var other = ChangeDescriptor(Original.Value with { ContractKey = "other" }, root => root["contractId"] = Owner + ".other");
        var contracts = new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal)
        {
            ["other"] = new(other.Descriptor.ContractId, Snapshot(other), []),
            [Key] = baseline.Contracts[Key],
        };
        var bytes = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = contracts });
        var parsed = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);
        Assert.Equal(new[] { Key, "other" }, parsed.Contracts.Keys);
        Assert.Null(parsed.Contracts[Key].Current);
        AssertSnapshotEqual(first, parsed.Contracts[Key].History[0]);
        AssertSnapshotEqual(second, parsed.Contracts[Key].History[1]);
        AssertSchema(bytes);

        contracts[Key] = baseline.Contracts[Key] with { History = [second, first] };
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = contracts }));
        contracts[Key] = new(first.Descriptor.ContractId, first with { State = "draft" }, []);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = contracts }));
        contracts[Key] = new(first.Descriptor.ContractId, first with { Descriptor = second.Descriptor }, []);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = contracts }));
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("packageVersion")]
    [InlineData("unknown")]
    public void Write_StrictlyReparsesRetainedEvidenceInsteadOfDiscardingOrRepairingIt(string mutation)
    {
        var snapshot = Snapshot(Original.Value, "published");
        var json = JsonNode.Parse(snapshot.SnapshotJson.GetRawText())!;
        if (mutation == "unknown") json["unexpected"] = true;
        else json["publication"]![mutation] = mutation == "packageVersion" ? "9.8.8" : "invalid";
        var altered = snapshot with { SnapshotJson = JsonSerializer.SerializeToElement(json) };
        var baseline = new SunderRpcOwnedPackageBaseline(Owner,
            new Dictionary<string, SunderRpcOwnedContractBaseline> { [Key] = new(snapshot.Descriptor.ContractId, altered, []) }, [], string.Empty);

        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value], "9.8.8"));
    }

    [Fact]
    public void Read_RemainsPackageOnlyAndRejectsNonDeterministicOrIncompleteInput()
    {
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(Fixture("conformance/valid/platform-owned-baseline.json")));
        var bytes = Baseline(Snapshot(Original.Value)).ExactBytes;
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes[..^1]));
        var invalid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"history\": []", "\"history\": null", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(invalid));
    }

    [Fact]
    public void Accept_NewSourceContractNeedsNoAuthoredSchemaAndDefaultsToIndependentVersionOne()
    {
        var baseline = EmptyBaseline();
        var source = Compile(Source);

        var bytes = Accept(baseline, [source]);
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);

        var current = result.Contracts[Key].Current!;
        Assert.Equal("1.0.0", current.Descriptor.Version);
        Assert.Equal(source.Descriptor.GetCanonicalUtf8(), current.Descriptor.GetCanonicalUtf8());
        Assert.Equal("draft", current.State);
        Assert.Empty(result.Contracts[Key].History);
        Assert.False(current.SnapshotJson.TryGetProperty("packageRelease", out _));
        Assert.False(current.SnapshotJson.TryGetProperty("publication", out _));
        Assert.Empty(baseline.Contracts);
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(result, [source]);
        AssertSchema(bytes);
    }

    [Fact]
    public void Accept_UnissuedDraftReplacementRetainsItsVersionWithoutInventingHistory()
    {
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, "3.2.1")));
        var candidate = AtVersion(AddedError.Value, "3.2.1");

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate]));

        Assert.Equal("3.2.1", result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(candidate.Descriptor.GetCanonicalUtf8(), result.Contracts[Key].Current!.Descriptor.GetCanonicalUtf8());
        Assert.Empty(result.Contracts[Key].History);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
    }

    [Theory]
    [InlineData("draft", "1.0.0")]
    [InlineData("release-frozen", "1.0.0")]
    [InlineData("published", "1.0.0")]
    [InlineData("draft", "1.0.0-preview.1")]
    [InlineData("release-frozen", "1.0.0-preview.1")]
    [InlineData("published", "1.0.0-preview.1")]
    public void Accept_ExactlyUnchangedSnapshotsRetainTheirStateEvidenceAndVersion(string state, string version)
    {
        var candidate = AtVersion(Original.Value, version);
        var baseline = Baseline(Snapshot(candidate, state));

        Assert.Equal(baseline.ExactBytes, Accept(baseline, [candidate]));
    }

    [Theory]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void Accept_ImmutableMinorChangeAutomaticallyAllocatesMinimumAndPreservesPriorSnapshot(string state)
    {
        var baseline = Baseline(Snapshot(Original.Value, state));
        var before = baseline.Contracts[Key].Current!;
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value], Key, "1.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value], Key, next: "1.1.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value], Key, "0.9.0", "1.1.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value], Key, "1.0.0", "1.0.1"));

        var bytes = Accept(baseline, [AddedError.Value]);
        Assert.Equal(bytes, Accept(baseline, [AddedError.Value], Key));
        Assert.Equal(bytes, Accept(baseline, [AddedError.Value], Key, "1.0.0", "1.1.0"));
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);

        AssertSnapshotEqual(before, Assert.Single(result.Contracts[Key].History));
        Assert.Equal("draft", result.Contracts[Key].Current!.State);
        Assert.Equal("1.1.0", result.Contracts[Key].Current!.Descriptor.Version);
        Assert.False(result.Contracts[Key].Current!.SnapshotJson.TryGetProperty("packageRelease", out _));
        Assert.False(result.Contracts[Key].Current!.SnapshotJson.TryGetProperty("publication", out _));
        AssertSnapshotEqual(before, baseline.Contracts[Key].Current!);
        Assert.Empty(baseline.Contracts[Key].History);
        AssertSchema(bytes);
    }

    [Fact]
    public void Accept_AnnotationOnlyChangeUsesPatchAndCannotReuseAnIssuedVersionByHash()
    {
        var baseline = Baseline(Snapshot(Original.Value, "published"));
        var annotated = ChangeDescriptor(Original.Value, root => root["$defs"]!.AsObject().First().Value!["description"] = "Reviewed \u03bb <>& / annotation");
        Assert.Equal(Original.Value.Descriptor.GetCanonicalAbiProjectionUtf8(), annotated.Descriptor.GetCanonicalAbiProjectionUtf8());

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [annotated]));

        Assert.Equal("1.0.1", result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
        Assert.Throws<InvalidDataException>(() => Accept(result, [Original.Value], Key, "1.0.1", "1.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(result, [Original.Value], Key, "1.0.1", "1.0.0+different"));
    }

    [Fact]
    public void Accept_WholeBatchAllocatesCompatibleImmutableChangesAtomicallyWithoutOverrides()
    {
        var other = AtVersion(Compile(Source + OtherService), "4.5.6");
        var published = Snapshot(Original.Value, "published");
        var frozen = Snapshot(other, "release-frozen");
        var baseline = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(
            EmptyBaseline() with
            {
                Contracts = new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal)
                {
                    [Key] = new(published.Descriptor.ContractId, published, []),
                    [other.ContractKey] = new(frozen.Descriptor.ContractId, frozen, []),
                },
            }));
        var annotated = ChangeDescriptor(other, root => root["$defs"]!.AsObject().First().Value!["description"] = "Reviewed batch annotation");

        var bytes = Accept(baseline, [annotated, AddedError.Value]);
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);

        Assert.Equal(bytes, Accept(baseline, [AddedError.Value, annotated]));
        Assert.Equal("1.1.0", result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal("4.5.7", result.Contracts[other.ContractKey].Current!.Descriptor.Version);
        AssertSnapshotEqual(published, Assert.Single(result.Contracts[Key].History));
        AssertSnapshotEqual(frozen, Assert.Single(result.Contracts[other.ContractKey].History));
        Assert.All(result.Contracts.Values, contract => Assert.Equal("draft", contract.Current!.State));
        Assert.Equal(bytes, Accept(result, [AtVersion(AddedError.Value, "1.1.0"), AtVersion(annotated, "4.5.7")]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value, annotated], expected: "1.0.0", next: "1.1.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value, annotated with { Exposure = SunderServiceExposure.Package }]));
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
        Assert.Empty(baseline.Contracts[Key].History);
        Assert.Empty(baseline.Contracts[other.ContractKey].History);
        AssertSchema(bytes);
    }

    [Theory]
    [InlineData("1.0.5", "1.1.0")]
    [InlineData("1.7.0", "1.7.1")]
    public void Accept_AutomaticHistoryFloorsAreAnchoredToEachSnapshotRatherThanRebasedOnHighest(string currentVersion, string expectedVersion)
    {
        var older = Snapshot(Original.Value, "published");
        var current = AtVersion(AddedError.Value, currentVersion);
        var baseline = Baseline(Snapshot(current, "release-frozen"), older);
        var candidate = ChangeDescriptor(current, root => root["$defs"]!.AsObject().First().Value!["description"] = "Reviewed annotation");

        var bytes = Accept(baseline, [candidate]);
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);

        Assert.Equal(expectedVersion, result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(bytes, Accept(baseline, [candidate], Key, currentVersion, expectedVersion));
        Assert.Equal(2, result.Contracts[Key].History.Count);
        AssertSnapshotEqual(older, result.Contracts[Key].History[0]);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, result.Contracts[Key].History[1]);
    }

    [Theory]
    [InlineData("1.0.6", "1.1.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0+planned", "1.2.0+planned")]
    public void Accept_AutomaticDraftVersionRetainsThePlanUnlessImmutableHistoryNeedsAHigherFloor(string plannedVersion, string expectedVersion)
    {
        var history = Snapshot(AtVersion(Original.Value, "1.0.5"), "published");
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, plannedVersion)), history);
        var candidate = AtVersion(AddedError.Value, plannedVersion);

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate]));

        Assert.Equal(expectedVersion, result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(AtVersion(candidate, expectedVersion).Descriptor.GetCanonicalUtf8(), result.Contracts[Key].Current!.Descriptor.GetCanonicalUtf8());
        AssertSnapshotEqual(history, Assert.Single(result.Contracts[Key].History));
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("minor")]
    [InlineData("major")]
    public void Accept_OverflowingMinimumUsesSmallestFittingStableBumpAndAllowsExplicitHigherVersion(string overflow)
    {
        var nines = new string('9', 252);
        var previousVersion = overflow switch
        {
            "patch" => "1.0." + nines,
            "minor" => "1." + nines + ".0",
            _ => "1" + new string('9', 251) + ".9.9",
        };
        var expectedVersion = overflow switch
        {
            "patch" => "1.1.0",
            "minor" => "2.0.0",
            _ => "2" + new string('0', 251) + ".0.0",
        };
        Assert.Equal(256, previousVersion.Length);
        var previous = AtVersion(Original.Value, previousVersion);
        var baseline = Baseline(Snapshot(previous, "published"));
        var candidate = overflow == "minor" ? AtVersion(AddedError.Value, previousVersion)
            : ChangeDescriptor(previous, root => root["$defs"]!.AsObject().First().Value!["description"] = "Reviewed annotation");

        var explicitBytes = Accept(baseline, [candidate], Key, previousVersion, expectedVersion);
        var automaticBytes = Accept(baseline, [candidate]);
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(automaticBytes);

        Assert.Equal(explicitBytes, automaticBytes);
        Assert.Equal(expectedVersion, result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, previousVersion, previousVersion));
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
        AssertSchema(automaticBytes);
    }

    [Fact]
    public void Accept_WhenNoStableBumpFitsReportsActionableVersionLimitWithoutChangingBaseline()
    {
        var previous = AtVersion(Original.Value, new string('9', 252) + ".9.9");
        var baseline = Baseline(Snapshot(previous, "release-frozen"));
        var candidate = ChangeDescriptor(previous, root => root["$defs"]!.AsObject().First().Value!["description"] = "Reviewed annotation");

        var error = Assert.Throws<InvalidOperationException>(() => Accept(baseline, [candidate]));

        Assert.Contains("contract-version.version-limit-exceeded", error.Message, StringComparison.Ordinal);
        Assert.Contains("review a new contract identity", error.Message, StringComparison.Ordinal);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
        Assert.Empty(baseline.Contracts[Key].History);
    }

    [Fact]
    public void Accept_ExpectedCurrentVersionUsesExactIdentityIncludingBuildMetadata()
    {
        var previous = AtVersion(Original.Value, "1.0.0+issued");
        var baseline = Baseline(Snapshot(previous, "published"));
        var candidate = AtVersion(AddedError.Value, previous.Descriptor.Version);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "1.1.0"));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate], Key, "1.0.0+issued", "1.1.0"));

        Assert.Equal("1.1.0", result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
    }

    [Fact]
    public void Accept_DraftMustSatisfyAllSameMajorImmutableHistoryNotOnlyHighest()
    {
        var older = Snapshot(AddedError.Value, "published");
        var highest = Snapshot(AtVersion(Original.Value, "1.1.0"), "release-frozen");
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, "1.2.0")), older, highest);
        var candidate = AtVersion(Compile(AddMethod(Source, "List")), "1.2.0");

        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate]));
        var error = Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.2.0", "1.2.0", acknowledge: true));

        Assert.Contains("history", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
    }

    [Fact]
    public void Accept_DraftWithHistoryCannotBypassAMajorVersionThroughSameVersionReplacement()
    {
        var history = Snapshot(Original.Value, "published");
        var baseline = Baseline(Snapshot(AtVersion(Original.Value, "1.1.0")), history);
        var candidate = AtVersion(RenamedMethod.Value, "1.1.0");
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.1.0", "1.1.0", acknowledge: true));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate], Key, "1.1.0", "2.0.0", acknowledge: true));

        Assert.Equal("2.0.0", result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshotEqual(history, Assert.Single(result.Contracts[Key].History));
        Assert.DoesNotContain(result.Contracts[Key].History, snapshot => snapshot.State == "draft");
    }

    [Fact]
    public void Accept_CompatibleDraftWithHistoryKeepsItsAlreadySufficientVersion()
    {
        var history = Snapshot(Original.Value, "published");
        var baseline = Baseline(Snapshot(AtVersion(AddedError.Value, "1.1.0")), history);
        var candidate = AtVersion(AddError(AddedError.Value, "example.second"), "1.1.0");

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate]));

        Assert.Equal("1.1.0", result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(candidate.Descriptor.GetCanonicalUtf8(), result.Contracts[Key].Current!.Descriptor.GetCanonicalUtf8());
        AssertSnapshotEqual(history, Assert.Single(result.Contracts[Key].History));
        AssertSchema(Freeze(result, [candidate]));
    }

    [Fact]
    public void Accept_UnissuedBreakingDraftNeedsReviewedTupleButCanKeepItsVersion()
    {
        var baseline = Baseline(Snapshot(Original.Value));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [RenamedMethod.Value]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [RenamedMethod.Value], acknowledge: true));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [RenamedMethod.Value], Key, "1.0.0", "1.0.0", acknowledge: true));

        Assert.Equal("1.0.0", result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(RenamedMethod.Value.Descriptor.GetCanonicalUtf8(), result.Contracts[Key].Current!.Descriptor.GetCanonicalUtf8());
        Assert.Empty(result.Contracts[Key].History);
    }

    [Fact]
    public void Accept_UnclassifiedDefinitionChangeRequiresTargetedMajorAcknowledgement()
    {
        var previous = ChangeDescriptor(Original.Value, root => root["$defs"]!["Unused"] = new JsonObject { ["type"] = "string" });
        var candidate = ChangeDescriptor(previous, root => root["$defs"]!["Unused"]!["maxLength"] = 10);
        var baseline = Baseline(Snapshot(previous, "published"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "1.1.0", acknowledge: true));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate], Key, "1.0.0", "2.0.0", acknowledge: true));

        Assert.Equal("2.0.0", result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accept_ExposureChangeIsConservativelyMajorInBothDirections(bool initiallyPublic)
    {
        // Public evidence remains explicitly governed; active private baselines are unsupported.
        var previous = Original.Value with { Exposure = initiallyPublic ? SunderServiceExposure.Public : SunderServiceExposure.Package };
        var candidate = previous with { Exposure = initiallyPublic ? SunderServiceExposure.Package : SunderServiceExposure.Public };
        var baseline = Baseline(Snapshot(previous, "release-frozen"));
        if (!initiallyPublic)
        {
            Assert.Contains("private-baseline-unsupported", Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate])).Message, StringComparison.Ordinal);
            return;
        }
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "1.1.0", acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], acknowledge: true));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate], Key, "1.0.0", "2.0.0", acknowledge: true));

        Assert.Equal("2.0.0", result.Contracts[Key].Current!.Descriptor.Version);
        Assert.Equal(initiallyPublic ? "package" : "public", result.Contracts[Key].Current!.Exposure);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
    }

    [Theory]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void Accept_RemovingIssuedContractRetainsImmutableHistoryAndOmitsCurrent(string state)
    {
        var baseline = Baseline(Snapshot(Original.Value, state));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, []));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [], Key, "1.0.0", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [], Key, "1.0.0", "1.1.0", acknowledge: true));

        var bytes = Accept(baseline, [], Key, "1.0.0", "2.0.0", acknowledge: true);
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);

        Assert.Null(result.Contracts[Key].Current);
        Assert.Empty(SunderRpcAuthoringDocuments.CompilerIdentities(result));
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, Assert.Single(result.Contracts[Key].History));
        using var document = JsonDocument.Parse(bytes);
        Assert.False(document.RootElement.GetProperty("contracts").GetProperty(Key).TryGetProperty("current", out _));
        Assert.Equal(bytes, Accept(result, []));
        AssertSchema(bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accept_RemovingDraftNeverFabricatesFrozenHistory(bool issuedHistory)
    {
        var history = Snapshot(Original.Value, "published");
        var baseline = issuedHistory
            ? Baseline(Snapshot(AtVersion(AddedError.Value, "1.1.0")), history)
            : Baseline(Snapshot(AtVersion(AddedError.Value, "1.1.0")));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, []));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [], Key, acknowledge: true));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [], Key, "1.1.0", "2.0.0", acknowledge: true));

        if (!issuedHistory) Assert.Empty(result.Contracts);
        else
        {
            Assert.Null(result.Contracts[Key].Current);
            AssertSnapshotEqual(history, Assert.Single(result.Contracts[Key].History));
        }
    }

    [Fact]
    public void Accept_ReintroducingRemovedKeyUsesRetainedIdentityAndHistoryRatherThanVersionOne()
    {
        var baseline = Baseline(null, Snapshot(Original.Value, "published"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value], Key, "1.0.0", "1.0.0"));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [AddedError.Value]));

        Assert.Equal(result.ExactBytes, Accept(baseline, [AddedError.Value], Key, "1.0.0", "1.1.0"));
        Assert.Equal("1.1.0", result.Contracts[Key].Current!.Descriptor.Version);
        AssertSnapshotEqual(baseline.Contracts[Key].History[0], Assert.Single(result.Contracts[Key].History));
    }

    [Fact]
    public void Accept_ExactKeysNeverInferStableIdentityChangesAliasesOrRenames()
    {
        var baseline = Baseline(Snapshot(Original.Value, "published"));
        var differentId = ChangeDescriptor(Original.Value, root => root["contractId"] = Owner + ".renamed");
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [differentId], Key, "1.0.0", "2.0.0", acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value with { ContractKey = "renamed" }]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value], "Greeting"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value, Original.Value]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [Original.Value, Original.Value with { ContractKey = "Greeting" }]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [differentId with { ContractKey = "renamed" }], Key, "1.0.0", "2.0.0", acknowledge: true));
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
    }

    [Fact]
    public void Accept_TargetedCommandRequiresAllOtherExactSourceKeysAndCannotFanOutAcknowledgement()
    {
        var other = Compile(Source + OtherService);
        var baseline = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(EmptyBaseline(), [other, Original.Value]));
        Assert.Equal(Accept(EmptyBaseline(), [Original.Value, other]), baseline.ExactBytes);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value], Key));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AddedError.Value, other with { Exposure = SunderServiceExposure.Package }], Key));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [RenamedMethod.Value, other], acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [RenamedMethod.Value, other], expected: "1.0.0", next: "2.0.0", acknowledge: true));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [AddedError.Value, other], Key));

        AssertSnapshotEqual(baseline.Contracts[other.ContractKey].Current!, result.Contracts[other.ContractKey].Current!);
        Assert.Equal(AddedError.Value.Descriptor.GetCanonicalUtf8(), result.Contracts[Key].Current!.Descriptor.GetCanonicalUtf8());
    }

    [Fact]
    public void Freeze_UsesExactAcceptedBytesAndIndependentPackageReleaseVersion()
    {
        var candidate = AtVersion(Original.Value, "2.3.4");
        var baseline = Baseline(Snapshot(candidate));

        var bytes = Freeze(baseline, [candidate]);
        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);

        var frozen = result.Contracts[Key].Current!;
        Assert.Equal("release-frozen", frozen.State);
        Assert.Equal("2.3.4", frozen.Descriptor.Version);
        Assert.Equal(candidate.Descriptor.GetCanonicalUtf8(), frozen.Descriptor.GetCanonicalUtf8());
        Assert.Equal(candidate.Descriptor.GetCanonicalAbiProjectionUtf8(), frozen.Descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(Owner, frozen.SnapshotJson.GetProperty("packageRelease").GetProperty("packageId").GetString());
        Assert.Equal(ReleaseVersion, frozen.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
        Assert.False(frozen.SnapshotJson.TryGetProperty("publication", out _));
        Assert.Empty(result.Contracts[Key].History);
        Assert.Equal("draft", baseline.Contracts[Key].Current!.State);
        Assert.Equal(bytes, Freeze(result, [candidate]));
        AssertSchema(bytes);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void Freeze_RejectsMissingExtraOrDifferentExactSourceBytesBeforeAnyTransition(string state)
    {
        var baseline = Baseline(Snapshot(Original.Value, state));
        var annotation = ChangeDescriptor(Original.Value, root => root["$defs"]!.AsObject().First().Value!["title"] = "Different annotation");
        foreach (var candidates in new SunderRpcCSharpCompiledContract[][]
        {
            [], [Original.Value, Original.Value], [AddedError.Value], [annotation],
            [AtVersion(Original.Value, "1.0.1")], [Original.Value with { Exposure = SunderServiceExposure.Package }],
            [Original.Value with { ContractKey = "Greeting" }],
        })
        {
            Assert.Throws<InvalidDataException>(() => Freeze(baseline, candidates, "9.8.8"));
        }
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value], packageVersion: "latest"));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value], contractKey: "Greeting"));
        Assert.Equal(state, baseline.Contracts[Key].Current!.State);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
    }

    [Theory]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void Freeze_UnchangedImmutableContractRetainsOriginalEvidenceAcrossPackageVersions(string state)
    {
        var baseline = Baseline(Snapshot(Original.Value, state));
        var original = baseline.Contracts[Key].Current!;

        foreach (var packageVersion in new[] { ReleaseVersion, "9.8.8", "10.0.0", "9.8.7+different" })
        {
            var bytes = Freeze(baseline, [Original.Value], packageVersion);
            Assert.Equal(baseline.ExactBytes, bytes);
            Assert.Equal(bytes, Freeze(baseline, [Original.Value], packageVersion, contractKey: Key));
            var retained = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes).Contracts[Key].Current!;
            AssertSnapshotEqual(original, retained);
            Assert.Equal("1.0.0", retained.Descriptor.Version);
            Assert.Equal(ReleaseVersion, retained.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
            Assert.Equal(original.SnapshotJson.GetProperty("packageRelease").GetRawText(), retained.SnapshotJson.GetProperty("packageRelease").GetRawText());
            if (state == "published")
                Assert.Equal(original.SnapshotJson.GetProperty("publication").GetRawText(), retained.SnapshotJson.GetProperty("publication").GetRawText());
        }
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
    }

    [Fact]
    public void Freeze_DoesNotInferPublicationAndFailedLaterPlansCannotThawOrRewriteTheFrozenTuple()
    {
        var frozenBytes = Freeze(Baseline(Snapshot(Original.Value)), [Original.Value]);
        var frozen = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(frozenBytes);

        Assert.Throws<InvalidDataException>(() => Accept(frozen, [RenamedMethod.Value]));
        Assert.Throws<InvalidDataException>(() => Freeze(frozen, [AddedError.Value], "9.8.8"));

        Assert.Equal("release-frozen", frozen.Contracts[Key].Current!.State);
        Assert.False(frozen.Contracts[Key].Current!.SnapshotJson.TryGetProperty("publication", out _));
        Assert.Equal(frozenBytes, Freeze(frozen, [Original.Value]));
        Assert.Equal(frozenBytes, Freeze(frozen, [Original.Value], "9.8.8"));
        Assert.Equal(frozenBytes, Accept(frozen, [Original.Value]));
    }

    [Fact]
    public void Freeze_TargetedTransitionStillValidatesTheEntireSourceAndPreservesOtherDrafts()
    {
        var other = Compile(Source + OtherService);
        var baseline = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(EmptyBaseline(), [Original.Value, other]));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value], contractKey: Key));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [Original.Value, other with { Exposure = SunderServiceExposure.Package }], contractKey: Key));

        var result = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Freeze(baseline, [other, Original.Value], contractKey: Key));

        Assert.Equal("release-frozen", result.Contracts[Key].Current!.State);
        AssertSnapshotEqual(baseline.Contracts[other.ContractKey].Current!, result.Contracts[other.ContractKey].Current!);
    }

    [Fact]
    public void Freeze_ValidatesDraftVersionAndExposureAgainstImmutableHistory()
    {
        var invalidVersion = Baseline(Snapshot(AtVersion(RenamedMethod.Value, "1.1.0")), Snapshot(Original.Value, "published"));
        var invalidExposure = Baseline(Snapshot(AtVersion(Original.Value, "1.1.0") with { Exposure = SunderServiceExposure.Package }), Snapshot(Original.Value, "published"));

        Assert.Throws<InvalidDataException>(() => Freeze(invalidVersion, [AtVersion(RenamedMethod.Value, "1.1.0")]));
        Assert.Throws<InvalidDataException>(() => Freeze(invalidExposure, [AtVersion(Original.Value, "1.1.0") with { Exposure = SunderServiceExposure.Package }]));
        Assert.Throws<InvalidDataException>(() => Accept(invalidVersion, [AtVersion(RenamedMethod.Value, "1.1.0")]));
        Assert.Throws<InvalidDataException>(() => Accept(invalidExposure, [AtVersion(Original.Value, "1.1.0") with { Exposure = SunderServiceExposure.Package }]));
    }

    [Fact]
    public void Accept_ChangedPrereleaseProgressionFailsClosedEvenWithExplicitAcknowledgement()
    {
        var prerelease = AtVersion(Original.Value, "1.0.0-preview.1");
        var baseline = Baseline(Snapshot(prerelease, "published"));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AtVersion(AddedError.Value, prerelease.Descriptor.Version)]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [AtVersion(AddedError.Value, prerelease.Descriptor.Version)], Key, prerelease.Descriptor.Version, "1.1.0", acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Accept(EmptyBaseline(), [Original.Value], Key, next: "1.0.0-preview.1"));
        var draft = Baseline(Snapshot(Original.Value), Snapshot(prerelease, "published"));
        Assert.Throws<InvalidDataException>(() => Accept(draft, [AddedError.Value]));
        Assert.Throws<InvalidDataException>(() => Accept(draft, [AddedError.Value], Key, "1.0.0", "1.1.0", acknowledge: true));
        Assert.Throws<InvalidDataException>(() => Freeze(draft, [Original.Value]));
        Assert.Equal(draft.ExactBytes, Accept(draft, [Original.Value]));
        Assert.Equal(baseline.ExactBytes, Accept(baseline, [prerelease]));
    }

    [Fact]
    public void Accept_UnrelatedOlderMajorPrereleaseDoesNotSkipApplicableStableHistory()
    {
        var prerelease = Snapshot(AtVersion(Original.Value, "0.1.0-preview.1"), "published");
        var stable = Snapshot(Original.Value, "published");
        var incompatible = AtVersion(RenamedMethod.Value, "1.1.0");
        var baseline = Baseline(Snapshot(incompatible), prerelease, stable);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [incompatible]));
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [incompatible]));

        var compatible = AtVersion(Original.Value, "2.0.0");
        var valid = Baseline(Snapshot(compatible), prerelease, stable);
        Assert.Equal(valid.ExactBytes, Accept(valid, [compatible]));
        AssertSchema(Freeze(valid, [compatible]));
    }

    [Fact]
    public void Publication_ConfirmsFrozenHistoryWithoutPromotingOrChangingTheCurrentDraft()
    {
        var frozen = Snapshot(Original.Value, "release-frozen");
        var draft = Snapshot(AtVersion(AddedError.Value, "1.1.0"));
        var baseline = Baseline(draft, frozen);
        var evidence = new SunderRpcVerifiedPackagePublication("https://registry.example/", Owner, ReleaseVersion, new string('a', 64), "2026-09-17T12:34:56.1234000+00:00",
            [new(frozen.Descriptor.ContractId, frozen.Descriptor.Version, frozen.Descriptor.Sha256, frozen.Descriptor.AbiSha256, frozen.Exposure)]);
        var bytes = SunderRpcAuthoringDocuments.PlanPackagePublication(baseline, evidence);
        var confirmed = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);
        AssertSnapshotEqual(draft, confirmed.Contracts[Key].Current!);
        var historical = Assert.Single(confirmed.Contracts[Key].History);
        Assert.Equal("published", historical.State); Assert.Equal(frozen.Descriptor.GetCanonicalUtf8(), historical.Descriptor.GetCanonicalUtf8());
        Assert.Equal("2026-09-17T12:34:56.1234Z", historical.SnapshotJson.GetProperty("publication").GetProperty("publishedAtUtc").GetString());
        Assert.Equal(bytes, SunderRpcAuthoringDocuments.PlanPackagePublication(confirmed, evidence));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.PlanPackagePublication(confirmed, evidence with { ContentIdentity = new string('b', 64) }));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("owner")]
    [InlineData("descriptor")]
    [InlineData("abi")]
    [InlineData("exposure")]
    [InlineData("release")]
    [InlineData("missing")]
    public void Publication_CannotFreezeOrInventArtifactAuthority(string mismatch)
    {
        var snapshot = Snapshot(Original.Value, mismatch == "draft" ? "draft" : "release-frozen"); var baseline = Baseline(snapshot);
        var evidence = new SunderRpcVerifiedPackagePublication("https://registry.example/", mismatch == "owner" ? "other.owner" : Owner,
            mismatch == "release" ? "99.0.0" : ReleaseVersion, new string('a', 64), "2026-09-17T12:34:56Z", mismatch == "missing" ? [] :
            [new(snapshot.Descriptor.ContractId, snapshot.Descriptor.Version, mismatch == "descriptor" ? new string('f', 64) : snapshot.Descriptor.Sha256,
                mismatch == "abi" ? new string('f', 64) : snapshot.Descriptor.AbiSha256, mismatch == "exposure" ? "invalid" : snapshot.Exposure)]);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.PlanPackagePublication(baseline, evidence));
    }

    private static byte[] Accept(SunderRpcOwnedPackageBaseline baseline, IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string? key = null, string? expected = null, string? next = null, bool acknowledge = false)
        => SunderRpcAuthoringDocuments.PlanOwnedBaselineAcceptance(baseline, candidates, key, expected, next, acknowledge);

    private static byte[] Freeze(SunderRpcOwnedPackageBaseline baseline, IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string packageVersion = ReleaseVersion, string? contractKey = null)
        => SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidates, packageVersion, contractKey);

    private static SunderRpcOwnedPackageBaseline EmptyBaseline()
        => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(
            new(Owner, new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal), [], string.Empty)));

    private static SunderRpcOwnedPackageBaseline Baseline(SunderRpcOwnedContractSnapshot? current, params SunderRpcOwnedContractSnapshot[] history)
    {
        var descriptor = (current ?? history[0]).Descriptor;
        return SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(new(
            Owner, new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal)
            {
                [Key] = new(descriptor.ContractId, current, Array.AsReadOnly(history)),
            }, [], string.Empty)));
    }

    private static SunderRpcOwnedContractSnapshot Snapshot(SunderRpcCSharpCompiledContract candidate, string state = "draft", string release = ReleaseVersion)
    {
        var descriptor = candidate.Descriptor;
        var exposure = candidate.Exposure == SunderServiceExposure.Public ? "public" : "package";
        var json = new JsonObject
        {
            ["state"] = state,
            ["exposure"] = exposure,
            ["contractVersion"] = descriptor.Version,
            ["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length,
            ["canonicalDescriptor"] = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8()),
            ["descriptorSha256"] = descriptor.Sha256,
            ["abiProjectionBytes"] = descriptor.GetCanonicalAbiProjectionUtf8().Length,
            ["canonicalAbiProjection"] = Encoding.UTF8.GetString(descriptor.GetCanonicalAbiProjectionUtf8()),
            ["abiSha256"] = descriptor.AbiSha256,
        };
        if (state != "draft") json["packageRelease"] = new JsonObject { ["packageId"] = Owner, ["packageVersion"] = release };
        if (state == "published") json["publication"] = new JsonObject
        {
            ["registryOrigin"] = "https://registry.example/", ["packageId"] = Owner, ["packageVersion"] = release,
            ["packageContentIdentity"] = new string('a', 64), ["publishedAtUtc"] = "2026-09-11T12:34:56.123456789Z", ["etag"] = "\"" + descriptor.Sha256 + "\"",
        };
        // Synthetic evolution fixtures have their exact source in this test. Frozen
        // historical files above remain projection-less and still round-trip verbatim.
        return new(state, exposure, descriptor, JsonSerializer.SerializeToElement(json))
        {
            ProjectionReview = SunderRpcAuthoringDocuments.CreateProjectionReview(descriptor, candidate.ModelProjection, candidate.MethodPolicies),
        };
    }

    private static SunderRpcCSharpCompiledContract AtVersion(SunderRpcCSharpCompiledContract candidate, string version)
        => ChangeDescriptor(candidate, root => root["version"] = version);

    private static SunderRpcCSharpCompiledContract ChangeDescriptor(SunderRpcCSharpCompiledContract candidate, Action<JsonObject> change)
    {
        var json = JsonNode.Parse(candidate.Descriptor.GetCanonicalUtf8())!.AsObject();
        change(json);
        return candidate with { Descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())) };
    }

    private static SunderRpcCSharpCompiledContract Compile(string source)
    {
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var references = trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SunderServiceAttribute).Assembly.Location)
            .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new SunderRpcCSharpSourceCompiler().Compile(new(Owner, ReleaseVersion, [new("Source.cs", source)], references));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return result.Contracts.Single(contract => contract.ContractKey == (source.Contains("OtherService", StringComparison.Ordinal) ? "other" : Key));
    }

    private static string AddMethod(string source, string name)
        => source.Insert(source.IndexOf("[SunderMethod]", StringComparison.Ordinal), "[SunderMethod]\n    public ValueTask<Response> " + name
            + "Async(Request request) => throw new System.NotImplementedException();\n    ");

    private static SunderRpcCSharpCompiledContract AddError(SunderRpcCSharpCompiledContract source, string code)
        => ChangeDescriptor(source, root => root["errors"]![code] = new JsonObject());

    private static void AssertSnapshotEqual(SunderRpcOwnedContractSnapshot expected, SunderRpcOwnedContractSnapshot actual)
    {
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Exposure, actual.Exposure);
        Assert.Equal(expected.Descriptor.GetCanonicalUtf8(), actual.Descriptor.GetCanonicalUtf8());
        Assert.Equal(expected.Descriptor.GetCanonicalAbiProjectionUtf8(), actual.Descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.True(JsonElement.DeepEquals(expected.SnapshotJson, actual.SnapshotJson));
    }

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
        namespace Example.Evolution;
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class GreetingService
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

    private const string OtherService = """

        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class OtherService
        {
            [SunderMethod]
            public ValueTask<Response> ReadAsync(Request request) => throw new System.NotImplementedException();
        }
        """;
}
