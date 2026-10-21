using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcOwnedProjectionReview(
    string ReviewKind, SunderRpcModelProjection ModelProjection, SunderRpcMethodPolicies MethodPolicies, JsonElement ReviewJson);

internal static partial class SunderRpcAuthoringDocuments
{
    private const int MaximumReviewedProjectionBytes = 4_194_304;

    internal static byte[] ReviewedProjectionBytes(SunderRpcModelProjection projection, SunderRpcMethodPolicies policies)
    {
        var json = new JsonObject { ["modelProjection"] = projection.ToJson() };
        if (!policies.IsEmpty) json.Add("methodPolicies", policies.ToJson());
        return WriteDeterministicDocument(JsonSerializer.SerializeToElement(json));
    }

    internal static SunderRpcOwnedProjectionReview CreateProjectionReview(SunderRpcContractDescriptor descriptor,
        SunderRpcModelProjection projection, SunderRpcMethodPolicies policies)
    {
        var canonical = ReviewedProjectionBytes(projection, policies);
        var value = JsonSerializer.SerializeToElement(new
        {
            contractVersion = descriptor.Version, descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256,
            reviewKind = "accepted-source", canonicalProjection = StrictUtf8.GetString(canonical), projectionSha256 = LowerSha256(canonical),
        });
        return ReadProjectionReview(value, descriptor);
    }

    private static SunderRpcOwnedProjectionReview ReadProjectionReview(JsonElement value, SunderRpcContractDescriptor descriptor)
    {
        const string label = "owned source projection review";
        string[] fields = ["contractVersion", "descriptorSha256", "abiSha256", "reviewKind", "canonicalProjection", "projectionSha256"];
        RequireObject(value, label, fields, fields, []);
        RequireString(value, "contractVersion", descriptor.Version, label);
        RequireString(value, "descriptorSha256", descriptor.Sha256, label);
        RequireString(value, "abiSha256", descriptor.AbiSha256, label);
        var kind = RequireString(value, "reviewKind", label);
        if (kind is not ("accepted-source" or "initial-source")) throw new InvalidDataException("Invalid owned source projection review kind.");
        var bytes = StrictUtf8.GetBytes(RequireString(value, "canonicalProjection", label));
        using var document = ParseStrictDocument(bytes, MaximumReviewedProjectionBytes, label);
        RequireObject(document.RootElement, label, ["modelProjection", "methodPolicies"], ["modelProjection"], ["methodPolicies"]);
        var projection = SunderRpcModelProjection.Parse(document.RootElement.GetProperty("modelProjection"));
        projection.Validate(descriptor);
        var policies = ReadMethodPolicies(document.RootElement, descriptor);
        if (!bytes.AsSpan().SequenceEqual(ReviewedProjectionBytes(projection, policies))
            || RequireSha256(value, "projectionSha256", label) != LowerSha256(bytes))
            throw new InvalidDataException("Owned source projection review must contain exact canonical projection bytes and hash.");
        return new(kind, projection, policies, value.Clone());
    }

    private static void AttachProjectionReviews(JsonElement value, string key,
        ref SunderRpcOwnedContractSnapshot? current, List<SunderRpcOwnedContractSnapshot> history)
    {
        if (!value.TryGetProperty("projectionReviews", out var reviews)) return; // Historical evidence, not an empty projection.
        if (reviews.ValueKind != JsonValueKind.Array || reviews.GetArrayLength() is < 1 or > 257)
            throw new InvalidDataException($"Owned contract '{key}' projectionReviews must have 1..257 entries; omit when absent.");
        SemanticVersion? previous = null;
        foreach (var json in reviews.EnumerateArray())
        {
            var version = RequireSemanticVersion(json, "contractVersion", "owned source projection review");
            var parsedVersion = SemanticVersion.Parse(version);
            if (previous is not null && parsedVersion.ComparePrecedenceTo(previous.Value) <= 0)
                throw new InvalidDataException("Owned projection reviews must be in strictly increasing semantic-version order.");
            previous = parsedVersion;
            var index = history.FindIndex(snapshot => snapshot.Descriptor.Version == version);
            var snapshot = index >= 0 ? history[index] : current?.Descriptor.Version == version ? current : null;
            if (snapshot is null) throw new InvalidDataException($"Owned projection review '{key}@{version}' has no exact retained snapshot.");
            snapshot = snapshot with { ProjectionReview = ReadProjectionReview(json, snapshot.Descriptor) };
            if (index >= 0) history[index] = snapshot; else current = snapshot;
        }
    }

    private static bool SameReviewedProjection(SunderRpcOwnedProjectionReview? review, SunderRpcCSharpCompiledContract candidate)
        => review is not null && SameModelProjection(review.ModelProjection, candidate.ModelProjection)
            && SameMethodPolicies(review.MethodPolicies, candidate.MethodPolicies);

    private static void RequireReviewedProjection(string key, SunderRpcOwnedContractSnapshot snapshot, SunderRpcCSharpCompiledContract candidate)
        => RequireReviewedProjection(key, snapshot, candidate.Descriptor, candidate.ModelProjection, candidate.MethodPolicies);

