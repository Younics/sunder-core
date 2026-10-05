using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcAuthoringReceiptProjection(
    SunderPackageManifest Manifest,
    IReadOnlyDictionary<string, SunderRpcContractDescriptor> Contracts);

internal static partial class SunderRpcAuthoringDocuments
{
    private static readonly JsonSerializerOptions ReceiptManifestOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SunderRpcAuthoringReceiptProjection ReadAuthoringReceiptProjection(
        string path, string expectedSha256, string lockSha256, string packageId, string packageVersion)
    {
        var (bytes, document) = ReadStrictDocument(path, MaximumGeneratedDocumentBytes, "Sunder RPC authoring receipt");
        using (document)
        {
            if (LowerSha256(bytes) != expectedSha256)
            {
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Authoring receipt changed after generation.");
            }
            return ProjectAuthoringReceipt(document.RootElement, lockSha256, packageId, packageVersion);
        }
    }

    public static SunderRpcAuthoringReceiptProjection ParseAuthoringReceiptProjection(
        byte[] bytes, string lockSha256, string packageId, string packageVersion)
    {
        using var document = ParseStrictDocument(bytes, MaximumGeneratedDocumentBytes, "Sunder RPC authoring receipt");
        return ProjectAuthoringReceipt(document.RootElement, lockSha256, packageId, packageVersion);
    }

    private static SunderRpcAuthoringReceiptProjection ProjectAuthoringReceipt(
        JsonElement root, string lockSha256, string packageId, string packageVersion)
    {
        const string label = "authoring receipt";
        string[] members = ["schemaVersion", "producer", "inputFingerprint", "lockSha256", "packageId", "packageVersion",
            "contracts", "packageArtifacts", "bundles", "packageArtifactImports", "packageApiImports", "providerImports",
            "platformImports", "providers", "uses", "generatedPackageFacades", "generatedBindings"];
        RequireObject(root, label, [.. members, "role"], members, ["role"]);
        var appRole = root.TryGetProperty("role", out var role);
        if (appRole && (role.ValueKind != JsonValueKind.String || role.GetString() != "app"))
            throw new InvalidDataException("rpc-authoring.receipt-invalid: Only App project-import receipts declare role=app.");
        RequireInteger(root, "schemaVersion", 1, label);
        var producer = RequireProperty(root, "producer", JsonValueKind.Object, label);
        RequireObject(producer, label, ["name", "version", "language"], ["name", "version", "language"], []);
        RequireString(producer, "name", "Sunder.Package.Build", label);
        RequireString(producer, "language", "csharp", label);
        _ = RequireSemanticVersion(producer, "version", label);
        _ = RequireSha256(root, "inputFingerprint", label);
        _ = RequireSha256(root, "lockSha256", label);
        RequireString(root, "lockSha256", lockSha256, label);
        RequireString(root, "packageId", packageId, label);
        RequireString(root, "packageVersion", packageVersion, label);
        _ = RequirePackageId(root, "packageId", label);
        _ = RequireSemanticVersion(root, "packageVersion", label);
        foreach (var member in members.Skip(6))
        {
            if (RequireProperty(root, member, JsonValueKind.Array, label).GetArrayLength() > 128)
                throw new InvalidDataException($"The {label} '{member}' exceeds 128 entries.");
        }
        foreach (var artifact in ReceiptRecords(root, "packageArtifactImports", ["packageId", "requestedVersionRange", "includePrerelease",
                     "selectedPackageVersion", "selectedPackageContentIdentity", "publicContractCount", "publicInventorySha256", "authorityConstraints"]))
        {
            _ = RequirePackageId(artifact, "packageId", label);
            _ = ReceiptRecords(artifact, "authorityConstraints", ["authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity"]).ToArray();
        }
        foreach (var import in ReceiptRecords(root, "packageApiImports", ["packageId", "presence", "bundleRefs", "useRefs"]))
        {
            _ = RequirePackageId(import, "packageId", label);
            _ = RequireString(import, "presence", label);
            _ = ReceiptRecords(import, "bundleRefs", ["contractId", "contractVersion"]).ToArray();
            _ = ReceiptRecords(import, "useRefs", ["role", "contractId", "contractVersion"]).ToArray();
        }
        foreach (var import in ReceiptRecords(root, "providerImports", ["key", "packageId", "contractId", "contractVersion", "bundleRef", "providerRefs"]))
        {
            _ = RequirePackageId(import, "packageId", label);
            RequireObject(import.GetProperty("bundleRef"), label, ["contractId", "contractVersion"], ["contractId", "contractVersion"], []);
            _ = ReceiptRecords(import, "providerRefs", ["role", "providerId", "contractId", "contractVersion"]).ToArray();
        }
        foreach (var import in ReceiptRecords(root, "platformImports", ["key", "authorityKind", "authorityId", "authorityReleaseRange", "includePrerelease",
                     "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
                     "contractId", "contractVersion", "bundleRef", "providerRefs"]))
        {
            RequireObject(import.GetProperty("bundleRef"), label, ["contractId", "contractVersion"], ["contractId", "contractVersion"], []);
            _ = ReceiptRecords(import, "providerRefs", ["role", "providerId", "contractId", "contractVersion"]).ToArray();
        }

        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        var contractFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var appPolicies = new Dictionary<string, SunderRpcOwnedUsePolicy?>(StringComparer.Ordinal);
        var runtimePolicies = new Dictionary<string, SunderRpcOwnedUsePolicy?>(StringComparer.Ordinal);
        var foldedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousKey = null;
        foreach (var value in root.GetProperty("contracts").EnumerateArray())
        {
            string[] fields = ["contractKey", "contractId", "contractVersion", "exposure", "descriptorBytes",
                "canonicalDescriptor", "descriptorSha256", "abiSha256"];
            RequireObject(value, label + " contract", [.. fields, "appUse", "runtimeUse"], fields, ["appUse", "runtimeUse"]);
            var key = RequireString(value, "contractKey", label);
            if (!IsPortableKey(key) || !foldedKeys.Add(key) || previousKey is not null && string.CompareOrdinal(previousKey, key) >= 0)
                throw new InvalidDataException("rpc-authoring.identity-collision: Receipt contract keys are invalid, duplicated or unordered.");
            previousKey = key;
            var bytes = Utf8NoBom.GetBytes(RequireString(value, "canonicalDescriptor", label));
            if (bytes.Length is < 2 or > MaximumDescriptorBytes || RequireSafeInteger(value, "descriptorBytes", label) != bytes.Length)
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Receipt descriptor byte count differs.");
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            if (!bytes.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8())
                || RequirePackageId(value, "contractId", label) != descriptor.ContractId
                || RequireSemanticVersion(value, "contractVersion", label) != descriptor.Version
                || RequireSha256(value, "descriptorSha256", label) != descriptor.Sha256
                || RequireSha256(value, "abiSha256", label) != descriptor.AbiSha256
                || RequireString(value, "exposure", label) is not ("package" or "public"))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Receipt canonical descriptor fields differ.");
            contracts.Add(key, descriptor);
            contractFields.Add(key, value);
            appPolicies.Add(key, ResolveAppUse(value.GetProperty("exposure").GetString() == "package" ? SunderRpcExposure.Package : SunderRpcExposure.Public,
                descriptor, ReadUsePolicy(value, descriptor, "app")));
            runtimePolicies.Add(key, ReadUsePolicy(value, descriptor, "runtime"));
        }

