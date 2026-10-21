using System.Text.Json;

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
        if (after.RetiredPrivateContracts.Keys.Except(before.RetiredPrivateContracts.Keys, StringComparer.Ordinal).Any())
            throw new InvalidDataException("rpc-authoring.private-retirement-mutated: The retained private archive cannot gain entries.");
    }

    private static bool SameRecord(SunderRpcOwnedContractBaseline left, SunderRpcOwnedContractBaseline right)
        => left.ContractId == right.ContractId && SameSnapshot(left.Current, right.Current)
            && left.History.Count == right.History.Count && left.History.Zip(right.History).All(pair => SameSnapshot(pair.First, pair.Second));

    private static bool SameSnapshot(SunderRpcOwnedContractSnapshot? left, SunderRpcOwnedContractSnapshot? right)
        => left is null ? right is null : right is not null && JsonElement.DeepEquals(left.SnapshotJson, right.SnapshotJson)
            && left.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(right.Descriptor.GetCanonicalUtf8());
}
