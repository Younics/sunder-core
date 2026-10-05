using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcOwnedContractSnapshot(
    string State,
    string Exposure,
    SunderRpcContractDescriptor Descriptor,
    JsonElement SnapshotJson);

internal sealed record SunderRpcOwnedContractBaseline(
    string ContractId,
    SunderRpcOwnedContractSnapshot? Current,
    IReadOnlyList<SunderRpcOwnedContractSnapshot> History);

internal sealed record SunderRpcOwnedPackageBaseline(
    string PackageId,
    IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> Contracts,
    byte[] ExactBytes,
    string Sha256);

internal sealed record SunderRpcPackageProjectExportContract(
    string ContractKey,
    SunderRpcExposure Exposure,
    string GeneratedLogicalName,
    SunderRpcContractDescriptor Descriptor)
{
    internal SunderRpcOwnedUsePolicy? AppUse { get; init; }
    internal SunderRpcOwnedUsePolicy? RuntimeUse { get; init; }
}

internal sealed record SunderRpcPackageProjectExport(
    string InputFingerprint,
    string LockSha256,
    string PackageId,
    IReadOnlyList<SunderRpcPackageProjectExportContract> Contracts,
    string Sha256);

internal static partial class SunderRpcAuthoringDocuments
{
    internal const string PackageExportFileName = "package-export.v1.json";
    internal const string AuthoringReceiptFileName = "authoring-receipt.v1.json";
    internal const string AppAuthoringReceiptFileName = "app-authoring-receipt.v1.json";
    internal const string OwnedContractsFileName = "sunder-contracts.owned.json";
    internal const string ContractLockFileName = "sunder-contracts.lock.json";

    private const int MaximumOwnedBaselineBytes = 268_435_456;
    private const int MaximumContractLockBytes = 33_554_432;
    private const int MaximumGeneratedDocumentBytes = 67_108_864;
    private const int MaximumOuterDepth = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    public static SunderRpcOwnedPackageBaseline ReadOwnedPackageBaseline(string path)
        => ParseOwnedPackageBaseline(SunderRpcDescriptorFileReader.ReadStableBytes(
            path, MaximumOwnedBaselineBytes, "Sunder owned-contract baseline"));