        // The receipt already contains the normalized import groups. Join only descriptor fields;
        // do not reconstruct authority or grants from a Worker assembly or from authored item metadata.
        var projection = new JsonObject { ["id"] = packageId, ["version"] = packageVersion };
        foreach (var member in new[] { "packageArtifactImports", "packageApiImports", "providerImports", "platformImports" })
            projection[member] = JsonNode.Parse(root.GetProperty(member).GetRawText());
        var bundledKeys = new HashSet<string>(StringComparer.Ordinal);
        var ownedKeys = new HashSet<string>(StringComparer.Ordinal);
        projection["contractBundles"] = ReceiptArray(ReceiptRecords(root, "bundles", ["contractKey", "source", "authorityKind", "authorityId"],
            ["selectedAuthorityRelease", "selectedAuthorityContentIdentity"]).Select(value =>
        {
            var (key, descriptor, record) = Join(value);
            if (value.GetProperty("source").GetString() == "owned") ownedKeys.Add(key);
            else if (contractFields[key].TryGetProperty("appUse", out _) || contractFields[key].TryGetProperty("runtimeUse", out _))
                throw new InvalidDataException("rpc-authoring.use-invalid: Imported contracts cannot declare owned consumer policy.");
            if (!bundledKeys.Add(key)) throw new InvalidDataException("Receipt repeats a bundle contractKey.");
            record["version"] = descriptor.Version;
            record["descriptorPath"] = $"contracts/{key}.rpc.json";
            record["descriptorSha256"] = descriptor.Sha256;
            record["abiSha256"] = descriptor.AbiSha256;
            record["exposure"] = contractFields[key].GetProperty("exposure").GetString();
            return record;
        }));
        if (!bundledKeys.SetEquals(contracts.Keys)) throw new InvalidDataException("Receipt bundles must cover exactly its contracts.");
        var appUseKeys = new HashSet<string>(StringComparer.Ordinal);
        var runtimeUseKeys = new HashSet<string>(StringComparer.Ordinal);
        projection["usesContracts"] = ReceiptArray(ReceiptRecords(root, "uses", ["role", "required", "actions", "contractKey"]).Select(value =>
        {
            var (key, descriptor, record) = Join(value);
            record["contractVersion"] = descriptor.Version;
            record["abiSha256"] = descriptor.AbiSha256;
            var actions = RequireProperty(value, "actions", JsonValueKind.Array, label).EnumerateArray()
                .Select(static action => action.ValueKind == JsonValueKind.String ? action.GetString()
                    : throw new InvalidDataException("Receipt actions must be strings.")).ToArray();
            var useRole = RequireString(value, "role", label);
            if (useRole is not ("app" or "runtime")) throw new InvalidDataException("Receipt use role must be app or runtime.");
            var isAppUse = useRole == "app";
            var owned = ownedKeys.Contains(key);
            var policy = isAppUse ? appPolicies[key] : runtimePolicies[key];
            if (isAppUse || owned)
            {
                if (!owned || policy is null || !(isAppUse ? appUseKeys : runtimeUseKeys).Add(key)
                    || RequireBoolean(value, "required", label) != policy.Required)
                    throw new InvalidDataException("rpc-authoring.evidence-mismatch: Consumer use does not match its owned role policy.");
            }
            if (!actions.SequenceEqual(isAppUse || owned ? policy!.Actions : ReceiptActions(descriptor), StringComparer.Ordinal))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Receipt actions do not match its descriptor.");
            return record;
        }));
        projection["provides"] = ReceiptArray(ReceiptRecords(root, "providers", ["providerId", "role", "audience", "contractKey"]).Select(value =>
        {
            var (_, descriptor, record) = Join(value);
            record["contractVersion"] = descriptor.Version;
            record["descriptorSha256"] = descriptor.Sha256;
            record["abiSha256"] = descriptor.AbiSha256;
            return record;
        }));
        var projected = projection.Deserialize<SunderPackageManifest>(ReceiptManifestOptions)!;
        if (appRole || appUseKeys.Count != 0)
        {
            if (runtimePolicies.Values.Any(policy => policy is not null))
                throw new InvalidDataException("rpc-authoring.runtime-use-invalid: App receipts cannot inherit Runtime consumer policy.");
            if (!appUseKeys.SetEquals(appPolicies.Where(pair => pair.Value is not null).Select(pair => pair.Key)))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: App uses must exactly cover the exported App policy.");
            if (projected.ContractBundles!.Any(bundle => bundle!.Source != "owned") || projected.PackageArtifactImports is { Count: > 0 }
                || projected.Provides is { Count: > 0 } || projected.UsesContracts!.Any(use => use!.Role != "app"))
                throw new InvalidDataException("App project-import receipts cannot grant external imports or providers.");
        }
        else if (!runtimeUseKeys.SetEquals(runtimePolicies.Where(pair => pair.Value is not null).Select(pair => pair.Key)))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Runtime uses must exactly cover the owned Runtime policy.");

