using System.Text;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcOwnedPlatformBaseline(
    IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> Contracts,
    byte[] ExactBytes,
    string Sha256);

internal static partial class SunderRpcAuthoringDocuments
{
    private const string CorePlatformAuthorityId = "sunder.core";
    private const string MandatoryPlatformContractId = "sunder.stack.contributor";

    internal static SunderRpcOwnedPlatformBaseline ReadOwnedPlatformBaseline(string path)
        => ParseOwnedPlatformBaseline(SunderRpcDescriptorFileReader.ReadStableBytes(
            path, MaximumOwnedBaselineBytes, "Sunder platform owned-contract baseline"));

    internal static SunderRpcOwnedPlatformBaseline ParseOwnedPlatformBaseline(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        // The platform parser also preflights the structural budget before allocating the DOM.
        using var document = SunderRpcAuthoringJson.Parse(bytes, MaximumOwnedBaselineBytes);
        if (!bytes.AsSpan().SequenceEqual(WriteDeterministicDocument(document.RootElement)))
        {
            throw new InvalidDataException("The platform owned-contract baseline does not use the required deterministic outer JSON form.");
        }
        const string label = "platform owned-contract baseline";
        var root = document.RootElement;
        RequireObject(root, label,
            ["schemaVersion", "authorityKind", "authorityId", "contracts"],
            ["schemaVersion", "authorityKind", "authorityId", "contracts"], []);
        RequireInteger(root, "schemaVersion", 1, label);
        RequireString(root, "authorityKind", "platform", label);
        RequireString(root, "authorityId", CorePlatformAuthorityId, label);
        var contracts = ReadOwnedContractMap(root, ReadPlatformSnapshot);
        if (!contracts.Values.Any(static contract => contract.ContractId == MandatoryPlatformContractId && contract.Current is not null))
        {
            throw new InvalidDataException($"The platform owned-contract baseline requires a current public '{MandatoryPlatformContractId}' contract.");
        }

        var publications = new Dictionary<(string Origin, string Release), JsonElement>();
        foreach (var contract in contracts.Values)
        {
            var snapshots = contract.Current is { } current ? contract.History.Append(current) : contract.History;
            foreach (var snapshot in snapshots)
            {
                if (!snapshot.SnapshotJson.TryGetProperty("publication", out var publication)) continue;
                var key = (publication.GetProperty("registryOrigin").GetString()!, publication.GetProperty("catalogRelease").GetString()!);
                if (publications.TryGetValue(key, out var previous)
                    && publication.EnumerateObject().Any(property => property.Name != "etag"
                        && !JsonElement.DeepEquals(property.Value, previous.GetProperty(property.Name))))
                {
                    throw new InvalidDataException("Platform snapshots contain conflicting evidence for the same Registry catalog publication.");
                }
                publications[key] = publication;
            }
        }
        return new(contracts, bytes.ToArray(), LowerSha256(bytes));
    }

    internal static byte[] WriteOwnedPlatformBaseline(SunderRpcOwnedPlatformBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var output = new StringBuilder();
        Line(output, 0, "{");
        IntegerProperty(output, 1, "schemaVersion", 1, comma: true);
        StringProperty(output, 1, "authorityKind", "platform", comma: true);
        StringProperty(output, 1, "authorityId", CorePlatformAuthorityId, comma: true);
        OwnedContractMapProperty(output, baseline.Contracts);
        Line(output, 0, "}");
        var bytes = Utf8NoBom.GetBytes(output.ToString());
        var reparsed = ParseOwnedPlatformBaseline(bytes);
        ValidateOwnedSnapshotModels(baseline.Contracts, reparsed.Contracts);
        if (baseline.ExactBytes.Length != 0) ValidateImmutableProjectionReviews(ParseOwnedPlatformBaseline(baseline.ExactBytes).Contracts, reparsed.Contracts);
        return bytes;
    }

    internal static IReadOnlyDictionary<string, SunderRpcCSharpOwnedContractIdentity> CompilerIdentities(
        SunderRpcOwnedPlatformBaseline baseline)
        => CompilerIdentities(baseline.Contracts);

    internal static void ValidateCompiledContracts(
        SunderRpcOwnedPlatformBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> contracts)
    {
        _ = WriteOwnedPlatformBaseline(baseline);
        _ = OwnedPlatformCandidatesByKey(baseline.Contracts, contracts, contractKey: null);
        ValidateCompiledContracts(baseline.Contracts, contracts, platform: true);
    }

