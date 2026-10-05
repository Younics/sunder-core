using System.Text;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    public static byte[] WriteOwnedPackageBaseline(SunderRpcOwnedPackageBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var output = new StringBuilder();
        Line(output, 0, "{");
        IntegerProperty(output, 1, "schemaVersion", 1, comma: true);
        StringProperty(output, 1, "authorityKind", "package", comma: true);
        StringProperty(output, 1, "authorityId", baseline.PackageId, comma: true);
        StringProperty(output, 1, "packageId", baseline.PackageId, comma: true);
        OwnedContractMapProperty(output, baseline.Contracts);
        Line(output, 0, "}");

        var bytes = Utf8NoBom.GetBytes(output.ToString());
        var reparsed = ParseOwnedPackageBaseline(bytes);
        ValidateOwnedSnapshotModels(baseline.Contracts, reparsed.Contracts);
        return bytes;
    }

    private static void OwnedContractMapProperty(
        StringBuilder output,
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> baseline)
    {
        var contracts = baseline.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (contracts.Length == 0)
        {
            Line(output, 1, "\"contracts\": {}");
        }
        else
        {
            Line(output, 1, "\"contracts\": {");
            for (var index = 0; index < contracts.Length; index++)
            {
                var (key, contract) = contracts[index];
                output.Append("    ");
                AppendJsonString(output, key);
                output.Append(": {\n");
                StringProperty(output, 3, "contractId", contract.ContractId, comma: true);
                if (contract.Current is { } current)
                {
                    output.Append("      \"current\": ");
                    AppendDeterministicValue(output, current.SnapshotJson, 3);
                    output.Append(",\n");
                }
                if (contract.History.Count == 0)
                {
                    Line(output, 3, "\"history\": []");
                }
                else
                {
                    Line(output, 3, "\"history\": [");
                    for (var historyIndex = 0; historyIndex < contract.History.Count; historyIndex++)
                    {
                        output.Append("        ");
                        AppendDeterministicValue(output, contract.History[historyIndex].SnapshotJson, 4);
                        output.Append(historyIndex + 1 == contract.History.Count ? "\n" : ",\n");
                    }
                    Line(output, 3, "]");
                }
                Line(output, 2, index + 1 == contracts.Length ? "}" : "},");
            }
            Line(output, 1, "}");
        }
    }

    private static void ValidateOwnedSnapshotModels(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> expected,
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> reparsed)
    {
        foreach (var (key, contract) in expected)
        {
            var actual = reparsed[key];
            if (!OwnedSnapshotModelsAgree(contract.Current, actual.Current)
                || contract.History.Count != actual.History.Count
                || !contract.History.Zip(actual.History).All(static pair => OwnedSnapshotModelsAgree(pair.First, pair.Second)))
            {
                throw new InvalidDataException($"Owned contract '{key}' model does not agree with its retained snapshot JSON.");
            }
        }
    }

    public static byte[] PlanOwnedBaselineAcceptance(
        SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string? contractKey,
        string? expectedCurrentVersion,
        string? nextVersion,
        bool acknowledgeBreakingChange)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        _ = WriteOwnedPackageBaseline(baseline);
        var source = OwnedCandidatesByKey(baseline.Contracts, candidates, contractKey);
        var contracts = AcceptOwnedContracts(baseline.Contracts, source, contractKey, expectedCurrentVersion, nextVersion, acknowledgeBreakingChange);
        return WriteOwnedPackageBaseline(baseline with { Contracts = contracts });
    }

    private static IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> AcceptOwnedContracts(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> baseline,
        IReadOnlyDictionary<string, SunderRpcCSharpCompiledContract> source,
        string? contractKey,
        string? expectedCurrentVersion,
        string? nextVersion,
        bool acknowledgeBreakingChange)
    {
        if (expectedCurrentVersion is not null && !SemanticVersion.TryParse(expectedCurrentVersion, out _)
            || nextVersion is not null && !SemanticVersion.TryParse(nextVersion, out _))
        {
            throw new InvalidDataException("Owned baseline acceptance requires exact semantic contract versions.");
        }
        if (contractKey is null && (expectedCurrentVersion is not null || nextVersion is not null || acknowledgeBreakingChange)
            || acknowledgeBreakingChange && (expectedCurrentVersion is null || nextVersion is null))
        {
            throw new InvalidDataException("A version override or breaking-change acknowledgement requires one exact contract key and an exact old/new version pair.");
        }
        if (contractKey is not null)
        {
            var otherActive = baseline.Where(pair => pair.Key != contractKey && pair.Value.Current is not null).ToArray();
            if (source.Keys.Count(key => key != contractKey) != otherActive.Length
                || otherActive.Any(pair => !source.TryGetValue(pair.Key, out var candidate)
                                           || !OwnedSourceMatches(pair.Value.Current!, candidate)))
            {
                throw new InvalidDataException("Targeted acceptance requires the complete exact source key set and unchanged snapshots outside the selected contract key.");
            }
        }

        var contracts = baseline.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        string[] keys = contractKey is null
            ? baseline.Keys.Union(source.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : [contractKey];
        foreach (var key in keys)
        {
            baseline.TryGetValue(key, out var contract);
            source.TryGetValue(key, out var candidate);
            var current = contract?.Current;
            var previous = current ?? contract?.History.LastOrDefault();
            if (expectedCurrentVersion is not null && expectedCurrentVersion != previous?.Descriptor.Version)
            {
                throw new InvalidDataException($"Owned contract '{key}' does not have the expected current version '{expectedCurrentVersion}'.");
            }
            if (previous is not null && (expectedCurrentVersion is null) != (nextVersion is null))
            {
                throw new InvalidDataException($"Owned contract '{key}' version selection requires both expectedCurrentVersion and nextVersion.");
            }

            if (candidate is null)
            {
                if (current is null) continue;
                RequireOwnedBreakingAcknowledgement(key, contractKey, expectedCurrentVersion, nextVersion, acknowledgeBreakingChange);
                RequireStableOwnedTransition(key, current.Descriptor.Version, nextVersion!);
                var minimum = SemanticVersion.Parse(current.Descriptor.Version).NextMajor();
                if (SemanticVersion.Parse(nextVersion!).ComparePrecedenceTo(minimum) < 0)
                {
                    throw new InvalidDataException($"Removing owned contract '{key}' requires a reviewed next version of at least '{minimum}'.");
                }
                var history = current.State == "draft" ? contract!.History : contract!.History.Append(current).ToArray();
                if (history.Count == 0) contracts.Remove(key);
                else contracts[key] = contract! with { Current = null, History = Array.AsReadOnly(history.ToArray()) };
                continue;
            }

            if (current is not null && OwnedSourceMatches(current, candidate)
                && (nextVersion is null || nextVersion == current.Descriptor.Version))
            {
                if (current.State == "draft")
                {
                    ValidateOwnedCandidateHistory(key, current.Descriptor, current.Exposure, contract!.History, preserveUnchangedPrerelease: true);
                }
                continue;
            }

            var exposure = Exposure(candidate.Exposure);
            var version = nextVersion ?? current?.Descriptor.Version ?? "1.0.0";
            RequireStableOwnedTransition(key, previous?.Descriptor.Version, version);
            var kind = previous is null ? SunderRpcContractChangeKind.Unchanged : ClassifyOwnedChange(previous, candidate);
            if (previous is not null)
            {
                var previousVersion = SemanticVersion.Parse(previous.Descriptor.Version);
                if (kind == SunderRpcContractChangeKind.Major
                    || contract!.History.Any(snapshot => SemanticVersion.Parse(snapshot.Descriptor.Version).Major == previousVersion.Major
                                                         && ClassifyOwnedChange(snapshot, candidate) == SunderRpcContractChangeKind.Major))
                {
                    RequireOwnedBreakingAcknowledgement(key, contractKey, expectedCurrentVersion, nextVersion, acknowledgeBreakingChange);
                }
            }

            var immutableHistory = contract?.History ?? Array.Empty<SunderRpcOwnedContractSnapshot>();
            if (current is not null && current.State != "draft") immutableHistory = immutableHistory.Append(current).ToArray();
            if (nextVersion is null && previous is not null)
            {
                var minimum = SemanticVersion.Parse(current is { State: "draft" }
                    ? current.Descriptor.Version
                    : GetMinimumOwnedContractVersion(previous.Descriptor.Version,
                        kind == SunderRpcContractChangeKind.Unchanged ? SunderRpcContractChangeKind.Patch : kind));
                var major = minimum.Major;
                foreach (var snapshot in immutableHistory)
                {
                    var historicalVersion = SemanticVersion.Parse(snapshot.Descriptor.Version);
                    if (historicalVersion.Major != major && !ReferenceEquals(snapshot, immutableHistory[^1])) continue;
                    // The final-version validator rejects any still-applicable undefined prerelease progression.
                    if (historicalVersion.Prerelease is not null) continue;
                    var historicalKind = ClassifyOwnedChange(snapshot, candidate);
                    var floor = SemanticVersion.Parse(GetMinimumOwnedContractVersion(snapshot.Descriptor.Version,
                        historicalKind == SunderRpcContractChangeKind.Unchanged ? SunderRpcContractChangeKind.Patch : historicalKind));
                    if (floor.ComparePrecedenceTo(minimum) > 0) minimum = floor;
                }
                version = minimum.ToString();
            }
            var descriptor = OwnedDescriptorAtVersion(candidate.Descriptor, version);
            ValidateOwnedCandidateHistory(key, descriptor, exposure, immutableHistory, preserveUnchangedPrerelease: false);
            contracts[key] = new(
                descriptor.ContractId,
                CreateOwnedDraftSnapshot(exposure, descriptor),
                Array.AsReadOnly(immutableHistory.ToArray()));
        }
        return contracts;
    }

    public static string GetMinimumOwnedContractVersion(string previousVersion, SunderRpcContractChangeKind kind)
    {
        var previous = SemanticVersion.Parse(previousVersion);
        while (true)
        {
            try
            {
                return SunderRpcContractCompatibility.GetMinimumVersion(previousVersion, kind);
            }
            catch (InvalidOperationException exception) when (previous.Prerelease is null)
            {
                if (kind == SunderRpcContractChangeKind.Patch) kind = SunderRpcContractChangeKind.Minor;
                else if (kind == SunderRpcContractChangeKind.Minor) kind = SunderRpcContractChangeKind.Major;
                else throw new InvalidOperationException(
                    $"contract-version.version-limit-exceeded: No required stable bump from '{previousVersion}' fits the {SemanticVersion.MaximumLength}-character version limit. "
                    + "Retain the existing tuple or review a new contract identity before accepting this change.", exception);
            }
        }
    }

    public static byte[] PlanOwnedBaselineFreeze(
        SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string packageVersion,
        string? contractKey)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!SemanticVersion.TryParse(packageVersion, out _))
        {
            throw new InvalidDataException("Release freeze requires an exact semantic package version.");
        }
        _ = WriteOwnedPackageBaseline(baseline);
        var source = OwnedCandidatesByKey(baseline.Contracts, candidates, contractKey);
        var contracts = FreezeOwnedContracts(baseline.Contracts, source, contractKey, "packageRelease",
            JsonSerializer.SerializeToElement(new { packageId = baseline.PackageId, packageVersion }),
            (value, key, id, allowDraft) => ReadPackageSnapshot(value, key, id, baseline.PackageId, allowDraft));
        return WriteOwnedPackageBaseline(baseline with { Contracts = contracts });
    }

    private static IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> FreezeOwnedContracts(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> baseline,
        IReadOnlyDictionary<string, SunderRpcCSharpCompiledContract> source,
        string? contractKey,
        string releaseProperty,
        JsonElement release,
        Func<JsonElement, string, string, bool, SunderRpcOwnedContractSnapshot> readSnapshot)
    {
        var active = baseline.Where(static pair => pair.Value.Current is not null).ToArray();
        if (source.Count != active.Length
            || active.Any(pair => !source.TryGetValue(pair.Key, out var candidate)
                                  || !OwnedSourceMatches(pair.Value.Current!, candidate)))
        {
            throw new InvalidDataException("Release freeze requires source keys, exposure, and exact current descriptor/ABI bytes to match the owned baseline.");
        }
        if (contractKey is not null && !active.Any(pair => pair.Key == contractKey))
        {
            throw new InvalidDataException($"Owned contract '{contractKey}' has no current snapshot to freeze.");
        }

        var contracts = baseline.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        foreach (var (key, contract) in active.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (contractKey is not null && key != contractKey) continue;
            var current = contract.Current!;
            if (current.State != "draft")
            {
                // Later releases can reuse the exact contract without changing its original release evidence.
                continue;
            }
            ValidateOwnedCandidateHistory(key, current.Descriptor, current.Exposure, contract.History, preserveUnchangedPrerelease: false);
            var output = new StringBuilder();
            Line(output, 0, "{");
            foreach (var property in current.SnapshotJson.EnumerateObject())
            {
                if (property.Name == "state")
                {
                    StringProperty(output, 1, "state", "release-frozen", comma: true);
                    continue;
                }
                output.Append("  ");
                AppendJsonString(output, property.Name);
                output.Append(": ");
                AppendDeterministicValue(output, property.Value, 1);
                output.Append(",\n");
            }
            output.Append("  ");
            AppendJsonString(output, releaseProperty);
            output.Append(": ");
            AppendDeterministicValue(output, release, 1);
            output.Append('\n');
            Line(output, 0, "}");
            using var document = ParseStrictDocument(Utf8NoBom.GetBytes(output.ToString()), MaximumOwnedBaselineBytes, "owned release freeze");
            contracts[key] = contract with
            {
                Current = readSnapshot(document.RootElement, key, contract.ContractId, false),
            };
        }
        return contracts;
    }

    private static Dictionary<string, SunderRpcCSharpCompiledContract> OwnedCandidatesByKey(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> candidates,
        string? contractKey)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (contractKey is not null && !IsPortableKey(contractKey))
        {
            throw new InvalidDataException("The selected owned contract key must be an exact portable source key.");
        }
        if (candidates.Count > 128)
        {
            throw new InvalidDataException("The source exceeds the 128-contract owned baseline limit.");
        }
        var result = new Dictionary<string, SunderRpcCSharpCompiledContract>(StringComparer.Ordinal);
        var foldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var existingIdentities = baseline.ToDictionary(static pair => pair.Value.ContractId, static pair => pair.Key, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!IsPortableKey(candidate.ContractKey)
                || !foldedKeys.Add(candidate.ContractKey)
                || !identities.Add(candidate.Descriptor.ContractId)
                || candidate.Exposure is not (SunderRpcExposure.Package or SunderRpcExposure.Public))
            {
                throw new InvalidDataException($"Source owned contract '{candidate.ContractKey}' has an invalid or duplicate key, identity, or exposure.");
            }
            if (baseline.TryGetValue(candidate.ContractKey, out var existing)
                && existing.ContractId != candidate.Descriptor.ContractId)
            {
                throw new InvalidDataException($"Owned contract '{candidate.ContractKey}' cannot change its stable contract identity.");
            }
            if (existingIdentities.TryGetValue(candidate.Descriptor.ContractId, out var existingKey)
                && existingKey != candidate.ContractKey)
            {
                throw new InvalidDataException($"Owned contract '{existingKey}' cannot be aliased or renamed to '{candidate.ContractKey}'; review a separate removal and addition.");
            }
            result.Add(candidate.ContractKey, candidate);
        }
        if (contractKey is not null && !baseline.Keys.Contains(contractKey, StringComparer.Ordinal) && !result.ContainsKey(contractKey))
        {
            throw new InvalidDataException($"Owned contract key '{contractKey}' is not present in the baseline or source.");
        }
        return result;
    }

    private static bool OwnedSourceMatches(SunderRpcOwnedContractSnapshot snapshot, SunderRpcCSharpCompiledContract candidate)
        => snapshot.Exposure == Exposure(candidate.Exposure)
           && snapshot.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(candidate.Descriptor.GetCanonicalUtf8())
           && snapshot.Descriptor.GetCanonicalAbiProjectionUtf8().AsSpan().SequenceEqual(candidate.Descriptor.GetCanonicalAbiProjectionUtf8());

    private static bool OwnedSnapshotModelsAgree(SunderRpcOwnedContractSnapshot? expected, SunderRpcOwnedContractSnapshot? actual)
        => expected is null ? actual is null
            : actual is not null && expected.State == actual.State && expected.Exposure == actual.Exposure
              && expected.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(actual.Descriptor.GetCanonicalUtf8())
              && expected.Descriptor.GetCanonicalAbiProjectionUtf8().AsSpan().SequenceEqual(actual.Descriptor.GetCanonicalAbiProjectionUtf8());

    private static SunderRpcContractChangeKind ClassifyOwnedChange(SunderRpcOwnedContractSnapshot previous, SunderRpcCSharpCompiledContract candidate)
    {
        // Exposure is outside the descriptor classifier and remains conservatively Major in either direction.
        if (previous.Exposure != Exposure(candidate.Exposure)) return SunderRpcContractChangeKind.Major;
        var kind = SunderRpcContractCompatibility.Classify(previous.Descriptor, candidate.Descriptor).Kind;
        return !Enum.IsDefined(kind)
               || kind == SunderRpcContractChangeKind.Unchanged
               && !previous.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(candidate.Descriptor.GetCanonicalUtf8())
            ? SunderRpcContractChangeKind.Major : kind;
    }

    private static void RequireOwnedBreakingAcknowledgement(
        string key,
        string? selectedKey,
        string? expectedCurrentVersion,
        string? nextVersion,
        bool acknowledged)
    {
        if (selectedKey != key || expectedCurrentVersion is null || nextVersion is null || !acknowledged)
        {
            throw new InvalidDataException($"Breaking, unclassified, exposure, or removal change to owned contract '{key}' requires its exact contract key, old/new versions, and an explicit breaking-change acknowledgement.");
        }
    }

    private static void RequireStableOwnedTransition(string key, string? previousVersion, string nextVersion)
    {
        if (SemanticVersion.Parse(nextVersion).Prerelease is not null
            || previousVersion is not null && SemanticVersion.Parse(previousVersion).Prerelease is not null)
        {
            throw new InvalidDataException($"Owned contract '{key}' cannot allocate a changed prerelease baseline; prerelease progression policy is not defined.");
        }
    }

    private static void ValidateOwnedCandidateHistory(
        string key,
        SunderRpcContractDescriptor candidate,
        string exposure,
        IReadOnlyList<SunderRpcOwnedContractSnapshot> immutableHistory,
        bool preserveUnchangedPrerelease)
    {
        if (immutableHistory.Count == 0) return;
        var version = SemanticVersion.Parse(candidate.Version);
        var highest = immutableHistory[^1];
        var comparisons = immutableHistory.Where(snapshot => ReferenceEquals(snapshot, highest)
            || SemanticVersion.Parse(snapshot.Descriptor.Version).Major == version.Major).ToArray();
        if (version.Prerelease is not null || comparisons.Any(static snapshot => SemanticVersion.Parse(snapshot.Descriptor.Version).Prerelease is not null))
        {
            // Retaining an exact tuple does not allocate or infer a prerelease progression.
            if (preserveUnchangedPrerelease) return;
            throw new InvalidDataException($"Owned contract '{key}' needs an undefined prerelease history progression policy.");
        }
        if (version.ComparePrecedenceTo(SemanticVersion.Parse(highest.Descriptor.Version)) <= 0)
        {
            throw new InvalidDataException($"Owned contract '{key}' next version must follow all immutable history; issued versions cannot be reused, even with matching hashes.");
        }
        var errors = SunderRpcContractCompatibility.ValidateVersion(candidate, immutableHistory.Select(static snapshot => snapshot.Descriptor).ToArray());
        if (errors.Count != 0)
        {
            throw new InvalidDataException($"Owned contract '{key}' does not satisfy immutable history: {string.Join("; ", errors)}");
        }
        foreach (var snapshot in comparisons)
        {
            if (snapshot.Exposure != exposure && version.ComparePrecedenceTo(SemanticVersion.Parse(snapshot.Descriptor.Version).NextMajor()) < 0)
            {
                throw new InvalidDataException($"Owned contract '{key}' exposure change is Major relative to immutable history '{snapshot.Descriptor.Version}'.");
            }
        }
    }

    private static SunderRpcOwnedContractSnapshot CreateOwnedDraftSnapshot(
        string exposure,
        SunderRpcContractDescriptor descriptor)
    {
        var descriptorBytes = descriptor.GetCanonicalUtf8();
        var abiBytes = descriptor.GetCanonicalAbiProjectionUtf8();
        var output = new StringBuilder();
        Line(output, 0, "{");
        StringProperty(output, 1, "state", "draft", comma: true);
        StringProperty(output, 1, "exposure", exposure, comma: true);
        StringProperty(output, 1, "contractVersion", descriptor.Version, comma: true);
        IntegerProperty(output, 1, "descriptorBytes", descriptorBytes.Length, comma: true);
        StringProperty(output, 1, "canonicalDescriptor", StrictUtf8.GetString(descriptorBytes), comma: true);
        StringProperty(output, 1, "descriptorSha256", descriptor.Sha256, comma: true);
        IntegerProperty(output, 1, "abiProjectionBytes", abiBytes.Length, comma: true);
        StringProperty(output, 1, "canonicalAbiProjection", StrictUtf8.GetString(abiBytes), comma: true);
        StringProperty(output, 1, "abiSha256", descriptor.AbiSha256, comma: false);
        Line(output, 0, "}");
        using var document = ParseStrictDocument(Utf8NoBom.GetBytes(output.ToString()), MaximumOwnedBaselineBytes, "owned draft snapshot");
        return new("draft", exposure, descriptor, document.RootElement.Clone());
    }

    private static SunderRpcContractDescriptor OwnedDescriptorAtVersion(SunderRpcContractDescriptor descriptor, string version)
    {
        if (descriptor.Version == version) return descriptor;
        using var document = JsonDocument.Parse(descriptor.GetCanonicalUtf8(), new JsonDocumentOptions { MaxDepth = 256 });
        var output = new StringBuilder("{");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (output.Length > 1) output.Append(',');
            AppendJsonString(output, property.Name);
            output.Append(':');
            if (property.Name == "version") AppendJsonString(output, version);
            else output.Append(property.Value.GetRawText());
        }
        output.Append('}');
        return SunderRpcContractDescriptor.Parse(Utf8NoBom.GetBytes(output.ToString()));
    }
}