        var inventories = new Dictionary<string, SunderRpcLockedPackageArtifact>(StringComparer.Ordinal);
        foreach (var value in root.GetProperty("packageArtifacts").EnumerateArray())
        {
            var id = RequirePackageId(value, "packageId", label);
            var artifact = JsonNode.Parse(value.GetRawText())!.AsObject();
            artifact.Remove("packageId");
            var inventory = new JsonObject();
            foreach (var contract in RequireProperty(value, "contracts", JsonValueKind.Array, label).EnumerateArray())
            {
                var record = JsonNode.Parse(contract.GetRawText())!.AsObject();
                record.Remove("contractId");
                inventory.Add(RequirePackageId(contract, "contractId", label), record);
            }
            artifact["contracts"] = inventory;
            inventories.Add(id, ReadPackageArtifact(id, JsonSerializer.SerializeToElement(artifact), allowDevelopmentRegistryOrigin: true));
        }
        var artifacts = (projected.PackageArtifactImports ?? []).ToDictionary(static item => item!.PackageId!, static item => item!, StringComparer.Ordinal);
        if (!inventories.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(artifacts.Keys))
            throw new InvalidDataException("Receipt artifact imports do not cover its complete inventory evidence.");
        foreach (var (id, artifact) in artifacts)
        {
            var inventory = inventories[id];
            if (artifact.RequestedVersionRange != inventory.RequestedVersionRange || artifact.IncludePrerelease != inventory.IncludePrerelease
                || artifact.SelectedPackageVersion != inventory.SelectedPackageVersion || artifact.SelectedPackageContentIdentity != inventory.SelectedPackageContentIdentity
                || artifact.PublicContractCount != inventory.PublicContractCount || artifact.PublicInventorySha256 != inventory.PublicInventorySha256
                || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(artifact.AuthorityConstraints, ReceiptManifestOptions),
                    JsonSerializer.SerializeToElement(ReceiptArray(inventory.Contracts.Values.DistinctBy(static item => (item.AuthorityKind, item.AuthorityId))
                        .OrderBy(static item => item.AuthorityKind, StringComparer.Ordinal).ThenBy(static item => item.AuthorityId, StringComparer.Ordinal).Select(ReceiptAuthority)))))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Receipt artifact projection differs from inventory evidence.");
        }
        foreach (var bundle in projected.ContractBundles!.OfType<SunderPackageContractBundleManifest>().Where(static bundle => bundle.Source == "imported"))
        {
            if (!contracts.TryGetValue(bundle.ContractId!, out var descriptor)
                || descriptor.ContractId != bundle.ContractId)
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Imported receipt contract keys must equal canonical contract IDs.");
            var evidence = inventories.Values.SelectMany(static artifact => artifact.Contracts.Values)
                .Where(contract => contract.ContractId == bundle.ContractId)
                .Concat((projected.PlatformImports ?? []).Where(import => import!.ContractId == bundle.ContractId).Select(import => new SunderRpcLockedArtifactContract(
                    import!.ContractId!, "imported", import.AuthorityKind!, import.AuthorityId!, import.SelectedAuthorityRelease!, import.SelectedAuthorityContentIdentity!,
                     import.ContractVersion!, descriptor.GetCanonicalUtf8().Length,
                    descriptor.Sha256, descriptor.AbiSha256, "\"" + descriptor.Sha256 + "\""))).ToArray();
            if (evidence.Length == 0 || evidence.Any(contract => contract.ResolvedContractVersion != bundle.Version
                || contract.DescriptorSha256 != bundle.DescriptorSha256 || contract.AbiSha256 != bundle.AbiSha256
                || contract.DescriptorBytes != descriptor.GetCanonicalUtf8().Length
                || contract.AuthorityKind != bundle.AuthorityKind || contract.AuthorityId != bundle.AuthorityId
                || contract.SelectedAuthorityRelease != bundle.SelectedAuthorityRelease || contract.SelectedAuthorityContentIdentity != bundle.SelectedAuthorityContentIdentity))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Imported receipt descriptor differs from inventory evidence.");
        }
        foreach (var import in projected.PackageApiImports!.OfType<SunderPackageApiImportManifest>())
        {
            if (!inventories.TryGetValue(import.PackageId!, out var inventory)
                || import.BundleRefs is null
                || !inventory.Contracts.Values.Where(static contract => contract.AuthorityKind == "package")
                    .Select(static contract => (ContractId: (string?)contract.ContractId, ContractVersion: (string?)contract.ResolvedContractVersion))
                    .OrderBy(static contract => contract.ContractId, StringComparer.Ordinal)
                    .SequenceEqual(import.BundleRefs.Select(reference => (reference?.ContractId, reference?.ContractVersion))))
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Package API references must exactly cover their selected artifact inventory.");
        }
        foreach (var import in projected.ProviderImports!.OfType<SunderPackageProviderImportManifest>())
        {
            if (!inventories.TryGetValue(import.PackageId!, out var inventory)
                || !inventory.Contracts.TryGetValue(import.ContractId!, out var contract)
                || contract.AuthorityKind != "package" || contract.ResolvedContractVersion != import.ContractVersion)
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Provider import must select a contract from its own artifact inventory.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in root.GetProperty("generatedBindings").EnumerateArray())
        {
            RequireObject(binding, label, ["contractKey", "logicalName"], ["contractKey", "logicalName"], []);
            var key = RequireString(binding, "contractKey", label);
            var name = RequireString(binding, "logicalName", label);
            if (!contracts.ContainsKey(key) || !bindingKeys.Add(key) || !names.Add(name))
                throw new InvalidDataException("Receipt generated bindings must uniquely cover its contracts.");
            var bundle = projected.ContractBundles!.Single(bundle => bundle!.ContractId == contracts[key].ContractId)!;
            var expectedName = bundle.Source == "imported"
                ? "Sunder.Generated.Rpc.Imports." + SunderRpcCSharpImportPrepass.ContractName(contracts[key].ContractId)
                : SunderRpcCSharpNaming.TryCreate(key, null, out var ownedName) ? "Sunder.Generated.Rpc." + ownedName!.Pascal : null;
            if (name != expectedName) throw new InvalidDataException("Receipt binding name does not match its contract.");
        }
        if (!bindingKeys.SetEquals(contracts.Keys)) throw new InvalidDataException("Receipt omits generated bindings.");
        var facadeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var facade in root.GetProperty("generatedPackageFacades").EnumerateArray())
        {
            RequireObject(facade, label, ["packageId", "localName", "logicalName"], ["packageId", "localName", "logicalName"], []);
            var id = RequirePackageId(facade, "packageId", label);
            var name = RequireLocalName(facade, "localName", label);
            if (!facadeIds.Add(id) || !names.Add(RequireString(facade, "logicalName", label))
                || RequireString(facade, "logicalName", label) != "Sunder.Generated.Rpc." + char.ToUpperInvariant(name[0]) + name[1..] + "PackageApi")
                throw new InvalidDataException("Receipt facade identity is invalid or duplicated.");
        }
        if (!facadeIds.SetEquals(projected.PackageApiImports!.Select(static item => item!.PackageId!)))
            throw new InvalidDataException("Receipt facades do not cover its package API imports.");

        var presence = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        foreach (var import in projected.PackageApiImports!) AddPresence(import!.PackageId!, import.Presence!);
        foreach (var import in projected.ProviderImports!) AddPresence(import!.PackageId!, "required");
        while (pending.TryDequeue(out var id))
        {
            if (!artifacts.TryGetValue(id, out var artifact)) continue;
            foreach (var authority in artifact.AuthorityConstraints ?? [])
                if (authority?.AuthorityKind == "package" && authority.AuthorityId is { } owner && owner != id)
                    AddPresence(owner, presence[id]);
        }
        projection["dependsOn"] = ReceiptArray(presence.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => artifacts.ContainsKey(pair.Key)).Select(pair => new JsonObject
            {
                ["packageId"] = pair.Key, ["versionRange"] = artifacts[pair.Key].RequestedVersionRange, ["presence"] = pair.Value,
            }));
        // Receipt graph validation is role-only. Real target/RID membership is checked again by the manifest builder.
        projection["targets"] = new JsonArray(new JsonObject
        {
            ["role"] = appRole || appUseKeys.Count != 0 ? "app" : "runtime", ["rid"] = "linux-x64",
        });
        var manifest = projection.Deserialize<SunderPackageManifest>(ReceiptManifestOptions)!;
        var errors = new List<string>();
        PackageContractValidator.Validate(manifest, new Dictionary<string, string>(), errors, validateDescriptorPayload: false);
        PackageDependencyValidator.Validate(manifest.DependsOn, errors);
        if (errors.Count != 0) throw new InvalidDataException("rpc-authoring.receipt-invalid: " + string.Join(" ", errors));
        return new(manifest, contracts);

        (string Key, SunderRpcContractDescriptor Descriptor, JsonObject Record) Join(JsonElement value)
        {
            var key = RequireString(value, "contractKey", label);
            if (!contracts.TryGetValue(key, out var descriptor)) throw new InvalidDataException("Receipt references an unknown contractKey.");
            var record = JsonNode.Parse(value.GetRawText())!.AsObject();
            record.Remove("contractKey");
            // All joined fields are derived, never accepted from the receipt's keyed records.
            if (record.Any(static pair => pair.Key is "contractId" or "contractVersion" or "version" or "descriptorPath" or "descriptorSha256" or "abiSha256" or "exposure"))
                throw new InvalidDataException("Receipt keyed record contains unsupported derived fields.");
            record["contractId"] = descriptor.ContractId;
            return (key, descriptor, record);
        }

        void AddPresence(string id, string value)
        {
            if (!presence.TryGetValue(id, out var previous) || previous == "optional" && value == "required")
            {
                presence[id] = value;
                pending.Enqueue(id);
            }
        }
    }

    private static IEnumerable<JsonElement> ReceiptRecords(JsonElement root, string name, string[] required, string[]? optional = null)
    {
        optional ??= [];
        var values = RequireProperty(root, name, JsonValueKind.Array, "authoring receipt");
        if (values.GetArrayLength() > 128) throw new InvalidDataException($"Receipt '{name}' exceeds 128 entries.");
        foreach (var value in values.EnumerateArray())
        {
            RequireObject(value, "authoring receipt " + name, [.. required, .. optional], required, optional);
            yield return value;
        }
    }
}
