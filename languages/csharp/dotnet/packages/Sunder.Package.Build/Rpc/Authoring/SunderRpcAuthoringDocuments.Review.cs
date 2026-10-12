using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcOwnedContractReviewEntry(
    string ContractId, string ExpectedCurrentVersion, string ExpectedCurrentDescriptorSha256, string ExpectedCurrentAbiSha256,
    string NextVersion, string ExpectedNextDescriptorSha256, string ExpectedNextAbiSha256, bool AcknowledgeBreakingChange);

internal sealed record SunderRpcOwnedContractReview(
    string PackageId, string PackageVersion, string BaselineSha256, string InputFingerprint, string LockSha256,
    IReadOnlyDictionary<string, SunderRpcOwnedContractReviewEntry> Contracts);

internal static partial class SunderRpcAuthoringDocuments
{
    internal const int MaximumOwnedContractReviewBytes = 1_048_576;

    internal static SunderRpcOwnedContractReview ParseOwnedContractReview(byte[] bytes)
    {
        using var document = ParseStrictDocument(bytes, MaximumOwnedContractReviewBytes, "Sunder owned-contract batch review");
        var root = document.RootElement;
        const string label = "owned-contract batch review";
        string[] members = ["schemaVersion", "packageId", "packageVersion", "baselineSha256", "inputFingerprint", "lockSha256", "contracts"];
        RequireObject(root, label, members, members, []);
        RequireInteger(root, "schemaVersion", 1, label);
        var entries = new SortedDictionary<string, SunderRpcOwnedContractReviewEntry>(StringComparer.Ordinal);
        var map = ReadOrderedLockMap(RequireProperty(root, "contracts", JsonValueKind.Object, label), label);
        foreach (var property in map)
        {
            if (!IsPortableKey(property.Name)) throw ReviewError("A review key must be an exact portable source contract key.");
            string[] fields = ["contractId", "expectedCurrentVersion", "expectedCurrentDescriptorSha256", "expectedCurrentAbiSha256",
                "nextVersion", "expectedNextDescriptorSha256", "expectedNextAbiSha256", "acknowledgeBreakingChange"];
            RequireObject(property.Value, label + " entry", fields, fields, []);
            entries.Add(property.Name, new(RequirePackageId(property.Value, "contractId", label),
                RequireSemanticVersion(property.Value, "expectedCurrentVersion", label), RequireSha256(property.Value, "expectedCurrentDescriptorSha256", label),
                RequireSha256(property.Value, "expectedCurrentAbiSha256", label), RequireSemanticVersion(property.Value, "nextVersion", label),
                RequireSha256(property.Value, "expectedNextDescriptorSha256", label), RequireSha256(property.Value, "expectedNextAbiSha256", label),
                RequireBoolean(property.Value, "acknowledgeBreakingChange", label)));
        }
        return new(RequirePackageId(root, "packageId", label), RequireSemanticVersion(root, "packageVersion", label),
            RequireSha256(root, "baselineSha256", label), RequireSha256(root, "inputFingerprint", label), RequireSha256(root, "lockSha256", label),
            new ReadOnlyDictionary<string, SunderRpcOwnedContractReviewEntry>(entries));
    }

    internal static byte[] WriteOwnedContractReviewProposal(SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates, string packageVersion, string inputFingerprint, string lockSha256)
    {
        var source = BatchReviewSource(baseline, candidates);
        var entries = new JsonObject();
        foreach (var (key, candidate) in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var contract = baseline.Contracts[key]; var current = contract.Current!;
            if (OwnedSourceMatches(current, candidate)) continue;
            var history = current.State == "draft" ? contract.History : contract.History.Append(current).ToArray();
            var nextVersion = SelectOwnedCandidateVersion(current, current, candidate, ClassifyOwnedChange(current, candidate), history);
            RequireStableOwnedTransition(key, current.Descriptor.Version, nextVersion);
            var next = OwnedDescriptorAtVersion(candidate.Descriptor, nextVersion);
            ValidateOwnedCandidateHistory(key, next, Exposure(candidate.Exposure), history, preserveUnchangedPrerelease: false);
            entries.Add(key, new JsonObject
            {
                ["contractId"] = contract.ContractId,
                ["expectedCurrentVersion"] = current.Descriptor.Version,
                ["expectedCurrentDescriptorSha256"] = current.Descriptor.Sha256,
                ["expectedCurrentAbiSha256"] = current.Descriptor.AbiSha256,
                ["nextVersion"] = nextVersion,
                ["expectedNextDescriptorSha256"] = next.Sha256,
                ["expectedNextAbiSha256"] = next.AbiSha256,
                ["acknowledgeBreakingChange"] = false,
            });
        }
        var bytes = WriteDeterministicDocument(JsonSerializer.SerializeToElement(new JsonObject
        {
            ["schemaVersion"] = 1, ["packageId"] = baseline.PackageId, ["packageVersion"] = packageVersion,
            ["baselineSha256"] = baseline.Sha256, ["inputFingerprint"] = inputFingerprint, ["lockSha256"] = lockSha256,
            ["contracts"] = entries,
        }));
        _ = ParseOwnedContractReview(bytes);
        return bytes;
    }

