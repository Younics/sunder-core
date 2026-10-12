using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    internal static void ValidatePrivatePackageVersion(string version)
    {
        if (version.Length is 0 or > 256 || !Sunder.Sdk.Packaging.SemanticVersion.TryParse(version, out _))
            throw new InvalidDataException($"rpc-authoring.private-package-version-invalid: Effective package version '{version}' must be strict full SemVer of at most 256 characters.");
    }

    internal static bool HasPublicEvidence(SunderRpcOwnedContractBaseline contract)
        => contract.Current?.Exposure == "public" || contract.History.Any(snapshot => snapshot.Exposure == "public");

    internal static void ValidatePrivateArchiveTransition(SunderRpcOwnedPackageBaseline before, SunderRpcOwnedPackageBaseline after)
    {
        if (before.PackageId != after.PackageId) throw new InvalidDataException("rpc-authoring.private-retirement-mutated: The baseline package identity changed.");
        if (before.HasRetiredPrivateContracts && !after.HasRetiredPrivateContracts)
            throw new InvalidDataException("rpc-authoring.private-retirement-mutated: Preserve the existing private archive, including an explicit empty map.");
        foreach (var (key, archived) in before.RetiredPrivateContracts)
            if (!after.RetiredPrivateContracts.TryGetValue(key, out var retained) || !SameRecord(archived, retained))
                throw new InvalidDataException($"rpc-authoring.private-retirement-mutated: Archived '{key}' cannot be removed, changed or enriched.");
        foreach (var (key, added) in after.RetiredPrivateContracts.Where(pair => !before.RetiredPrivateContracts.ContainsKey(pair.Key)))
            if (!before.Contracts.TryGetValue(key, out var original) || after.Contracts.ContainsKey(key) || HasPublicEvidence(original) || !SameRecord(original, added))
                throw new InvalidDataException($"rpc-authoring.private-retirement-mutated: Archived '{key}' must be the exact removed private before-image.");
    }

    internal static byte[] PlanPrivateBaselineRetirement(SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> source, string? selectedKey)
    {
        _ = WriteOwnedPackageBaseline(baseline); // Validate every current/history/archive record first.
        var active = baseline.Contracts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var retired = baseline.RetiredPrivateContracts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var keys = selectedKey is null ? baseline.Contracts.Where(pair => !HasPublicEvidence(pair.Value)).Select(pair => pair.Key).ToArray() : [selectedKey];
        foreach (var key in keys)
        {
            if (!active.TryGetValue(key, out var entry))
            {
                if (retired.ContainsKey(key)) continue;
                throw new InvalidDataException($"rpc-authoring.private-retirement-conflict: Contract key '{key}' does not exist.");
            }
            if (HasPublicEvidence(entry) || source.Any(contract => contract.Exposure == SunderServiceExposure.Public
                    && (contract.ContractKey == key || contract.Descriptor.ContractId == entry.ContractId)))
                throw new InvalidDataException($"rpc-authoring.private-retirement-public-evidence: '{key}' has public current/history/source evidence; use the explicit public lifecycle.");
            if (retired.ContainsKey(key) || retired.Values.Any(contract => contract.ContractId == entry.ContractId))
                throw new InvalidDataException($"rpc-authoring.private-retirement-conflict: The archive already contains key/identity '{key}' / '{entry.ContractId}'; no overwrite or merge is permitted.");
            retired.Add(key, entry); active.Remove(key);
        }
        if (active.Count == baseline.Contracts.Count) return baseline.ExactBytes;
        return WriteOwnedPackageBaseline(baseline with { Contracts = active, RetiredPrivateContracts = retired, HasRetiredPrivateContracts = true });
    }

    private static bool SameRecord(SunderRpcOwnedContractBaseline left, SunderRpcOwnedContractBaseline right)
        => left.ContractId == right.ContractId && SameSnapshot(left.Current, right.Current)
            && left.History.Count == right.History.Count && left.History.Zip(right.History).All(pair => SameSnapshot(pair.First, pair.Second));

    private static bool SameSnapshot(SunderRpcOwnedContractSnapshot? left, SunderRpcOwnedContractSnapshot? right)
        => left is null ? right is null : right is not null && JsonElement.DeepEquals(left.SnapshotJson, right.SnapshotJson)
            && left.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(right.Descriptor.GetCanonicalUtf8());
}
