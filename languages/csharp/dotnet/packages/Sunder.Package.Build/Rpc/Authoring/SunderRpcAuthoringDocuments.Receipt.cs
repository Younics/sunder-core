using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Editor;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    public static byte[] WriteAuthoringReceipt(
        string producerVersion,
        string inputFingerprint,
        SunderRpcContractLock contractLock,
        string packageId,
        string packageVersion,
        SunderRpcCSharpCompilationResult compilation,
        SunderRpcCSharpImportPrepassResult imports,
        string role = "runtime",
        IReadOnlySet<string>? presentRoles = null)
    {
        var parsedLock = ParseContractLock(contractLock.ExactBytes.ToArray(), allowDevelopmentRegistryOrigin: true);
        if (parsedLock.Sha256 != contractLock.Sha256)
        {
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Contract lock bytes changed after validation.");
        }
        contractLock = parsedLock;
        if (role is not ("runtime" or "app")) throw new InvalidDataException("rpc-authoring.receipt-invalid: Unsupported consumer role.");
        presentRoles ??= new HashSet<string>(StringComparer.Ordinal) { role };
        foreach (var import in contractLock.PackageImports.Values)
            if (!import.ConsumerRoles.Any(presentRoles.Contains))
                throw new InvalidDataException($"package-api.consumer-roles-unavailable: '{import.PackageId}' selects [{string.Join(',', import.ConsumerRoles)}], but present roles are [{string.Join(',', presentRoles.Order(StringComparer.Ordinal))}]. Add a matching role or explicitly review the selection.");
        var consumers = contractLock.PackageImports.Values.Where(import => import.ConsumerRoles.Contains(role, StringComparer.Ordinal)).ToArray();
        var providerImports = role == "runtime" ? contractLock.ProviderImports.Values.ToArray() : [];
        var platformImports = role == "runtime" ? contractLock.PlatformImports.Values.ToArray() : [];
        var artifactIds = consumers.Select(import => import.PackageId).Concat(providerImports.Select(import => import.PackageId)).ToHashSet(StringComparer.Ordinal);
        var pendingArtifacts = new Queue<string>(artifactIds);
        while (pendingArtifacts.TryDequeue(out var id))
            foreach (var authority in contractLock.PackageArtifacts[id].Contracts.Values.Where(contract => contract.AuthorityKind == "package" && contract.AuthorityId != id))
                if (contractLock.PackageArtifacts.ContainsKey(authority.AuthorityId) && artifactIds.Add(authority.AuthorityId)) pendingArtifacts.Enqueue(authority.AuthorityId);
        var artifacts = contractLock.PackageArtifacts.Values.Where(artifact => artifactIds.Contains(artifact.PackageId)).ToArray();
        if (!compilation.Success)
        {
            throw new InvalidDataException("rpc-authoring.receipt-invalid: Cannot publish a failed compilation.");
        }
        if (role == "runtime") ValidateImportedProviderReferenceUses(contractLock, compilation);
        ValidatePrivatePackageVersion(packageVersion);
        foreach (var contract in compilation.Contracts.Where(contract => contract.Exposure == SunderServiceExposure.Package))
            if (contract.Descriptor.Version != packageVersion)
                throw new InvalidDataException($"rpc-authoring.private-version-mismatch: Receipt '{contract.ContractKey}' is '{contract.Descriptor.Version}', owning package is '{packageVersion}'.");
        foreach (var requirement in SunderRpcPlatformCatalogCache.Requirements(contractLock))
        {
            if (!imports.PlatformCatalogs.TryGetValue(requirement.ContentIdentity, out var catalog))
                throw new InvalidDataException("rpc-authoring.authority-mismatch: Platform receipt requires its verified original catalog.");
            _ = catalog.VerifyPinned(requirement.ReleaseVersion, requirement.ContentIdentity,
                requirement.Descriptor.ContractId, requirement.Descriptor.ContractVersion,
                requirement.Descriptor.DescriptorSha256, requirement.Descriptor.AbiSha256);
        }
        if (!PackageId.TryParse(packageId, out _) || !SemanticVersion.TryParse(packageVersion, out _)
            || !SemanticVersion.TryParse(producerVersion, out _))
        {
            throw new InvalidDataException("rpc-authoring.receipt-invalid: Invalid package or producer identity.");
        }
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["producer"] = new JsonObject { ["name"] = "Sunder.Package.Build", ["version"] = producerVersion, ["language"] = "csharp" },
            ["inputFingerprint"] = inputFingerprint,
            ["lockSha256"] = contractLock.Sha256,
            ["packageId"] = packageId,
            ["packageVersion"] = packageVersion,
        };
        var identity = JsonSerializer.SerializeToElement(root);
        _ = RequireSha256(identity, "inputFingerprint", "authoring receipt");
        _ = RequireSha256(identity, "lockSha256", "authoring receipt");

        var requiredImports = MaterializedImports(contractLock with
        {
            PackageImports = consumers.ToDictionary(import => import.PackageId, StringComparer.Ordinal),
            ProviderImports = providerImports.ToDictionary(import => import.Key, StringComparer.Ordinal),
            PlatformImports = platformImports.ToDictionary(import => import.Key, StringComparer.Ordinal),
        }, imports);
        var completeImports = MaterializedImports(contractLock, imports);
        if (imports.Contracts.Count != completeImports.Count || imports.Contracts.Any(contract => !completeImports.TryGetValue(contract.ContractKey, out var selected)
                || contract.ContractKey != contract.Descriptor.ContractId
                || contract.GeneratedLogicalName != GeneratedBindingLogicalName(selected.AuthorityKind, selected.AuthorityId, contract.ContractKey)
                || selected.ResolvedContractVersion != contract.Descriptor.Version || selected.DescriptorSha256 != contract.Descriptor.Sha256
                || selected.AbiSha256 != contract.Descriptor.AbiSha256 || selected.DescriptorBytes != contract.Descriptor.GetCanonicalUtf8().Length
                || !SunderRpcCSharpImportPrepass.HasExpectedNaturalProjection(contract, selected, imports.Contracts, completeImports)))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: The complete imported descriptor set differs from the canonical project lock.");
        var roleContracts = imports.Contracts.Where(contract => requiredImports.ContainsKey(contract.ContractKey)).ToArray();
        var importedByKey = roleContracts.ToDictionary(static contract => contract.ContractKey, StringComparer.Ordinal);
        if (importedByKey.Count != requiredImports.Count || roleContracts.Any(contract =>
                contract.ContractKey != contract.Descriptor.ContractId
                || !requiredImports.TryGetValue(contract.ContractKey, out var evidence)
                || contract.GeneratedLogicalName != GeneratedBindingLogicalName(evidence.AuthorityKind, evidence.AuthorityId, contract.ContractKey)
                || evidence.ResolvedContractVersion != contract.Descriptor.Version
                || evidence.DescriptorSha256 != contract.Descriptor.Sha256
                || evidence.AbiSha256 != contract.Descriptor.AbiSha256
                || evidence.DescriptorBytes != contract.Descriptor.GetCanonicalUtf8().Length))
        {
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Imported descriptors/names do not exactly project the lock.");
        }

        var allContracts = compilation.Contracts.Select(contract => new ReceiptContract(
                contract.ContractKey, contract.Exposure, contract.GeneratedLogicalName, contract.Descriptor, contract.ModelProjection, contract.MethodPolicies, contract.AppUse, role == "runtime" ? contract.RuntimeUse : null))
            .Concat(roleContracts.Select(contract => new ReceiptContract(
                contract.ContractKey, SunderServiceExposure.Public, contract.GeneratedLogicalName, contract.Descriptor, contract.ModelProjection, contract.MethodPolicies)))
            .OrderBy(static contract => contract.Key, StringComparer.Ordinal).ToArray();
        if (allContracts.Length > 128)
        {
            throw new InvalidDataException("rpc-authoring.aggregate-materializability-overflow: Receipt exceeds 128 contracts.");
        }
        if (allContracts.Select(static contract => contract.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != allContracts.Length
            || allContracts.Select(static contract => contract.Descriptor.ContractId).Distinct(StringComparer.Ordinal).Count() != allContracts.Length
            || allContracts.Select(static contract => contract.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != allContracts.Length)
        {
            throw new InvalidDataException("rpc-authoring.identity-collision: Owned and imported contract identities/names overlap.");
        }

        var providers = (role == "runtime" ? compilation.Contracts : []).SelectMany(contract => contract.Providers.Select(provider =>
                (ContractKey: contract.ContractKey, Provider: provider)))
            .Concat((role == "runtime" ? compilation.ImportedProviders : []).Select(provider => (provider.Surface.Contract.ContractKey, provider.Provider)))
            .OrderBy(static item => item.Provider.ProviderId, StringComparer.Ordinal).ToArray();
        if (providers.Length > 128)
        {
            throw new InvalidDataException("rpc-authoring.aggregate-materializability-overflow: Receipt exceeds 128 providers.");
        }
        if (providers.Select(static item => item.Provider.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != providers.Length)
        {
            throw new InvalidDataException("rpc-authoring.identity-collision: Receipt provider identities overlap.");
        }
        var surfaces = (role == "runtime" ? imports.ProviderSurfaces : []).ToDictionary(static surface => surface.Key, StringComparer.Ordinal);
        var intents = providerImports.Select(import => (import.Key, import.ContractId))
            .Concat(platformImports.Select(import => (import.Key, import.ContractId))).ToDictionary(import => import.Key, import => import.ContractId, StringComparer.Ordinal);
        if (surfaces.Count != intents.Count || surfaces.Any(pair =>
                !intents.TryGetValue(pair.Key, out var contractId)
                || !importedByKey.TryGetValue(contractId, out var contract)
                || pair.Value.Contract != contract
                || pair.Value.IsPlatform != contractLock.PlatformImports.ContainsKey(pair.Key)
                || !pair.Value.IsPlatform && !pair.Value.IsNatural
                || pair.Value.InterfaceMetadataName != (pair.Value.IsPlatform
                    ? "Sunder.Generated.Rpc.I" + char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..] + "Service"
                    : SunderRpcCSharpImportPrepass.NaturalProviderImportInterface(contractLock.ProviderImports[pair.Key].PackageId, pair.Key)))
            || role == "runtime" && compilation.ImportedProviders.Any(provider =>
                !surfaces.TryGetValue(provider.Surface.Key, out var surface) || provider.Surface != surface))
        {
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Imported provider surfaces do not exactly project the lock.");
        }

        root["contracts"] = ReceiptArray(allContracts.Select(contract =>
        {
            var record = new JsonObject
            {
                ["contractKey"] = contract.Key,
                ["contractId"] = contract.Descriptor.ContractId,
                ["contractVersion"] = contract.Descriptor.Version,
                ["exposure"] = Exposure(contract.Exposure),
                ["descriptorBytes"] = contract.Descriptor.GetCanonicalUtf8().Length,
                ["canonicalDescriptor"] = Utf8NoBom.GetString(contract.Descriptor.GetCanonicalUtf8()),
                ["descriptorSha256"] = contract.Descriptor.Sha256,
                ["abiSha256"] = contract.Descriptor.AbiSha256,
                ["modelProjection"] = contract.ModelProjection.ToJson(),
            };
            contract.MethodPolicies.Validate(contract.Descriptor);
            if (!contract.MethodPolicies.IsEmpty) record["methodPolicies"] = contract.MethodPolicies.ToJson();
            if (contract.AppUse is { } policy)
                record["appUse"] = ReceiptUsePolicy(policy);
            if (contract.RuntimeUse is { } runtimePolicy)
                record["runtimeUse"] = ReceiptUsePolicy(runtimePolicy);
            return record;
        }));

        // Keep the complete artifact inventory, including metadata-only platform members.
        using var lockDocument = JsonDocument.Parse(contractLock.ExactBytes);
        root["packageArtifacts"] = ReceiptArray(lockDocument.RootElement.GetProperty("packageArtifacts").EnumerateObject().Where(artifact => artifactIds.Contains(artifact.Name)).Select(artifact =>
        {
            var record = new JsonObject { ["packageId"] = artifact.Name };
            foreach (var member in artifact.Value.EnumerateObject())
            {
                if (member.Name == "contracts")
                {
                    record[member.Name] = ReceiptArray(member.Value.EnumerateObject().Select(contract =>
                    {
                        var value = new JsonObject { ["contractId"] = contract.Name };
                        foreach (var field in contract.Value.EnumerateObject()) value[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                        return value;
                    }));
                }
                else record[member.Name] = JsonNode.Parse(member.Value.GetRawText());
            }
            return record;
        }));
        root["bundles"] = ReceiptArray(allContracts.Select(contract =>
        {
            var bundle = new JsonObject { ["contractKey"] = contract.Key };
            if (importedByKey.ContainsKey(contract.Key))
            {
                bundle["source"] = "imported";
                foreach (var pair in ReceiptAuthority(requiredImports[contract.Key])) bundle[pair.Key] = pair.Value!.DeepClone();
            }
            else
            {
                bundle["source"] = "owned";
                bundle["authorityKind"] = "package";
                bundle["authorityId"] = packageId;
            }
            return bundle;
        }));
        root["packageArtifactImports"] = ReceiptArray(artifacts
            .OrderBy(static artifact => artifact.PackageId, StringComparer.Ordinal).Select(artifact => new JsonObject
            {
                ["packageId"] = artifact.PackageId,
                ["requestedVersionRange"] = artifact.RequestedVersionRange,
                ["includePrerelease"] = artifact.IncludePrerelease,
                ["selectedPackageVersion"] = artifact.SelectedPackageVersion,
                ["selectedPackageContentIdentity"] = artifact.SelectedPackageContentIdentity,
                ["publicContractCount"] = artifact.PublicContractCount,
                ["publicInventorySha256"] = artifact.PublicInventorySha256,
                ["authorityConstraints"] = ReceiptArray(artifact.Contracts.Values
                    .DistinctBy(static contract => (contract.AuthorityKind, contract.AuthorityId))
                    .OrderBy(static contract => contract.AuthorityKind, StringComparer.Ordinal)
                    .ThenBy(static contract => contract.AuthorityId, StringComparer.Ordinal).Select(ReceiptAuthority)),
            }));
        root["packageApiImports"] = ReceiptArray(consumers
            .OrderBy(static import => import.PackageId, StringComparer.Ordinal).Select(import =>
            {
                var contracts = contractLock.PackageArtifacts[import.PackageId].Contracts.Values
                    .Where(static contract => contract.AuthorityKind == "package")
                    .OrderBy(static contract => contract.ContractId, StringComparer.Ordinal).ToArray();
                return new JsonObject
                {
                    ["packageId"] = import.PackageId,
                    ["presence"] = import.Presence,
                    ["bundleRefs"] = ReceiptArray(contracts.Select(contract => ReceiptReference(contract.ContractId, contract.ResolvedContractVersion))),
                    ["useRefs"] = ReceiptArray(contracts.Select(contract => ReceiptReference(contract.ContractId, contract.ResolvedContractVersion, role))),
                };
            }));
        root["providerImports"] = ReceiptArray(providerImports.OrderBy(static import => import.Key, StringComparer.Ordinal).Select(import =>
        {
            var contract = importedByKey[import.ContractId];
            if (!compilation.ImportedProviders.Any(provider => provider.Surface.Key == import.Key))
            {
                throw new InvalidDataException($"rpc-authoring.handler-incomplete: Provider import '{import.Key}' has no local registration.");
            }
            var refs = compilation.ImportedProviders.Where(provider => provider.Surface.Contract.ContractKey == import.ContractId)
                .OrderBy(static provider => provider.Provider.ProviderId, StringComparer.Ordinal).ToArray();
            return new JsonObject
            {
                ["key"] = import.Key,
                ["packageId"] = import.PackageId,
                ["contractId"] = import.ContractId,
                ["contractVersion"] = contract.Descriptor.Version,
                ["bundleRef"] = ReceiptReference(import.ContractId, contract.Descriptor.Version),
                ["providerRefs"] = ReceiptArray(refs.Select(provider => new JsonObject
                {
                    ["role"] = "runtime",
                    ["providerId"] = provider.Provider.ProviderId,
                    ["contractId"] = import.ContractId,
                    ["contractVersion"] = contract.Descriptor.Version,
                })),
            };
        }));
        root["platformImports"] = ReceiptArray(platformImports.OrderBy(import => import.Key, StringComparer.Ordinal).Select(import =>
        {
            var refs = compilation.ImportedProviders.Where(provider => provider.Surface.Key == import.Key)
                .OrderBy(provider => provider.Provider.ProviderId, StringComparer.Ordinal).ToArray();
            if (refs.Length == 0) throw new InvalidDataException($"rpc-authoring.handler-incomplete: Platform import '{import.Key}' has no local registration.");
            return new JsonObject
            {
                ["key"] = import.Key, ["authorityKind"] = "platform", ["authorityId"] = import.AuthorityId,
                ["authorityReleaseRange"] = import.AuthorityReleaseRange, ["includePrerelease"] = false,
                ["selectedAuthorityRelease"] = import.SelectedAuthorityRelease, ["selectedAuthorityContentIdentity"] = import.SelectedAuthorityContentIdentity,
                ["contractId"] = import.ContractId, ["contractVersion"] = import.ResolvedContractVersion,
                ["bundleRef"] = ReceiptReference(import.ContractId, import.ResolvedContractVersion),
                ["providerRefs"] = ReceiptArray(refs.Select(provider => new JsonObject
                {
                    ["role"] = "runtime", ["providerId"] = provider.Provider.ProviderId,
                    ["contractId"] = import.ContractId, ["contractVersion"] = import.ResolvedContractVersion,
                })),
            };
        }));
        root["providers"] = ReceiptArray(providers.Select(item => new JsonObject
        {
            ["providerId"] = item.Provider.ProviderId,
            ["role"] = "runtime",
            ["audience"] = Audience(item.Provider.Audience),
            ["contractKey"] = item.ContractKey,
        }));
        var uses = consumers.SelectMany(import =>
                contractLock.PackageArtifacts[import.PackageId].Contracts.Values
                    .Where(static contract => contract.AuthorityKind == "package")
                    .Select(contract => (contract.ContractId, Required: import.RequiredContracts.Contains(contract.ContractId, StringComparer.Ordinal))))
            .GroupBy(static item => item.ContractId, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal);
        var importedUses = uses.Select(group => new JsonObject
        {
            ["role"] = role,
            ["required"] = group.Any(static use => use.Required),
            ["actions"] = new JsonArray(ReceiptActions(importedByKey[group.Key].Descriptor).Select(static action => (JsonNode?)JsonValue.Create(action)).ToArray()),
            ["contractKey"] = group.Key,
        });
        var ownedUses = compilation.Contracts.Select(contract => (Contract: contract, Policy: role == "runtime" ? contract.RuntimeUse : ResolveAppUse(contract.Exposure, contract.Descriptor, contract.AppUse)))
            .Where(value => value.Policy is not null).Select(value => new JsonObject
        {
            ["role"] = role,
            ["required"] = value.Policy!.Required,
            ["actions"] = new JsonArray(value.Policy.Actions.Select(action => JsonValue.Create(action)).ToArray()),
            ["contractKey"] = value.Contract.ContractKey,
        });
        var typedUses = role == "runtime" ? compilation.ImportedUses : [];
        var consumerIds = uses.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var explicitUses = typedUses.Where(use => !consumerIds.Contains(use.Contract.ContractKey)).DistinctBy(use => use.Contract.ContractKey).Select(use => new JsonObject
        {
            ["role"] = role, ["required"] = use.Policy.Required,
            ["actions"] = new JsonArray(use.Policy.Actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray()),
            ["contractKey"] = use.Contract.ContractKey,
        });
        root["uses"] = ReceiptArray(importedUses.Concat(ownedUses).Concat(explicitUses).OrderBy(use => use["contractKey"]!.GetValue<string>(), StringComparer.Ordinal));
        root["generatedPackageFacades"] = ReceiptArray(consumers
            .OrderBy(static import => import.PackageId, StringComparer.Ordinal).Select(import => new JsonObject
            {
                ["packageId"] = import.PackageId,
                ["localName"] = import.LocalName,
                ["logicalName"] = GeneratedPackageFacadeLogicalName(import.PackageId, import.LocalName),
            }));
        root["generatedBindings"] = ReceiptArray(allContracts.Select(contract => new JsonObject
        {
            ["contractKey"] = contract.Key,
            ["logicalName"] = contract.LogicalName,
        }));
        if (role == "app") root["role"] = "app";
        if (typedUses.Count != 0) root["roleUses"] = RoleUsesJson(typedUses.Select(use => use.Evidence));

        var bytes = WriteDeterministicDocument(JsonSerializer.SerializeToElement(root));
        if (bytes.Length > MaximumGeneratedDocumentBytes)
        {
            throw new InvalidDataException("rpc-authoring.aggregate-materializability-overflow: Receipt exceeds the generated document byte limit.");
        }
        return bytes;
    }

    public static void ValidateAuthoringReceipt(string path, ReadOnlySpan<byte> expectedBytes)
    {
        var (actual, document) = ReadStrictDocument(path, MaximumGeneratedDocumentBytes, "Sunder RPC authoring receipt");
        using (document)
        {
            if (!actual.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new InvalidDataException("rpc-authoring.evidence-mismatch: Authoring receipt differs from the complete validated input projection.");
            }
        }
    }

    private static JsonArray ReceiptArray(IEnumerable<JsonObject> records) => new(records.Cast<JsonNode>().ToArray());

    private static JsonObject ReceiptAuthority(SunderRpcLockedArtifactContract contract)
    {
        var result = new JsonObject
        {
            ["authorityKind"] = contract.AuthorityKind,
            ["authorityId"] = contract.AuthorityId,
            ["selectedAuthorityRelease"] = contract.SelectedAuthorityRelease,
            ["selectedAuthorityContentIdentity"] = contract.SelectedAuthorityContentIdentity,
        };
        return result;
    }

    private static JsonObject ReceiptReference(string contractId, string contractVersion, string? role = null)
    {
        var result = new JsonObject();
        if (role is not null) result["role"] = role;
        result["contractId"] = contractId;
        result["contractVersion"] = contractVersion;
        return result;
    }

    internal static IEnumerable<string> ReceiptActions(SunderRpcContractDescriptor descriptor)
    {
        yield return SunderRpcProtocol.DiscoverAction;
        var methods = descriptor.Services.SelectMany(static service => service.Methods).ToArray();
        if (methods.Any(static method => method.Kind == SunderRpcMethodKind.Unary)) yield return SunderRpcProtocol.InvokeAction;
        if (methods.Any(static method => method.Kind == SunderRpcMethodKind.ServerStream)) yield return SunderRpcProtocol.SubscribeAction;
    }

    private static JsonObject ReceiptUsePolicy(SunderRpcOwnedUsePolicy policy) => new()
    {
        ["required"] = policy.Required,
        ["actions"] = new JsonArray(policy.Actions.Select(action => JsonValue.Create(action)).ToArray()),
    };

    private sealed record ReceiptContract(string Key, SunderServiceExposure Exposure, string LogicalName, SunderRpcContractDescriptor Descriptor,
        SunderRpcModelProjection ModelProjection, SunderRpcMethodPolicies MethodPolicies, SunderRpcOwnedUsePolicy? AppUse = null, SunderRpcOwnedUsePolicy? RuntimeUse = null);

    // Package transport uses the validated ultimate authority, whereas a selected
    // package facade belongs to the selected package. Platform bindings retain
    // their explicit descriptor-only tooling namespace and have no package facade.
    private static string GeneratedBindingLogicalName(string authorityKind, string authorityId, string contractId)
        => authorityKind switch
        {
            "package" => GeneratedPackageNames.Transport(authorityId, contractId),
            "platform" when authorityId == "sunder.core" => "Sunder.Generated.Rpc.Imports." + SunderRpcCSharpImportPrepass.ContractName(contractId),
            _ => throw new InvalidDataException("rpc-authoring.authority-mismatch: Generated bindings require validated package or Core platform authority."),
        };

    private static string GeneratedPackageFacadeLogicalName(string packageId, string localName)
        => GeneratedPackageNames.Internal(packageId) + "." + char.ToUpperInvariant(localName[0]) + localName[1..] + "PackageApi";
}