    public static SunderRpcOwnedPackageBaseline ParseOwnedPackageBaseline(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var root = ParseStrictDocument(bytes, MaximumOwnedBaselineBytes, "Sunder owned-contract baseline");
        using (root)
        {
            var document = root.RootElement;
            RequireObject(
                document,
                "owned-contract baseline",
                ["schemaVersion", "authorityKind", "authorityId", "packageId", "contracts"],
                ["schemaVersion", "authorityKind", "authorityId", "contracts"],
                ["packageId"]);
            RequireInteger(document, "schemaVersion", 1, "owned-contract baseline");
            RequireString(document, "authorityKind", "package", "owned-contract baseline");
            var authorityId = RequirePackageId(document, "authorityId", "owned-contract baseline");
            var packageId = RequirePackageId(document, "packageId", "owned-contract baseline");
            if (!string.Equals(authorityId, packageId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The owned-contract baseline authority '{authorityId}' does not match package '{packageId}'.");
            }

            var contracts = ReadOwnedContractMap(document,
                (value, key, id, allowDraft) => ReadPackageSnapshot(value, key, id, packageId, allowDraft));

            return new(
                packageId,
                contracts,
                bytes.ToArray(),
                LowerSha256(bytes));
        }
    }

    public static SunderRpcPackageProjectExport ReadPackageProjectExport(
        string path,
        string expectedProducerVersion,
        string expectedProjectIdentity,
        string expectedConfiguration,
        string expectedTargetFramework,
        string expectedPackageId)
    {
        var (bytes, root) = ReadStrictDocument(path, MaximumGeneratedDocumentBytes, "Sunder package project export");
        string inputFingerprint;
        string lockSha256;
        var exportedContracts = new List<SunderRpcPackageProjectExportContract>();
        using (root)
        {
            var document = root.RootElement;
            RequireObject(
                document,
                "package project export",
                [
                    "schemaVersion",
                    "producer",
                    "projectIdentity",
                    "configuration",
                    "targetFramework",
                    "inputFingerprint",
                    "lockSha256",
                    "packageId",
                    "contracts",
                ],
                [
                    "schemaVersion",
                    "producer",
                    "projectIdentity",
                    "configuration",
                    "targetFramework",
                    "inputFingerprint",
                    "lockSha256",
                    "packageId",
                    "contracts",
                ],
                []);
            RequireInteger(document, "schemaVersion", 1, "package project export");
            var producer = RequireProperty(document, "producer", JsonValueKind.Object, "package project export");
            RequireObject(
                producer,
                "package project export producer",
                ["name", "version", "language"],
                ["name", "version", "language"],
                []);
            RequireString(producer, "name", "Sunder.Package.Build", "package project export producer");
            RequireString(producer, "version", expectedProducerVersion, "package project export producer");
            _ = RequireSemanticVersion(producer, "version", "package project export producer");
            RequireString(producer, "language", "csharp", "package project export producer");
            RequireString(document, "projectIdentity", expectedProjectIdentity, "package project export");
            RequireString(document, "configuration", expectedConfiguration, "package project export");
            RequireString(document, "targetFramework", expectedTargetFramework, "package project export");
            inputFingerprint = RequireSha256(document, "inputFingerprint", "package project export");
            lockSha256 = RequireSha256(document, "lockSha256", "package project export");
            RequireString(document, "packageId", expectedPackageId, "package project export");
            _ = RequirePackageId(document, "packageId", "package project export");

            var contracts = RequireProperty(document, "contracts", JsonValueKind.Array, "package project export")
                .EnumerateArray()
                .ToArray();
            if (contracts.Length > 128)
            {
                throw new InvalidDataException(
                    $"The package project export exceeds the 128-contract limit.");
            }
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
            string? previousKey = null;
            foreach (var value in contracts)
            {
                const string label = "package project export contract";
                RequireObject(
                    value,
                    label,
                    [
                        "contractKey",
                        "contractId",
                        "contractVersion",
                        "exposure",
                        "source",
                        "authorityKind",
                        "authorityId",
                        "descriptorBytes",
                        "canonicalDescriptor",
                        "descriptorSha256",
                        "abiSha256",
                        "appUse",
                        "runtimeUse",
                    ],
                    [
                        "contractKey",
                        "contractId",
                        "contractVersion",
                        "exposure",
                        "source",
                        "authorityKind",
                        "authorityId",
                        "descriptorBytes",
                        "canonicalDescriptor",
                        "descriptorSha256",
                        "abiSha256",
                    ],
                    ["appUse", "runtimeUse"]);
                var contractKey = RequireString(value, "contractKey", label);
                if (!IsPortableKey(contractKey)
                    || previousKey is not null && string.CompareOrdinal(previousKey, contractKey) >= 0
                    || !seenKeys.Add(contractKey))
                {
                    throw new InvalidDataException(
                        $"The package project export contract key '{contractKey}' is invalid, duplicated, unordered, or unexpected.");
                }
                previousKey = contractKey;
                if (!SunderRpcCSharpNaming.TryCreate(contractKey, null, out var generatedName))
                {
                    throw new InvalidDataException(
                        $"The package project export contract key '{contractKey}' cannot produce a generated C# name.");
                }
                var contractId = RequirePackageId(value, "contractId", label);
                var contractVersion = RequireSemanticVersion(value, "contractVersion", label);
                if (!seenIdentities.Add(contractId + "\0" + contractVersion))
                {
                    throw new InvalidDataException(
                        $"The package project export repeats contract identity '{contractId}' version '{contractVersion}'.");
                }
                var exposureText = RequireString(value, "exposure", label);
                var exposure = exposureText switch
                {
                    "package" => SunderRpcExposure.Package,
                    "public" => SunderRpcExposure.Public,
                    _ => (SunderRpcExposure)(-1),
                };
                if (!Enum.IsDefined(exposure))
                {
                    throw new InvalidDataException($"The package project export contract exposure '{exposureText}' is invalid.");
                }
                RequireString(value, "source", "owned", label);
                RequireString(value, "authorityKind", "package", label);
                RequireString(value, "authorityId", expectedPackageId, label);
                var canonicalDescriptor = RequireString(value, "canonicalDescriptor", label);
                var canonicalUtf8 = Utf8NoBom.GetBytes(canonicalDescriptor);
                if (RequireSafeInteger(value, "descriptorBytes", label) != canonicalUtf8.Length
                    || canonicalUtf8.Length is < 2 or > 4_194_304)
                {
                    throw new InvalidDataException(
                        $"The package project export contract '{contractKey}' descriptor byte count is invalid.");
                }
                SunderRpcContractDescriptor descriptor;
                try
                {
                    descriptor = SunderRpcContractDescriptor.Parse(canonicalUtf8);
                }
                catch (SunderRpcDescriptorException exception)
                {
                    throw new InvalidDataException(
                        $"The package project export contract '{contractKey}' descriptor is invalid: {exception.Message}",
                        exception);
                }
                if (!canonicalUtf8.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8())
                    || !string.Equals(contractId, descriptor.ContractId, StringComparison.Ordinal)
                    || !string.Equals(contractVersion, descriptor.Version, StringComparison.Ordinal)
                    || !string.Equals(RequireSha256(value, "descriptorSha256", label), descriptor.Sha256, StringComparison.Ordinal)
                    || !string.Equals(RequireSha256(value, "abiSha256", label), descriptor.AbiSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The package project export contract '{contractKey}' canonical identity is invalid.");
                }
                exportedContracts.Add(
                    new(contractKey, exposure, $"Sunder.Generated.Rpc.{generatedName!.Pascal}", descriptor)
                    {
                        AppUse = ReadUsePolicy(value, descriptor, "app"),
                        RuntimeUse = ReadUsePolicy(value, descriptor, "runtime"),
                    });
            }
        }
        return new(inputFingerprint, lockSha256, expectedPackageId, exportedContracts, LowerSha256(bytes));
    }

    public static string ValidatePackageProjectExport(
        string path,
        string expectedProducerVersion,
        string expectedProjectIdentity,
        string expectedConfiguration,
        string expectedTargetFramework,
        string expectedPackageId,
        string expectedLockSha256,
        IReadOnlyList<SunderRpcCSharpCompiledContract> expectedContracts)
    {
        var export = ReadPackageProjectExport(
            path,
            expectedProducerVersion,
            expectedProjectIdentity,
            expectedConfiguration,
            expectedTargetFramework,
            expectedPackageId);
        if (!string.Equals(export.LockSha256, expectedLockSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact-RID contract lock does not match the project-neutral RPC export.");
        }
        if (export.Contracts.Count != expectedContracts.Count)
        {
            throw new InvalidDataException(
                $"The package project export has {export.Contracts.Count} contract(s), but the exact-RID source has "
                + $"{expectedContracts.Count} contract(s).");
        }
        var exportedByKey = export.Contracts.ToDictionary(
            static contract => contract.ContractKey,
            StringComparer.Ordinal);
        foreach (var expected in expectedContracts)
        {
            if (!exportedByKey.TryGetValue(expected.ContractKey, out var actual)
                || actual.Exposure != expected.Exposure
                || !SameUsePolicy(actual.AppUse, expected.AppUse)
                || !SameUsePolicy(actual.RuntimeUse, expected.RuntimeUse)
                || !expected.Descriptor.GetCanonicalUtf8().AsSpan()
                    .SequenceEqual(actual.Descriptor.GetCanonicalUtf8())
                || !expected.Descriptor.GetCanonicalAbiProjectionUtf8().AsSpan()
                    .SequenceEqual(actual.Descriptor.GetCanonicalAbiProjectionUtf8()))
            {
                throw new InvalidDataException(
                    $"The exact-RID source contract '{expected.ContractKey}' does not match the project-neutral export.");
            }
        }
        return export.Sha256;
    }

    public static IReadOnlyDictionary<string, SunderRpcCSharpOwnedContractIdentity> CompilerIdentities(
        SunderRpcOwnedPackageBaseline baseline)
        => CompilerIdentities(baseline.Contracts);

    private static IReadOnlyDictionary<string, SunderRpcCSharpOwnedContractIdentity> CompilerIdentities(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> contracts)
        => contracts
            .Where(static pair => pair.Value.Current is not null)
            .ToDictionary(
                static pair => pair.Key,
                static pair => new SunderRpcCSharpOwnedContractIdentity(
                    pair.Value.ContractId,
                    pair.Value.Current!.Descriptor.Version),
                StringComparer.Ordinal);

    public static void ValidateCompiledContracts(
        SunderRpcOwnedPackageBaseline baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> contracts)
        => ValidateCompiledContracts(baseline.Contracts, contracts, platform: false);

    private static void ValidateCompiledContracts(
        IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> contracts,
        bool platform)
    {
        var current = baseline
            .Where(static pair => pair.Value.Current is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        if (current.Count != contracts.Count)
        {
            var sourceKeys = contracts.Select(static contract => contract.ContractKey).ToHashSet(StringComparer.Ordinal);
            var removed = current.Keys.Where(key => !sourceKeys.Contains(key)).Order(StringComparer.Ordinal).FirstOrDefault();
            var guidance = removed is not null
                ? DescribeOwnedDrift(removed, current[removed].Current!, null, current[removed].History, platform)
                : platform
                    ? "New contracts start at 1.0.0. Review the complete source set through Core platform baseline acceptance."
                    : "New contracts start at 1.0.0. Run 'dotnet msbuild -t:AcceptSunderContracts' to review the complete source set.";
            throw new InvalidDataException(
                $"rpc-authoring.baseline-drift: Source declares {contracts.Count} RPC contract(s), but the owned baseline has {current.Count} active contract(s). "
                + guidance);
        }

        foreach (var contract in contracts.OrderBy(static item => item.ContractKey, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(contract.ContractKey, out var accepted))
            {
                throw new InvalidDataException(
                    $"rpc-authoring.baseline-drift: Source RPC contract key '{contract.ContractKey}' is not active in the owned baseline. "
                    + (platform
                        ? "Review additions and removals through Core platform baseline acceptance; local keys are never inferred as aliases."
                        : "Run 'dotnet msbuild -t:AcceptSunderContracts' to review additions and removals; local keys are never inferred as aliases."));
            }
            var snapshot = accepted.Current!;
            var exposure = Exposure(contract.Exposure);
            if (!string.Equals(accepted.ContractId, contract.Descriptor.ContractId, StringComparison.Ordinal)
                || !string.Equals(snapshot.Exposure, exposure, StringComparison.Ordinal)
                || !snapshot.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(contract.Descriptor.GetCanonicalUtf8())
                || !snapshot.Descriptor.GetCanonicalAbiProjectionUtf8().AsSpan()
                    .SequenceEqual(contract.Descriptor.GetCanonicalAbiProjectionUtf8()))
            {
                throw new InvalidDataException(
                    $"rpc-authoring.baseline-drift: Source RPC contract '{contract.ContractKey}' differs from its accepted owned baseline snapshot. "
                    + DescribeOwnedDrift(contract.ContractKey, snapshot, contract, accepted.History, platform));
            }
            if (snapshot.State == "draft")
            {
                ValidateOwnedCandidateHistory(contract.ContractKey, snapshot.Descriptor, snapshot.Exposure,
                    accepted.History, preserveUnchangedPrerelease: true);
            }
        }
    }

    private static string DescribeOwnedDrift(
        string key,
        SunderRpcOwnedContractSnapshot previous,
        SunderRpcCSharpCompiledContract? candidate,
        IReadOnlyList<SunderRpcOwnedContractSnapshot> history,
        bool platform)
    {
        var comparison = candidate is null ? null : SunderRpcContractCompatibility.Classify(previous.Descriptor, candidate.Descriptor);
        var kind = candidate is null || previous.Exposure != Exposure(candidate.Exposure)
            ? SunderRpcContractChangeKind.Major : comparison!.Kind;
        string minimum;
        try
        {
            if (SemanticVersion.Parse(previous.Descriptor.Version).Prerelease is not null)
                _ = GetMinimumOwnedContractVersion(previous.Descriptor.Version, kind);
            minimum = previous.State == "draft" && candidate is not null
                ? previous.Descriptor.Version
                : GetMinimumOwnedContractVersion(previous.Descriptor.Version, kind);
            var currentVersion = SemanticVersion.Parse(previous.Descriptor.Version);
            foreach (var snapshot in history.Where(snapshot => SemanticVersion.Parse(snapshot.Descriptor.Version).Major == currentVersion.Major))
            {
                var historicalKind = candidate is null || snapshot.Exposure != Exposure(candidate.Exposure)
                    ? SunderRpcContractChangeKind.Major
                    : SunderRpcContractCompatibility.Classify(snapshot.Descriptor, candidate.Descriptor).Kind;
                var historicalMinimum = GetMinimumOwnedContractVersion(snapshot.Descriptor.Version, historicalKind);
                if (SemanticVersion.Parse(historicalMinimum).ComparePrecedenceTo(SemanticVersion.Parse(minimum)) > 0)
                {
                    minimum = historicalMinimum;
                    if (historicalKind > kind) kind = historicalKind;
                }
            }
            if (candidate is null && previous.State == "draft")
                minimum = GetMinimumOwnedContractVersion(previous.Descriptor.Version, SunderRpcContractChangeKind.Major);
        }
        catch (InvalidOperationException exception)
        {
            return $"Contract '{previous.Descriptor.ContractId}' current '{previous.Descriptor.Version}', classified {kind}. {exception.Message}";
        }
        var changes = candidate is null ? "contract-removed" : previous.Exposure != Exposure(candidate.Exposure)
            ? "exposure-changed"
            : string.Join(", ", comparison!.Changes.Take(8).Select(static change => $"{JsonSerializer.Serialize(change.Path)}: {change.Code}"));
        if (platform)
        {
            return $"Contract '{previous.Descriptor.ContractId}' current '{previous.Descriptor.Version}', classified {kind}; minimum next version '{minimum}'. "
                + $"Affected members: {changes}. Review Core platform baseline acceptance for exact key '{key}' and old/new versions"
                + (kind == SunderRpcContractChangeKind.Major ? " with an explicit breaking-change acknowledgement." : ".");
        }
        return $"Contract '{previous.Descriptor.ContractId}' current '{previous.Descriptor.Version}', classified {kind}; minimum next version '{minimum}'. "
            + $"Affected members: {changes}. Run 'dotnet msbuild -t:AcceptSunderContracts -p:SunderContractKey={key} "
            + $"-p:SunderContractCurrentVersion={previous.Descriptor.Version} -p:SunderContractNextVersion={minimum}"
            + (kind == SunderRpcContractChangeKind.Major ? " -p:SunderAcknowledgeBreakingChange=true" : string.Empty) + "'.";
    }

    public static string ComputeInputFingerprint(
        string packageId,
        string packageVersion,
        string projectIdentity,
        string configuration,
        string targetFramework,
        bool nullableEnabled,
        IReadOnlyList<string> defineConstants,
        IReadOnlyList<SunderRpcCSharpSourceFile> sources,
        IReadOnlyList<string> referencePaths,
        SunderRpcOwnedPackageBaseline baseline,
        SunderRpcContractLock contractLock,
        string assemblyName = "Sunder.Rpc.Authoring")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintValue(hash, "schemaVersion");
        AppendFingerprintValue(hash, "1");
        AppendFingerprintValue(hash, "packageId");
        AppendFingerprintValue(hash, packageId);
        AppendFingerprintValue(hash, "packageVersion");
        AppendFingerprintValue(hash, packageVersion);
        AppendFingerprintValue(hash, "projectIdentity");
        AppendFingerprintValue(hash, projectIdentity);
        AppendFingerprintValue(hash, "assemblyName");
        AppendFingerprintValue(hash, assemblyName);
        AppendFingerprintValue(hash, "configuration");
        AppendFingerprintValue(hash, configuration);
        AppendFingerprintValue(hash, "targetFramework");
        AppendFingerprintValue(hash, targetFramework);
        AppendFingerprintValue(hash, "nullableEnabled");
        AppendFingerprintValue(hash, nullableEnabled ? "true" : "false");
        AppendFingerprintValue(hash, "defineConstants");
        foreach (var constant in defineConstants.Order(StringComparer.Ordinal))
        {
            AppendFingerprintValue(hash, constant);
        }
        AppendFingerprintValue(hash, "sources");
        foreach (var sourceHash in sources
                     .Select(static source => LowerSha256(NormalizedSourceBytes(source.Text)))
                     .Order(StringComparer.Ordinal))
        {
            AppendFingerprintValue(hash, sourceHash);
        }
        AppendFingerprintValue(hash, "references");
        foreach (var referenceHash in referencePaths
                     .Distinct(PathComparer)
                     .Select(path => LowerSha256(SunderRpcDescriptorFileReader.ReadStableBytes(
                         path,
                         int.MaxValue,
                         "C# RPC compilation reference")))
                     .Order(StringComparer.Ordinal))
        {
            AppendFingerprintValue(hash, referenceHash);
        }
        AppendFingerprintValue(hash, "ownedBaselineSha256");
        AppendFingerprintValue(hash, baseline.Sha256);
        AppendFingerprintValue(hash, "lockSha256");
        AppendFingerprintValue(hash, contractLock.Sha256);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeProjectImportFingerprint(
        string exportSha256,
        string projectIdentity,
        string configuration,
        string targetFramework)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintValue(hash, "schemaVersion");
        AppendFingerprintValue(hash, "1");
        AppendFingerprintValue(hash, "packageProjectExportSha256");
        AppendFingerprintValue(hash, exportSha256);
        AppendFingerprintValue(hash, "projectIdentity");
        AppendFingerprintValue(hash, projectIdentity);
        AppendFingerprintValue(hash, "configuration");
        AppendFingerprintValue(hash, configuration);
        AppendFingerprintValue(hash, "targetFramework");
        AppendFingerprintValue(hash, targetFramework);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static byte[] WritePackageExport(
        string producerVersion,
        string projectIdentity,
        string configuration,
        string targetFramework,
        string inputFingerprint,
        string lockSha256,
        string packageId,
        IReadOnlyList<SunderRpcCSharpCompiledContract> contracts)
    {
        var output = new StringBuilder();
        Line(output, 0, "{");
        IntegerProperty(output, 1, "schemaVersion", 1, comma: true);
        ProducerProperty(output, producerVersion, comma: true);
        StringProperty(output, 1, "projectIdentity", projectIdentity, comma: true);
        StringProperty(output, 1, "configuration", configuration, comma: true);
        StringProperty(output, 1, "targetFramework", targetFramework, comma: true);
        StringProperty(output, 1, "inputFingerprint", inputFingerprint, comma: true);
        StringProperty(output, 1, "lockSha256", lockSha256, comma: true);
        StringProperty(output, 1, "packageId", packageId, comma: true);
        ExportContractArrayProperty(output, packageId, contracts);
        Line(output, 0, "}");
        return CompleteGeneratedDocument(output, "package project export");
    }

    public static byte[] WriteProjectImportReceipt(
        string producerVersion,
        string inputFingerprint,
        string lockSha256,
        string packageId,
        string packageVersion,
        IReadOnlyList<SunderRpcPackageProjectExportContract> contracts)
    {
        var output = new StringBuilder();
        Line(output, 0, "{");
        IntegerProperty(output, 1, "schemaVersion", 1, comma: true);
        ProducerProperty(output, producerVersion, comma: true);
        StringProperty(output, 1, "inputFingerprint", inputFingerprint, comma: true);
        StringProperty(output, 1, "lockSha256", lockSha256, comma: true);
        StringProperty(output, 1, "packageId", packageId, comma: true);
        StringProperty(output, 1, "packageVersion", packageVersion, comma: true);
        ProjectImportContractArrayProperty(output, contracts, comma: true);
        EmptyArrayProperty(output, 1, "packageArtifacts", comma: true);
        ProjectImportBundleArrayProperty(output, packageId, contracts, comma: true);
        EmptyArrayProperty(output, 1, "packageArtifactImports", comma: true);
        EmptyArrayProperty(output, 1, "packageApiImports", comma: true);
        EmptyArrayProperty(output, 1, "providerImports", comma: true);
        EmptyArrayProperty(output, 1, "platformImports", comma: true);
        EmptyArrayProperty(output, 1, "providers", comma: true);
        ProjectImportUseArrayProperty(output, contracts, comma: true);
        EmptyArrayProperty(output, 1, "generatedPackageFacades", comma: true);
        ProjectImportGeneratedBindingArrayProperty(output, contracts, comma: true);
        StringProperty(output, 1, "role", "app", comma: false);
        Line(output, 0, "}");
        return CompleteGeneratedDocument(output, "authoring receipt");
    }

    private static IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline> ReadOwnedContractMap(
        JsonElement document,
        Func<JsonElement, string, string, bool, SunderRpcOwnedContractSnapshot> readSnapshot)
    {
        var contractMap = RequireProperty(document, "contracts", JsonValueKind.Object, "owned-contract baseline");
        var properties = contractMap.EnumerateObject().ToArray();
        if (properties.Length > 128)
        {
            throw new InvalidDataException("The owned-contract baseline exceeds the 128-contract limit.");
        }
        RequireOrdinalPropertyOrder(properties, "owned-contract baseline contract map");

        var contracts = new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal);
        var foldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contractIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!IsPortableKey(property.Name) || !foldedKeys.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The owned-contract baseline key '{property.Name}' is invalid or collides under ASCII case folding.");
            }

            var contract = ReadOwnedContract(property.Name, property.Value, readSnapshot);
            if (!contractIds.Add(contract.ContractId))
            {
                throw new InvalidDataException(
                    $"The owned-contract baseline repeats contract identity '{contract.ContractId}'.");
            }
            contracts.Add(property.Name, contract);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, SunderRpcOwnedContractBaseline>(contracts);
    }

    private static SunderRpcOwnedContractBaseline ReadOwnedContract(
        string contractKey,
        JsonElement value,
        Func<JsonElement, string, string, bool, SunderRpcOwnedContractSnapshot> readSnapshot)
    {
        RequireObject(
            value,
            $"owned contract '{contractKey}'",
            ["contractId", "current", "history"],
            ["contractId", "history"],
            ["current"]);
        var contractId = RequirePackageId(value, "contractId", $"owned contract '{contractKey}'");
        var history = RequireProperty(value, "history", JsonValueKind.Array, $"owned contract '{contractKey}'");
        var historyItems = history.EnumerateArray().ToArray();
        if (historyItems.Length > 256)
        {
            throw new InvalidDataException($"Owned contract '{contractKey}' exceeds the 256-entry history limit.");
        }

        var versions = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new List<SunderRpcOwnedContractSnapshot>(historyItems.Length);
        SemanticVersion? previous = null;
        foreach (var item in historyItems)
        {
            var snapshot = readSnapshot(item, contractKey, contractId, false);
            var version = SemanticVersion.Parse(snapshot.Descriptor.Version);
            if (previous is not null && version.ComparePrecedenceTo(previous.Value) <= 0)
            {
                throw new InvalidDataException(
                    $"Owned contract '{contractKey}' history is not in strictly increasing semantic-version order.");
            }
            if (!versions.Add(snapshot.Descriptor.Version))
            {
                throw new InvalidDataException(
                    $"Owned contract '{contractKey}' repeats version '{snapshot.Descriptor.Version}'.");
            }
            previous = version;
            snapshots.Add(snapshot);
        }

        SunderRpcOwnedContractSnapshot? current = null;
        if (value.TryGetProperty("current", out var currentElement))
        {
            current = readSnapshot(currentElement, contractKey, contractId, true);
            var currentVersion = SemanticVersion.Parse(current.Descriptor.Version);
            if (previous is not null && currentVersion.ComparePrecedenceTo(previous.Value) <= 0)
            {
                throw new InvalidDataException(
                    $"Owned contract '{contractKey}' current version must follow its immutable history.");
            }
            if (!versions.Add(current.Descriptor.Version))
            {
                throw new InvalidDataException(
                    $"Owned contract '{contractKey}' repeats version '{current.Descriptor.Version}'.");
            }
        }
        else if (historyItems.Length == 0)
        {
            throw new InvalidDataException(
                $"Removed owned contract '{contractKey}' must retain nonempty immutable history.");
        }

        return new(contractId, current, snapshots.AsReadOnly());
    }

    private static SunderRpcOwnedContractSnapshot ReadPackageSnapshot(
        JsonElement value,
        string contractKey,
        string contractId,
        string packageId,
        bool allowDraft)
    {
        var snapshot = ReadOwnedSnapshot(value, contractKey, contractId, allowDraft, "packageRelease", publicOnly: false);
        var label = $"owned contract '{contractKey}' snapshot";
        if (value.TryGetProperty("packageRelease", out var release))
        {
            var releaseVersion = ValidatePackageRelease(release, packageId, label);
            if (value.TryGetProperty("publication", out var publication))
            {
                ValidatePackagePublication(publication, packageId, releaseVersion, snapshot.Descriptor.Sha256, label);
            }
        }
        return snapshot;
    }

    private static SunderRpcOwnedContractSnapshot ReadOwnedSnapshot(
        JsonElement value,
        string contractKey,
        string contractId,
        bool allowDraft,
        string releaseProperty,
        bool publicOnly)
    {
        var label = $"owned contract '{contractKey}' snapshot";
        RequireObject(
            value,
            label,
            [
                "state",
                "exposure",
                "contractVersion",
                "descriptorBytes",
                "canonicalDescriptor",
                "descriptorSha256",
                "abiProjectionBytes",
                "canonicalAbiProjection",
                "abiSha256",
                releaseProperty,
                "publication",
            ],
            [
                "state",
                "exposure",
                "contractVersion",
                "descriptorBytes",
                "canonicalDescriptor",
                "descriptorSha256",
                "abiProjectionBytes",
                "canonicalAbiProjection",
                "abiSha256",
            ],
            [releaseProperty, "publication"]);
        var state = RequireString(value, "state", label);
        if (state is not ("draft" or "release-frozen" or "published") || !allowDraft && state == "draft")
        {
            throw new InvalidDataException($"The {label} state '{state}' is invalid.");
        }
        var exposure = RequireString(value, "exposure", label);
        if (exposure is not ("package" or "public") || publicOnly && exposure != "public")
        {
            throw new InvalidDataException($"The {label} exposure '{exposure}' is invalid.");
        }
        var contractVersion = RequireSemanticVersion(value, "contractVersion", label);
        var canonicalDescriptor = RequireString(value, "canonicalDescriptor", label);
        var descriptorUtf8 = Utf8NoBom.GetBytes(canonicalDescriptor);
        var descriptorBytes = RequireSafeInteger(value, "descriptorBytes", label);
        if (descriptorBytes != descriptorUtf8.Length || descriptorBytes is < 2 or > 4_194_304)
        {
            throw new InvalidDataException($"The {label} descriptor byte count is invalid.");
        }

        SunderRpcContractDescriptor descriptor;
        try
        {
            descriptor = SunderRpcContractDescriptor.Parse(descriptorUtf8);
        }
        catch (SunderRpcDescriptorException exception)
        {
            throw new InvalidDataException($"The {label} descriptor is invalid: {exception.Message}", exception);
        }
        if (!descriptorUtf8.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8())
            || !string.Equals(descriptor.ContractId, contractId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Version, contractVersion, StringComparison.Ordinal)
            || !string.Equals(RequireSha256(value, "descriptorSha256", label), descriptor.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} canonical descriptor identity does not agree with its enclosing fields.");
        }

        var canonicalAbi = RequireString(value, "canonicalAbiProjection", label);
        var abiUtf8 = Utf8NoBom.GetBytes(canonicalAbi);
        var abiBytes = RequireSafeInteger(value, "abiProjectionBytes", label);
        if (abiBytes != abiUtf8.Length
            || abiBytes is < 2 or > 4_194_304
            || !abiUtf8.AsSpan().SequenceEqual(descriptor.GetCanonicalAbiProjectionUtf8())
            || !string.Equals(RequireSha256(value, "abiSha256", label), descriptor.AbiSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} canonical ABI identity is invalid.");
        }

        var hasRelease = value.TryGetProperty(releaseProperty, out _);
        var hasPublication = value.TryGetProperty("publication", out _);
        switch (state)
        {
            case "draft" when hasRelease || hasPublication:
                throw new InvalidDataException($"The draft {label} must not contain release or publication evidence.");
            case "release-frozen" when !hasRelease || hasPublication:
                throw new InvalidDataException($"The release-frozen {label} requires only {(releaseProperty == "packageRelease" ? "package-release" : "catalog-release")} evidence.");
            case "published" when !hasRelease || !hasPublication:
                throw new InvalidDataException($"The published {label} requires release and publication evidence.");
        }
        return new(state, exposure, descriptor, value.Clone());
    }

