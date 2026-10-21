using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcOwnedBaselineEvolutionTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("service-reference")]
    [InlineData("optional-reference")]
    [InlineData("method-policy")]
    public void ProjectionReview_WireIdenticalSourceChangesFailCheckFreezeAndRequireReviewedMajor(string change)
    {
        var source = ProjectionSource();
        var baseline = Baseline(Snapshot(source, "release-frozen"));
        var candidate = ChangeProjection(source, change);
        Assert.Equal(SunderRpcContractChangeKind.Unchanged, SunderRpcContractCompatibility.Classify(source.Descriptor, candidate.Descriptor).Kind);
        var error = Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [candidate]));
        Assert.Contains("source-projection-drift", error.Message);
        Assert.Contains("wire classification Unchanged", error.Message);
        Assert.Contains("exported source classification Major", error.Message);
        Assert.Throws<InvalidDataException>(() => Freeze(baseline, [candidate]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate]));
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "2.0.0", "2.0.0", true));
        Assert.Contains("source-projection-bump-required", Assert.Throws<InvalidDataException>(() =>
            Accept(baseline, [candidate], Key, "2.0.0", "2.0.1", true)).Message);
        var accepted = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate], Key, "2.0.0", "3.0.0", true));
        var retained = Assert.Single(accepted.Contracts[Key].History);
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, retained);
        Assert.Equal(baseline.Contracts[Key].Current!.ProjectionReview!.ReviewJson.GetRawText(), retained.ProjectionReview!.ReviewJson.GetRawText());
        var currentSource = AtVersion(candidate, "3.0.0");
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(accepted, [currentSource]);
        var frozen = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Freeze(accepted, [currentSource]));
        Assert.Equal(frozen.ExactBytes, Freeze(frozen, [currentSource], "10.0.0"));
        AssertSchema(frozen.ExactBytes);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void ProjectionReview_MissingEvidenceNeverValidatesAsAnUnchangedSourceTuple(string state)
    {
        var source = AtVersion(Original.Value, "2.0.0");
        var legacy = Baseline(Snapshot(source, state) with { ProjectionReview = null },
            Snapshot(Original.Value, "published") with { ProjectionReview = null });
        Assert.Equal(legacy.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(legacy));
        Assert.Contains("source-projection-unreviewed", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(legacy, [source])).Message);
        Assert.Throws<InvalidDataException>(() => Freeze(legacy, [source]));
        Assert.Throws<InvalidDataException>(() => Accept(legacy, [source]));
        Assert.Null(legacy.Contracts[Key].Current!.ProjectionReview);
        Assert.Null(legacy.Contracts[Key].History[0].ProjectionReview);
        Assert.Equal(legacy.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(legacy));
    }

    [Fact]
    public void ProjectionReview_MethodWithNewModelRequiresMajorWhileExactReuseDoesNotAllocateATuple()
    {
        var source = Original.Value;
        var baseline = Baseline(Snapshot(source, "release-frozen"));
        Assert.Equal(baseline.ExactBytes, Accept(baseline, [source]));
        var added = Compile(Source.Insert(Source.IndexOf("[SunderMethod]", StringComparison.Ordinal),
            "[SunderMethod] public ValueTask<Extra> AddAsync() => throw new System.NotImplementedException();\n")
            + "\npublic sealed record Extra { public required string Text { get; init; } }");
        Assert.Equal(SunderRpcContractChangeKind.Minor, SunderRpcContractCompatibility.Classify(source.Descriptor, added.Descriptor).Kind);
        Assert.Throws<InvalidDataException>(() => Accept(baseline, [added]));
        Assert.Contains("source-projection-bump-required", Assert.Throws<InvalidDataException>(() =>
            Accept(baseline, [added], Key, "1.0.0", "1.1.0", true)).Message);
        var accepted = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [added], Key, "1.0.0", "2.0.0", true));
        Assert.Equal("2.0.0", accepted.Contracts[Key].Current!.Descriptor.Version);
        var unprojected = Baseline(Snapshot(source with { ModelProjection = SunderRpcModelProjection.Empty }, "release-frozen"));
        Assert.Throws<InvalidDataException>(() => Accept(unprojected, [source])); // Naming an existing wire value changes its source API.
    }

    public static IEnumerable<object[]> SourceShapeCases()
        => JsonNode.Parse(File.ReadAllBytes(Fixture("evolution/source-shape-cases.json")))!["cases"]!.AsArray()
            .Select(value => new object[] { value!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(SourceShapeCases))]
    public void ProjectionReview_SharedSourceShapeCasesProtectCallersAndProviders(string name)
    {
        var corpus = JsonNode.Parse(File.ReadAllBytes(Fixture("evolution/source-shape-cases.json")))!;
        var beforeJson = corpus["descriptor"]!;
        var nextJson = beforeJson.DeepClone();
        var test = corpus["cases"]!.AsArray().Single(value => value!["name"]!.GetValue<string>() == name)!;
        foreach (var change in test["changes"]!.AsArray())
        {
            var path = change!["path"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            var parent = path[..^1].Aggregate(nextJson, (node, key) => node[key]!);
            if (change["remove"]?.GetValue<bool>() == true) parent.AsObject().Remove(path[^1]);
            else parent[path[^1]] = change["copyFrom"] is { } copy
                ? copy.AsArray().Aggregate(beforeJson, (node, key) => node[key!.GetValue<string>()]!).DeepClone()
                : change["value"]!.DeepClone();
        }
        var projection = SunderRpcModelProjection.Parse(JsonSerializer.SerializeToElement(corpus["modelProjection"]));
        var source = Original.Value with { Descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(beforeJson)), ModelProjection = projection };
        var candidate = source with { Descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(nextJson)) };
        var baseline = Baseline(Snapshot(source, "release-frozen"));
        var previous = baseline.Contracts[Key].Current!;
        Assert.Equal(test["wire"]!.GetValue<string>(), SunderRpcContractCompatibility.Classify(source.Descriptor, candidate.Descriptor).Kind.ToString());
        Assert.Equal(test["source"]!.GetValue<string>(), SunderRpcAuthoringDocuments.ClassifyProjectionChange(previous,
            candidate.Descriptor, candidate.ModelProjection, candidate.MethodPolicies).ToString());
        if (test["source"]!.GetValue<string>() == "Major")
        {
            Assert.Contains("wire classification Minor, exported source classification Major", Assert.Throws<InvalidDataException>(() =>
                SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [candidate])).Message);
            Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate]));
            Assert.Throws<InvalidDataException>(() => Accept(Baseline(Snapshot(source)), [candidate]));
            Assert.Contains("source-projection-bump-required", Assert.Throws<InvalidDataException>(() => Accept(baseline, [candidate], Key, "1.0.0", "1.1.0", true)).Message);
            var accepted = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate], Key, "1.0.0", "2.0.0", true));
            AssertSnapshotEqual(previous, Assert.Single(accepted.Contracts[Key].History));
            Assert.Equal(previous.ProjectionReview!.ReviewJson.GetRawText(), accepted.Contracts[Key].History[0].ProjectionReview!.ReviewJson.GetRawText());
        }
        else if (name == "exact-reuse") Assert.Equal(baseline.ExactBytes, Freeze(baseline, [source], "99.0.0"));
        else
        {
            var accepted = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [candidate]));
            Assert.Equal(test["wire"]!.GetValue<string>() == "Minor" ? "1.1.0" : "1.0.1", accepted.Contracts[Key].Current!.Descriptor.Version);
        }
    }

    [Fact]
    public void ProjectionReview_NewContractDoesNotChangeExistingProviderObligations()
    {
        var source = Original.Value;
        var baseline = Baseline(Snapshot(source, "release-frozen"));
        var addition = Compile(Source + OtherService);
        var accepted = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(baseline, [source, addition]));
        AssertSnapshotEqual(baseline.Contracts[Key].Current!, accepted.Contracts[Key].Current!);
        Assert.Equal("1.0.0", accepted.Contracts[addition.ContractKey].Current!.Descriptor.Version);
    }

    [Theory]
    [InlineData("release-frozen")]
    [InlineData("published")]
    public void ProjectionReview_WriterCannotBackfillFrozenEvidenceEvenWithAnInitialSourceLabel(string state)
    {
        var source = Original.Value;
        var baseline = Baseline(Snapshot(source, state) with { ProjectionReview = null });
        var json = JsonNode.Parse(Baseline(Snapshot(source, state)).ExactBytes)!;
        json["contracts"]![Key]!["projectionReviews"]![0]!["reviewKind"] = "initial-source";
        var proposed = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(json)));
        Assert.Contains("source-projection-history-unavailable", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = proposed.Contracts })).Message);
    }

    [Fact]
    public void ProjectionReview_BatchApprovalPinsProjectionEvenWhenWireAndInputFrameArgumentsAreUnchanged()
    {
        var source = ProjectionSource(); var candidate = ChangeProjection(source, "model");
        var baseline = Baseline(Snapshot(source, "release-frozen"));
        var hash = new string('a', 64);
        var proposal = SunderRpcAuthoringDocuments.WriteOwnedContractReviewProposal(baseline, [candidate], ReleaseVersion, hash, hash);
        var json = JsonNode.Parse(proposal)!; json["contracts"]![Key]!["acknowledgeBreakingChange"] = true;
        var review = SunderRpcAuthoringDocuments.ParseOwnedContractReview(SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(json)));
        Assert.NotEqual(review.Contracts[Key].ExpectedCurrentProjectionSha256, review.Contracts[Key].ExpectedNextProjectionSha256);
        var accepted = SunderRpcAuthoringDocuments.PlanOwnedBaselineBatchAcceptance(baseline, [candidate], review, ReleaseVersion, hash, hash);
        AssertSchema(accepted);
        var staleSource = ChangeProjection(candidate, "method-policy");
        Assert.Contains("exact current/next exported source projection", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.PlanOwnedBaselineBatchAcceptance(baseline, [staleSource], review, ReleaseVersion, hash, hash)).Message);
    }

    [Fact]
    public void ProjectionReview_PublicationConfirmationComparesRetainedFullArtifactEntry()
    {
        var source = ProjectionSource(); var baseline = Baseline(Snapshot(source, "release-frozen"));
        SunderRpcPublishedContract[] owned = [new(source.Descriptor.ContractId, source.Descriptor.Version, source.Descriptor.Sha256, source.Descriptor.AbiSha256, "public")];
        SunderRpcModelProjectionContract Projection(SunderRpcCSharpCompiledContract contract) => new(contract.Descriptor.ContractId, contract.Descriptor.Version,
            contract.Descriptor.Sha256, contract.Descriptor.AbiSha256, contract.ModelProjection, contract.MethodPolicies);
        SunderRpcAuthoringDocuments.ValidateReviewedArtifactProjections(baseline, owned, [Projection(source)]);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateReviewedArtifactProjections(baseline, owned, [Projection(ChangeProjection(source, "model"))]));
        var legacy = Baseline(Snapshot(source, "release-frozen") with { ProjectionReview = null });
        SunderRpcAuthoringDocuments.ValidateReviewedArtifactProjections(legacy, owned, [Projection(source)]);
        Assert.Null(legacy.Contracts[Key].Current!.ProjectionReview);
    }

    [Fact]
    public void ProjectionReview_UnknownHistoryRequiresNewMajorWithoutFabricatingOldSource()
    {
        var legacy = Baseline(Snapshot(Original.Value, "published") with { ProjectionReview = null });
        Assert.Throws<InvalidDataException>(() => Accept(legacy, [AddedError.Value], Key, "1.0.0", "1.1.0", true));
        var accepted = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(Accept(legacy, [AddedError.Value], Key, "1.0.0", "2.0.0", true));
        Assert.Null(Assert.Single(accepted.Contracts[Key].History).ProjectionReview);
        AssertSnapshotEqual(legacy.Contracts[Key].Current!, accepted.Contracts[Key].History[0]);
        Assert.Equal("accepted-source", accepted.Contracts[Key].Current!.ProjectionReview!.ReviewKind);
    }

    [Fact]
    public void ProjectionReview_ChecksEverySameMajorHistoryRatherThanOnlyLatestProjection()
    {
        var source = ProjectionSource();
        var changed = ChangeProjection(source, "model");
        var baseline = Baseline(Snapshot(AtVersion(changed, "2.2.0")), Snapshot(source, "published"), Snapshot(AtVersion(changed, "2.1.0"), "release-frozen"));
        Assert.Contains("source-projection-bump-required", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [AtVersion(changed, "2.2.0")])).Message);
    }

    [Theory]
    [InlineData("contractVersion")]
    [InlineData("descriptorSha256")]
    [InlineData("abiSha256")]
    [InlineData("projectionSha256")]
    [InlineData("canonicalProjection")]
    public void ProjectionReview_ReaderRejectsMismatchedCompanionEvidence(string field)
    {
        var baseline = Baseline(Snapshot(Original.Value));
        var json = JsonNode.Parse(baseline.ExactBytes)!;
        var review = json["contracts"]![Key]!["projectionReviews"]![0]!;
        review[field] = field == "contractVersion" ? "9.9.9" : field == "canonicalProjection" ? "{}\n" : new string('f', 64);
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(json))));
    }

    [Fact]
    public void ProjectionReview_WritersRejectFrozenReviewReplacementAndModelJsonDisagreement()
    {
        var source = ProjectionSource(); var changed = ChangeProjection(source, "model");
        var baseline = Baseline(Snapshot(source, "release-frozen"));
        var original = baseline.Contracts[Key];
        var review = SunderRpcAuthoringDocuments.CreateProjectionReview(source.Descriptor, changed.ModelProjection, source.MethodPolicies);
        var changedContracts = new Dictionary<string, SunderRpcOwnedContractBaseline> { [Key] = original with { Current = original.Current! with { ProjectionReview = review } } };
        Assert.Contains("immutable-projection-review-mutated", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = changedContracts })).Message);
        var mismatched = review with { ModelProjection = source.ModelProjection };
        changedContracts[Key] = original with { Current = original.Current! with { ProjectionReview = mismatched } };
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = changedContracts }));
    }

    private static SunderRpcCSharpCompiledContract ProjectionSource()
    {
        // Read frozen evidence as test input; do not modify or backfill the fixture.
        using var document = JsonDocument.Parse(File.ReadAllBytes(Fixture("conformance/valid/package-project-export-optional-reference.json")));
        var contract = document.RootElement.GetProperty("contracts")[0];
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!));
        var service = descriptor.Services[0]; var method = service.Methods[0];
        return Original.Value with
        {
            Descriptor = descriptor, ModelProjection = SunderRpcModelProjection.Parse(contract.GetProperty("modelProjection")),
            MethodPolicies = new(new Dictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>>
            { [service.ServiceId] = new Dictionary<string, SunderRpcMethodPolicy> { [method.MethodId] = new(60) } }),
        };
    }

    private static SunderRpcCSharpCompiledContract ChangeProjection(SunderRpcCSharpCompiledContract source, string change)
    {
        var json = source.ModelProjection.ToJson();
        if (change == "model") json["models"]!["Context"] = "RenamedContext";
        if (change == "service-reference") json["serviceReferences"]!["Locator"]!["contractId"] = "example.other";
        if (change == "optional-reference") json.Remove("optionalServiceReferenceMembers");
        var policies = source.MethodPolicies.ToJson();
        if (change == "method-policy") policies.First().Value!.AsObject().First().Value!["timeoutSeconds"] = 90;
        return source with { ModelProjection = SunderRpcModelProjection.Parse(JsonSerializer.SerializeToElement(json)),
            MethodPolicies = SunderRpcMethodPolicies.Parse(JsonSerializer.SerializeToElement(policies)) };
    }
}