    internal static byte[] PlanOwnedPlatformBaselineAcceptance(
        SunderRpcOwnedPlatformBaseline? baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string? contractKey,
        string? expectedCurrentVersion,
        string? nextVersion,
        bool acknowledgeBreakingChange)
    {
        // Null explicitly bootstraps a first baseline; an existing empty/invalid baseline is never repaired.
        if (baseline is not null) _ = WriteOwnedPlatformBaseline(baseline);
        var existing = baseline?.Contracts ?? new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal);
        var source = OwnedPlatformCandidatesByKey(existing, candidates, contractKey);
        var contracts = AcceptOwnedContracts(existing, source, contractKey, expectedCurrentVersion, nextVersion, acknowledgeBreakingChange);
        return WriteOwnedPlatformBaseline(new(contracts, baseline?.ExactBytes ?? [], baseline?.Sha256 ?? string.Empty));
    }

    internal static byte[] PlanOwnedPlatformBaselineFreeze(
        SunderRpcOwnedPlatformBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string catalogReleaseVersion,
        string? contractKey)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!SemanticVersion.TryParse(catalogReleaseVersion, out var releaseVersion) || releaseVersion.Prerelease is not null)
        {
            throw new InvalidDataException("Platform release freeze requires an exact stable semantic catalog release version.");
        }
        _ = WriteOwnedPlatformBaseline(baseline);
        var source = OwnedPlatformCandidatesByKey(baseline.Contracts, candidates, contractKey);
        ValidateCompiledContracts(baseline.Contracts, candidates, platform: true);
        foreach (var (key, contract) in baseline.Contracts)
        {
            if (contract.Current is not { State: "draft" } current) continue;
            RequireStableOwnedTransition(key, previousVersion: null, current.Descriptor.Version);
            ValidateOwnedCandidateHistory(key, current.Descriptor, current.Exposure, contract.History, preserveUnchangedPrerelease: false, current.ProjectionReview);
        }
        var contracts = FreezeOwnedContracts(baseline.Contracts, source, contractKey, "catalogRelease",
            JsonSerializer.SerializeToElement(new { authorityId = CorePlatformAuthorityId, releaseVersion = catalogReleaseVersion }),
            ReadPlatformSnapshot);
        return WriteOwnedPlatformBaseline(baseline with { Contracts = contracts });
    }

    private static Dictionary<string, SunderRpcCSharpCompiledContract> OwnedPlatformCandidatesByKey(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string? contractKey)
    {
        var source = OwnedCandidatesByKey(baseline, candidates, contractKey);
        if (source.Values.Any(static candidate => candidate.Exposure != SunderServiceExposure.Public)
            || !source.Values.Any(static candidate => candidate.Descriptor.ContractId == MandatoryPlatformContractId))
        {
            throw new InvalidDataException($"Platform source must be public-only and retain the mandatory current '{MandatoryPlatformContractId}' contract.");
        }
        return source;
    }

    private static SunderRpcOwnedContractSnapshot ReadPlatformSnapshot(
        JsonElement value,
        string contractKey,
        string contractId,
        bool allowDraft)
    {
        var snapshot = ReadOwnedSnapshot(value, contractKey, contractId, allowDraft, "catalogRelease", publicOnly: true);
        var label = $"platform owned contract '{contractKey}' snapshot";
        if (value.TryGetProperty("catalogRelease", out var release))
        {
            RequireObject(release, $"{label} catalog release", ["authorityId", "releaseVersion"], ["authorityId", "releaseVersion"], []);
            RequireString(release, "authorityId", CorePlatformAuthorityId, label);
            var releaseVersion = RequireSemanticVersion(release, "releaseVersion", label);
            if (value.TryGetProperty("publication", out var publication))
            {
                var publicationLabel = $"{label} publication";
                string[] members = ["registryOrigin", "authorityId", "catalogRelease", "catalogContentIdentity", "publishedAtUtc", "etag"];
                RequireObject(publication, publicationLabel, members, members, []);
                RequireCanonicalRegistryOrigin(RequireString(publication, "registryOrigin", publicationLabel), publicationLabel);
                RequireString(publication, "authorityId", CorePlatformAuthorityId, publicationLabel);
                RequireString(publication, "catalogRelease", releaseVersion, publicationLabel);
                _ = RequireSha256(publication, "catalogContentIdentity", publicationLabel);
                RequireCanonicalTimestamp(RequireString(publication, "publishedAtUtc", publicationLabel), publicationLabel);
                RequireString(publication, "etag", $"\"{snapshot.Descriptor.Sha256}\"", publicationLabel);
            }
        }
        return snapshot;
    }
}