    private static string ValidatePackageRelease(JsonElement value, string packageId, string label)
    {
        RequireObject(
            value,
            $"{label} package release",
            ["packageId", "packageVersion"],
            ["packageId", "packageVersion"],
            []);
        RequireString(value, "packageId", packageId, $"{label} package release");
        return RequireSemanticVersion(value, "packageVersion", $"{label} package release");
    }

    private static void ValidatePackagePublication(
        JsonElement value,
        string packageId,
        string packageVersion,
        string descriptorSha256,
        string label)
    {
        var publicationLabel = $"{label} publication";
        RequireObject(
            value,
            publicationLabel,
            ["registryOrigin", "packageId", "packageVersion", "packageContentIdentity", "publishedAtUtc", "etag"],
            ["registryOrigin", "packageId", "packageVersion", "packageContentIdentity", "publishedAtUtc", "etag"],
            []);
        RequireCanonicalRegistryOrigin(RequireString(value, "registryOrigin", publicationLabel), publicationLabel);
        RequireString(value, "packageId", packageId, publicationLabel);
        RequireString(value, "packageVersion", packageVersion, publicationLabel);
        _ = RequireSha256(value, "packageContentIdentity", publicationLabel);
        RequireCanonicalTimestamp(RequireString(value, "publishedAtUtc", publicationLabel), publicationLabel);
        RequireString(value, "etag", $"\"{descriptorSha256}\"", publicationLabel);
    }