    // Qualified prebuilt fan-in validates retained receipts with the same gate,
    // without compiling or replacing already qualified role bytes.
    internal static void RequireReviewedProjection(string key, SunderRpcOwnedContractSnapshot snapshot,
        SunderRpcContractDescriptor descriptor, SunderRpcModelProjection projection, SunderRpcMethodPolicies policies)
    {
        if (snapshot.ProjectionReview is null)
            throw new InvalidDataException($"rpc-authoring.source-projection-unreviewed: '{key}@{snapshot.Descriptor.Version}' has descriptor/ABI evidence only. "
                + "Restore the reviewed source projection or explicitly accept a new Major tuple. Current source cannot supply historical projection evidence.");
        if (!SameModelProjection(snapshot.ProjectionReview.ModelProjection, projection) || !SameMethodPolicies(snapshot.ProjectionReview.MethodPolicies, policies))
            throw new InvalidDataException($"rpc-authoring.source-projection-drift: '{key}@{snapshot.Descriptor.Version}' wire classification "
                + $"{SunderRpcContractCompatibility.Classify(snapshot.Descriptor, descriptor).Kind}; exported source classification "
                + $"{ClassifyProjectionChange(snapshot, descriptor, projection, policies)}. "
                + "Model names, service-reference/optional policies and method policies must match reviewed evidence. Review through AcceptSunderContracts.");
    }

    // This is an exported-source policy, never a wire proof or a runtime substitution rule.
    internal static SunderRpcContractChangeKind ClassifyProjectionChange(SunderRpcOwnedContractSnapshot previous,
        SunderRpcContractDescriptor descriptor, SunderRpcModelProjection projection, SunderRpcMethodPolicies policies)
        => previous.ProjectionReview is { } review
            ? SunderRpcSourceCompatibility.Classify(previous.Descriptor, review.ModelProjection, review.MethodPolicies, descriptor, projection, policies)
            : SunderRpcContractChangeKind.Major; // Unknown, not unchanged.

    private static void ValidateProjectionReviewHistory(string key, SunderRpcContractDescriptor descriptor,
        SunderRpcOwnedProjectionReview? review, IReadOnlyList<SunderRpcOwnedContractSnapshot> comparisons)
    {
        foreach (var previous in comparisons)
        {
            var kind = review is null ? SunderRpcContractChangeKind.Major : ClassifyProjectionChange(previous, descriptor, review.ModelProjection, review.MethodPolicies);
            var minimum = GetMinimumOwnedContractVersion(previous.Descriptor.Version, kind);
            if (SemanticVersion.Parse(descriptor.Version).ComparePrecedenceTo(SemanticVersion.Parse(minimum)) < 0)
                throw new InvalidDataException($"rpc-authoring.source-projection-bump-required: '{key}' exported source is {kind} relative to immutable history '{previous.Descriptor.Version}'"
                    + (previous.ProjectionReview is null ? " (historical source projection unavailable)" : "") + $"; minimum '{minimum}'.");
        }
    }

    internal static void ValidateImmutableProjectionReviews(IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> before,
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> after)
    {
        foreach (var (key, contract) in before.Where(pair => HasPublicEvidence(pair.Value)))
            foreach (var snapshot in contract.History.Concat(contract.Current is { } current ? [current] : []))
            {
                if (snapshot.State == "draft") continue;
                var retained = after.TryGetValue(key, out var next) && next.ContractId == contract.ContractId
                    ? next.History.Concat(next.Current is { } active ? [active] : []).SingleOrDefault(value => value.Descriptor.Version == snapshot.Descriptor.Version) : null;
                if (retained is null) throw new InvalidDataException($"rpc-authoring.immutable-evidence-mutated: Retain complete frozen history for '{key}'.");
                // The only snapshot mutation admitted is verified publication confirmation.
                var oldJson = JsonNode.Parse(snapshot.SnapshotJson.GetRawText())!.AsObject();
                var newJson = JsonNode.Parse(retained.SnapshotJson.GetRawText())!.AsObject();
                if (snapshot.State == "release-frozen" && retained.State == "published")
                {
                    newJson["state"] = "release-frozen";
                    newJson.Remove("publication");
                }
                if (!JsonNode.DeepEquals(oldJson, newJson))
                    throw new InvalidDataException($"rpc-authoring.immutable-evidence-mutated: Preserve complete SnapshotJson/release/publication evidence for '{key}'.");
                if (snapshot.ProjectionReview is { } review)
                {
                    if (retained.ProjectionReview is null || !WriteDeterministicDocument(review.ReviewJson).AsSpan()
                        .SequenceEqual(WriteDeterministicDocument(retained.ProjectionReview.ReviewJson)))
                        throw new InvalidDataException($"rpc-authoring.immutable-projection-review-mutated: Preserve exact frozen projection review for '{key}'.");
                }
                else if (retained.ProjectionReview is not null)
                    throw new InvalidDataException($"rpc-authoring.source-projection-history-unavailable: Cannot backfill historical or published projection evidence for '{key}' from current source.");
            }
    }

    internal static void ValidateReviewedArtifactProjections(SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcPublishedContract> owned, IReadOnlyList<SunderRpcModelProjectionContract> projections)
    {
        var snapshots = baseline.Contracts.Values.SelectMany(contract => contract.History.Concat(contract.Current is { } current ? [current] : [])).ToArray();
        foreach (var bundle in owned.Where(bundle => bundle.Exposure == "public"))
        {
            var snapshot = snapshots.SingleOrDefault(value => value.Descriptor.ContractId == bundle.ContractId && value.Descriptor.Version == bundle.Version);
            // Legacy confirmation remains descriptor/publication evidence only. Never
            // create a historical source review as a side effect of confirmation.
            if (snapshot?.ProjectionReview is not { } review) continue;
            var actual = projections.SingleOrDefault(value => value.ContractId == bundle.ContractId && value.ContractVersion == bundle.Version);
            var expected = new SunderRpcModelProjectionContract(snapshot.Descriptor.ContractId, snapshot.Descriptor.Version,
                snapshot.Descriptor.Sha256, snapshot.Descriptor.AbiSha256, review.ModelProjection, review.MethodPolicies);
            if (actual is null || !SameProjectionContract(expected, actual))
                throw SunderRpcPublicationImport.Invalid($"Published artifact projection for '{bundle.ContractId}@{bundle.Version}' differs from the retained source review.");
        }
    }
}