    internal static byte[] PlanOwnedBaselineBatchAcceptance(SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates, SunderRpcOwnedContractReview review,
        string packageVersion, string inputFingerprint, string lockSha256)
    {
        if (review.PackageId != baseline.PackageId || review.PackageVersion != packageVersion || review.BaselineSha256 != baseline.Sha256
            || review.InputFingerprint != inputFingerprint || review.LockSha256 != lockSha256)
            throw ReviewError("The review does not match the exact package, baseline, source/reference input frame and lock. Regenerate its proposal from current source.");
        var source = BatchReviewSource(baseline, candidates);
        var changes = source.Where(pair => !OwnedSourceMatches(baseline.Contracts[pair.Key].Current!, pair.Value))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (!changes.SetEquals(review.Contracts.Keys))
            throw ReviewError("Review entries must cover all and only changed current contracts; unknown, extra, unchanged or missing keys are not accepted.");

        var result = baseline.Contracts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (key, entry) in review.Contracts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var contract = baseline.Contracts[key]; var current = contract.Current!;
            if (entry.ContractId != contract.ContractId || entry.ExpectedCurrentVersion != current.Descriptor.Version
                || entry.ExpectedCurrentDescriptorSha256 != current.Descriptor.Sha256 || entry.ExpectedCurrentAbiSha256 != current.Descriptor.AbiSha256)
                throw ReviewError($"Review '{key}' does not match its exact current contract identity/version/descriptor/ABI.");
            var next = OwnedDescriptorAtVersion(source[key].Descriptor, entry.NextVersion);
            if (entry.ExpectedNextDescriptorSha256 != next.Sha256 || entry.ExpectedNextAbiSha256 != next.AbiSha256)
                throw ReviewError($"Review '{key}' does not match the compiled next descriptor/ABI at its reviewed next version.");
            // Reuse the existing single-contract policy, including immutable-history
            // version floors and breaking acknowledgement. This is planning only;
            // the complete batch is compiled and committed once by the task.
            var accepted = AcceptOwnedContracts(new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal) { [key] = contract },
                new Dictionary<string, SunderRpcCSharpCompiledContract>(StringComparer.Ordinal) { [key] = source[key] },
                key, entry.ExpectedCurrentVersion, entry.NextVersion, entry.AcknowledgeBreakingChange);
            result[key] = accepted[key];
        }
        if (changes.Count == 0)
            _ = AcceptOwnedContracts(baseline.Contracts, source, null, null, null, false);
        return WriteOwnedPackageBaseline(baseline with { Contracts = result });
    }

    private static Dictionary<string, SunderRpcCSharpCompiledContract> BatchReviewSource(SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates)
    {
        _ = WriteOwnedPackageBaseline(baseline);
        var source = OwnedCandidatesByKey(baseline.Contracts, PublicLifecycleCandidates(baseline, candidates), null);
        if (!source.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(baseline.Contracts.Where(pair => pair.Value.Current is not null).Select(pair => pair.Key)))
            throw ReviewError("Batch tuple review requires the complete existing current contract set. Additions/removals require their existing explicit lifecycle review.");
        return source;
    }

    private static InvalidDataException ReviewError(string message) => new("rpc-authoring.batch-review-invalid: " + message);
}