    private static (byte[] Bytes, JsonDocument Document) ReadStrictDocument(string path, int maximumBytes, string label)
    {
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, maximumBytes, label);
        return (bytes, ParseStrictDocument(bytes, maximumBytes, label));
    }

    private static JsonDocument ParseStrictDocument(byte[] bytes, int maximumBytes, string label)
    {
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {label} exceeds its {maximumBytes}-byte limit.");
        }
        if (bytes.Length == 0
            || bytes[^1] != (byte)'\n'
            || bytes.AsSpan().Contains((byte)'\r')
            || bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            throw new InvalidDataException(
                $"The {label} must be strict UTF-8 without a BOM, use LF line endings, and have exactly one terminal LF.");
        }
        JsonDocument? document = null;
        try
        {
            _ = StrictUtf8.GetString(bytes);
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumOuterDepth,
            });
            ValidateRepresentation(document.RootElement, label);
            var deterministic = WriteDeterministicDocument(document.RootElement);
            if (!bytes.AsSpan().SequenceEqual(deterministic))
            {
                throw new InvalidDataException(
                    $"The {label} does not use the required deterministic outer JSON form.");
            }
            return document;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            document?.Dispose();
            throw new InvalidDataException($"The {label} is not valid strict UTF-8 JSON: {exception.Message}", exception);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private static void ValidateRepresentation(JsonElement value, string label)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var exact = new HashSet<string>(StringComparer.Ordinal);
                var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    RequireUnicodeScalars(property.Name, label);
                    if (!exact.Add(property.Name) || !folded.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            $"The {label} contains duplicate or ASCII-case-colliding member '{property.Name}'.");
                    }
                    ValidateRepresentation(property.Value, label);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateRepresentation(item, label);
                break;
            case JsonValueKind.String:
                RequireUnicodeScalars(value.GetString()!, label);
                break;
        }
    }

    private static void RequireUnicodeScalars(string value, string label)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new InvalidDataException($"The {label} contains a non-scalar Unicode string.");
                }
                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new InvalidDataException($"The {label} contains a non-scalar Unicode string.");
            }
        }
    }

    private static void RequireObject(
        JsonElement value,
        string label,
        IReadOnlyList<string> propertyOrder,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {label} must be an object.");
        }
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var actual = value.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Any(name => !allowed.Contains(name)) || required.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException($"The {label} has missing or unsupported members.");
        }
        var expectedOrder = propertyOrder.Where(actual.Contains).ToArray();
        if (!actual.SequenceEqual(expectedOrder, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"The {label} members are not in deterministic schema order.");
        }
    }

    private static JsonElement RequireProperty(JsonElement value, string name, JsonValueKind kind, string label)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != kind)
        {
            throw new InvalidDataException($"The {label} member '{name}' must be a {kind.ToString().ToLowerInvariant()}.");
        }
        return property;
    }

    private static string RequireString(JsonElement value, string name, string label)
        => RequireProperty(value, name, JsonValueKind.String, label).GetString()!;

    private static void RequireString(JsonElement value, string name, string expected, string label)
    {
        var actual = RequireString(value, name, label);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} member '{name}' must be exactly '{expected}'.");
        }
    }

    private static void RequireInteger(JsonElement value, string name, long expected, string label)
    {
        var actual = RequireSafeInteger(value, name, label);
        if (actual != expected)
        {
            throw new InvalidDataException($"The {label} member '{name}' must be {expected.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static long RequireSafeInteger(JsonElement value, string name, string label)
    {
        var property = RequireProperty(value, name, JsonValueKind.Number, label);
        if (!property.TryGetInt64(out var result) || result is < 0 or > 9_007_199_254_740_991)
        {
            throw new InvalidDataException($"The {label} member '{name}' must be a nonnegative safe integer.");
        }
        return result;
    }

    private static string RequirePackageId(JsonElement value, string name, string label)
    {
        var result = RequireString(value, name, label);
        if (!PackageId.TryParse(result, out _))
        {
            throw new InvalidDataException($"The {label} member '{name}' is not a canonical package identity.");
        }
        return result;
    }

    private static string RequireSemanticVersion(JsonElement value, string name, string label)
    {
        var result = RequireString(value, name, label);
        if (!SemanticVersion.TryParse(result, out _))
        {
            throw new InvalidDataException($"The {label} member '{name}' is not strict semantic versioning.");
        }
        return result;
    }

    private static string RequireSha256(JsonElement value, string name, string label)
    {
        var result = RequireString(value, name, label);
        if (result.Length != 64 || result.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"The {label} member '{name}' is not lowercase SHA-256 text.");
        }
        return result;
    }

    private static void RequireOrdinalPropertyOrder(IReadOnlyList<JsonProperty> properties, string label)
    {
        var expected = properties.Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (!properties.Select(static property => property.Name).SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"The {label} is not in ascending ordinal key order.");
        }
    }

    private static bool IsPortableKey(string value)
    {
        if (value.Length is < 1 or > 128 || !IsAsciiAlphaNumeric(value[0]) || !IsAsciiAlphaNumeric(value[^1]))
        {
            return false;
        }
        return value.All(static character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsAsciiAlphaNumeric(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    internal static void RequireCanonicalRegistryOrigin(
        string value,
        string label,
        bool allowDevelopmentRegistryOrigin = true)
    {
        var isHttps = value.StartsWith("https://", StringComparison.Ordinal);
        var isDevelopmentHttp = value.StartsWith("http://localhost", StringComparison.Ordinal);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/"
            || uri.Host.Any(static character => character > 0x7f || char.IsUpper(character))
            || isHttps && (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                           || uri.HostNameType != UriHostNameType.Dns
                           || !IsCanonicalDnsHost(uri.Host))
            || isDevelopmentHttp && (!allowDevelopmentRegistryOrigin
                                     || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                                     || !string.Equals(uri.Host, "localhost", StringComparison.Ordinal))
            || !isHttps && !isDevelopmentHttp
            || HasInvalidExplicitRegistryPort(value, isHttps ? 443 : 80))
        {
            throw new InvalidDataException($"The {label} registry origin '{value}' is not canonical.");
        }
    }

    private static bool IsCanonicalDnsHost(string host)
        => host.Length <= 253
           && host.Any(static character => character is >= 'a' and <= 'z')
           && host.Split('.').All(static label => label.Length is >= 1 and <= 63
                                               && !label.StartsWith("xn--", StringComparison.Ordinal)
                                               && label[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
                                               && label[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
                                               && label.All(static character => character is >= 'a' and <= 'z'
                                                                                       or >= '0' and <= '9'
                                                                                       or '-'));

    private static bool HasInvalidExplicitRegistryPort(string value, int defaultPort)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authority = value.AsSpan(authorityStart, value.Length - authorityStart - 1);
        var separator = authority.LastIndexOf(':');
        if (separator < 0) return false;
        var portText = authority[(separator + 1)..];
        return portText.Length == 0
               || portText.Length > 1 && portText[0] == '0'
               || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
               || port is < 1 or > 65535
               || port == defaultPort;
    }

    private static void RequireCanonicalTimestamp(string value, string label)
    {
        var hasFraction = value.Length != 20;
        var fractionalDigits = value.Length - 21;
        if (value.Length is < 20 or > 30
            || value[4] != '-'
            || value[7] != '-'
            || value[10] != 'T'
            || value[13] != ':'
            || value[16] != ':'
            || value[^1] != 'Z'
            || !IsAsciiDigits(value.AsSpan(0, 4))
            || !IsAsciiDigits(value.AsSpan(5, 2))
            || !IsAsciiDigits(value.AsSpan(8, 2))
            || !IsAsciiDigits(value.AsSpan(11, 2))
            || !IsAsciiDigits(value.AsSpan(14, 2))
            || !IsAsciiDigits(value.AsSpan(17, 2))
            || hasFraction && (value.Length < 22
                               || value[19] != '.'
                               || fractionalDigits is < 1 or > 9
                               || value[^2] == '0'
                               || !IsAsciiDigits(value.AsSpan(20, fractionalDigits)))
            || !TryParseTimestampComponent(value, 0, 4, out var year)
            || !TryParseTimestampComponent(value, 5, 2, out var month)
            || !TryParseTimestampComponent(value, 8, 2, out var day)
            || !TryParseTimestampComponent(value, 11, 2, out var hour)
            || !TryParseTimestampComponent(value, 14, 2, out var minute)
            || !TryParseTimestampComponent(value, 17, 2, out var second)
            || year == 0
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || hour > 23
            || minute > 59
            || second > 59)
        {
            throw new InvalidDataException($"The {label} timestamp '{value}' is not canonical UTC text.");
        }
    }

    private static bool TryParseTimestampComponent(string value, int start, int length, out int result)
        => int.TryParse(
            value.AsSpan(start, length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result);

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')) return false;
        }
        return true;
    }

    internal static byte[] WriteDeterministicDocument(JsonElement root)
    {
        var output = new StringBuilder();
        AppendDeterministicValue(output, root, 0);
        output.Append('\n');
        return Utf8NoBom.GetBytes(output.ToString());
    }

    private static void AppendDeterministicValue(StringBuilder output, JsonElement value, int indent)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().ToArray();
                if (properties.Length == 0)
                {
                    output.Append("{}");
                    return;
                }
                output.Append("{\n");
                for (var index = 0; index < properties.Length; index++)
                {
                    output.Append(' ', (indent + 1) * 2);
                    AppendJsonString(output, properties[index].Name);
                    output.Append(": ");
                    AppendDeterministicValue(output, properties[index].Value, indent + 1);
                    output.Append(index + 1 == properties.Length ? "\n" : ",\n");
                }
                output.Append(' ', indent * 2).Append('}');
                return;
            }
            case JsonValueKind.Array:
            {
                var values = value.EnumerateArray().ToArray();
                if (values.Length == 0)
                {
                    output.Append("[]");
                    return;
                }
                output.Append("[\n");
                for (var index = 0; index < values.Length; index++)
                {
                    output.Append(' ', (indent + 1) * 2);
                    AppendDeterministicValue(output, values[index], indent + 1);
                    output.Append(index + 1 == values.Length ? "\n" : ",\n");
                }
                output.Append(' ', indent * 2).Append(']');
                return;
            }
            case JsonValueKind.String:
                AppendJsonString(output, value.GetString()!);
                return;
            case JsonValueKind.Number when value.TryGetInt64(out var integer):
                output.Append(integer.ToString(CultureInfo.InvariantCulture));
                return;
            case JsonValueKind.True:
                output.Append("true");
                return;
            case JsonValueKind.False:
                output.Append("false");
                return;
            default:
                throw new InvalidDataException(
                    "The deterministic outer JSON form permits only objects, arrays, strings, safe integers, and booleans.");
        }
    }

    private static byte[] CompleteGeneratedDocument(StringBuilder output, string label)
    {
        var bytes = Utf8NoBom.GetBytes(output.ToString());
        if (bytes.Length > MaximumGeneratedDocumentBytes)
        {
            throw new InvalidDataException(
                $"The generated Sunder RPC {label} exceeds the inclusive {MaximumGeneratedDocumentBytes} byte limit.");
        }
        return bytes;
    }

    private static void AppendFingerprintValue(IncrementalHash hash, string value)
    {
        var bytes = Utf8NoBom.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static byte[] NormalizedSourceBytes(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n') + "\n";
        return Utf8NoBom.GetBytes(normalized);
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ProducerProperty(StringBuilder output, string producerVersion, bool comma)
    {
        Line(output, 1, "\"producer\": {");
        StringProperty(output, 2, "name", "Sunder.Package.Build", comma: true);
        StringProperty(output, 2, "version", producerVersion, comma: true);
        StringProperty(output, 2, "language", "csharp", comma: false);
        Line(output, 1, comma ? "}," : "}");
    }

    private static void ExportContractArrayProperty(
        StringBuilder output,
        string packageId,
        IReadOnlyList<SunderRpcCSharpCompiledContract> contracts)
    {
        var ordered = contracts.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            EmptyArrayProperty(output, 1, "contracts", comma: false);
            return;
        }
        Line(output, 1, "\"contracts\": [");
        for (var index = 0; index < ordered.Length; index++)
        {
            var contract = ordered[index];
            var descriptor = contract.Descriptor;
            Line(output, 2, "{");
            StringProperty(output, 3, "contractKey", contract.ContractKey, comma: true);
            StringProperty(output, 3, "contractId", descriptor.ContractId, comma: true);
            StringProperty(output, 3, "contractVersion", descriptor.Version, comma: true);
            StringProperty(output, 3, "exposure", Exposure(contract.Exposure), comma: true);
            StringProperty(output, 3, "source", "owned", comma: true);
            StringProperty(output, 3, "authorityKind", "package", comma: true);
            StringProperty(output, 3, "authorityId", packageId, comma: true);
            IntegerProperty(output, 3, "descriptorBytes", descriptor.GetCanonicalUtf8().Length, comma: true);
            StringProperty(output, 3, "canonicalDescriptor", Utf8NoBom.GetString(descriptor.GetCanonicalUtf8()), comma: true);
            StringProperty(output, 3, "descriptorSha256", descriptor.Sha256, comma: true);
            StringProperty(output, 3, "abiSha256", descriptor.AbiSha256!, comma: contract.AppUse is not null || contract.RuntimeUse is not null);
            UsePolicyProperty(output, 3, "app", contract.AppUse, comma: contract.RuntimeUse is not null);
            UsePolicyProperty(output, 3, "runtime", contract.RuntimeUse);
            Line(output, 2, index + 1 == ordered.Length ? "}" : "},");
        }
        Line(output, 1, "]");
    }

    private static void ProjectImportContractArrayProperty(
        StringBuilder output,
        IReadOnlyList<SunderRpcPackageProjectExportContract> contracts,
        bool comma)
    {
        var ordered = contracts.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            EmptyArrayProperty(output, 1, "contracts", comma);
            return;
        }
        Line(output, 1, "\"contracts\": [");
        for (var index = 0; index < ordered.Length; index++)
        {
            var contract = ordered[index];
            var descriptor = contract.Descriptor;
            Line(output, 2, "{");
            StringProperty(output, 3, "contractKey", contract.ContractKey, comma: true);
            StringProperty(output, 3, "contractId", descriptor.ContractId, comma: true);
            StringProperty(output, 3, "contractVersion", descriptor.Version, comma: true);
            StringProperty(output, 3, "exposure", Exposure(contract.Exposure), comma: true);
            IntegerProperty(output, 3, "descriptorBytes", descriptor.GetCanonicalUtf8().Length, comma: true);
            StringProperty(output, 3, "canonicalDescriptor", Utf8NoBom.GetString(descriptor.GetCanonicalUtf8()), comma: true);
            StringProperty(output, 3, "descriptorSha256", descriptor.Sha256, comma: true);
            StringProperty(output, 3, "abiSha256", descriptor.AbiSha256!, comma: contract.AppUse is not null);
            UsePolicyProperty(output, 3, "app", contract.AppUse);
            Line(output, 2, index + 1 == ordered.Length ? "}" : "},");
        }
        Line(output, 1, comma ? "]," : "]");
    }

    private static void ProjectImportBundleArrayProperty(
        StringBuilder output,
        string packageId,
        IReadOnlyList<SunderRpcPackageProjectExportContract> contracts,
        bool comma)
    {
        var ordered = contracts.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            EmptyArrayProperty(output, 1, "bundles", comma);
            return;
        }
        Line(output, 1, "\"bundles\": [");
        for (var index = 0; index < ordered.Length; index++)
        {
            Line(output, 2, "{");
            StringProperty(output, 3, "contractKey", ordered[index].ContractKey, comma: true);
            StringProperty(output, 3, "source", "owned", comma: true);
            StringProperty(output, 3, "authorityKind", "package", comma: true);
            StringProperty(output, 3, "authorityId", packageId, comma: false);
            Line(output, 2, index + 1 == ordered.Length ? "}" : "},");
        }
        Line(output, 1, comma ? "]," : "]");
    }

    private static void ProjectImportUseArrayProperty(
        StringBuilder output,
        IReadOnlyList<SunderRpcPackageProjectExportContract> contracts,
        bool comma)
    {
        var ordered = contracts.Select(contract => (Contract: contract, Policy: ResolveAppUse(contract.Exposure, contract.Descriptor, contract.AppUse)))
            .Where(item => item.Policy is not null).OrderBy(item => item.Contract.ContractKey, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            EmptyArrayProperty(output, 1, "uses", comma);
            return;
        }
        Line(output, 1, "\"uses\": [");
        for (var index = 0; index < ordered.Length; index++)
        {
            var contract = ordered[index].Contract;
            var policy = ordered[index].Policy!;
            var actions = policy.Actions;
            Line(output, 2, "{");
            StringProperty(output, 3, "role", "app", comma: true);
            Line(output, 3, "\"required\": " + (policy.Required ? "true," : "false,"));
            Line(output, 3, "\"actions\": [");
            for (var actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                output.Append(' ', 8);
                AppendJsonString(output, actions[actionIndex]);
                output.Append(actionIndex + 1 == actions.Count ? "\n" : ",\n");
            }
            Line(output, 3, "],");
            StringProperty(output, 3, "contractKey", contract.ContractKey, comma: false);
            Line(output, 2, index + 1 == ordered.Length ? "}" : "},");
        }
        Line(output, 1, comma ? "]," : "]");
    }

    private static void ProjectImportGeneratedBindingArrayProperty(
        StringBuilder output,
        IReadOnlyList<SunderRpcPackageProjectExportContract> contracts,
        bool comma)
    {
        var ordered = contracts.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            EmptyArrayProperty(output, 1, "generatedBindings", comma);
            return;
        }
        Line(output, 1, "\"generatedBindings\": [");
        for (var index = 0; index < ordered.Length; index++)
        {
            Line(output, 2, "{");
            StringProperty(output, 3, "contractKey", ordered[index].ContractKey, comma: true);
            StringProperty(output, 3, "logicalName", ordered[index].GeneratedLogicalName, comma: false);
            Line(output, 2, index + 1 == ordered.Length ? "}" : "},");
        }
        Line(output, 1, comma ? "]," : "]");
    }

    private static void EmptyArrayProperty(StringBuilder output, int indent, string name, bool comma)
        => Line(output, indent, $"\"{name}\": []" + (comma ? "," : string.Empty));

    private static void IntegerProperty(StringBuilder output, int indent, string name, long value, bool comma)
        => Line(
            output,
            indent,
            $"\"{name}\": {value.ToString(CultureInfo.InvariantCulture)}" + (comma ? "," : string.Empty));

    private static void StringProperty(StringBuilder output, int indent, string name, string value, bool comma)
    {
        output.Append(' ', indent * 2);
        AppendJsonString(output, name);
        output.Append(": ");
        AppendJsonString(output, value);
        if (comma) output.Append(',');
        output.Append('\n');
    }

    private static void AppendJsonString(StringBuilder output, string value)
        => SunderRpcAuthoringJson.AppendString(output, value);

    private static void Line(StringBuilder output, int indent, string value)
        => output.Append(' ', indent * 2).Append(value).Append('\n');

    private static string Exposure(SunderRpcExposure exposure)
        => exposure switch
        {
            SunderRpcExposure.Package => "package",
            SunderRpcExposure.Public => "public",
            _ => throw new InvalidDataException($"Unsupported RPC exposure '{exposure}'."),
        };

    private static string Audience(SunderRpcProviderAudience audience)
        => audience switch
        {
            SunderRpcProviderAudience.Owner => "owner",
            SunderRpcProviderAudience.Public => "public",
            _ => throw new InvalidDataException($"Unsupported RPC provider audience '{audience}'."),
        };

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
