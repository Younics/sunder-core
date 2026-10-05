using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

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
        SunderRpcCSharpImportPrepassResult imports)
    {
        var parsedLock = ParseContractLock(contractLock.ExactBytes.ToArray(), allowDevelopmentRegistryOrigin: true);
        if (parsedLock.Sha256 != contractLock.Sha256)
        {
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Contract lock bytes changed after validation.");
        }
        contractLock = parsedLock;
        if (!compilation.Success)
        {
            throw new InvalidDataException("rpc-authoring.receipt-invalid: Cannot publish a failed compilation.");
        }
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

        var requiredImports = contractLock.PackageImports.Values
            .SelectMany(import => contractLock.PackageArtifacts[import.PackageId].Contracts.Values
                .Where(static contract => contract.AuthorityKind == "package"))
            .Concat(contractLock.ProviderImports.Values.Select(import =>
                contractLock.PackageArtifacts[import.PackageId].Contracts[import.ContractId]))
            .Concat(contractLock.PlatformImports.Values.Select(SunderRpcPlatformCatalogCache.AsArtifactContract))
            .GroupBy(static contract => contract.ContractId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group =>
            {
                var first = group.First();
                if (group.Any(contract => contract.ResolvedContractVersion != first.ResolvedContractVersion))
                {
                    throw new InvalidDataException($"rpc-authoring.identity-collision: Imported contract '{group.Key}' selects more than one version.");
                }
                return first;
            }, StringComparer.Ordinal);
        var importedByKey = imports.Contracts.ToDictionary(static contract => contract.ContractKey, StringComparer.Ordinal);
        if (importedByKey.Count != requiredImports.Count || imports.Contracts.Any(contract =>
                contract.ContractKey != contract.Descriptor.ContractId
                || contract.GeneratedLogicalName != "Sunder.Generated.Rpc.Imports." + SunderRpcCSharpImportPrepass.ContractName(contract.ContractKey)
                || !requiredImports.TryGetValue(contract.ContractKey, out var evidence)
                || evidence.ResolvedContractVersion != contract.Descriptor.Version
                || evidence.DescriptorSha256 != contract.Descriptor.Sha256
                || evidence.AbiSha256 != contract.Descriptor.AbiSha256
                || evidence.DescriptorBytes != contract.Descriptor.GetCanonicalUtf8().Length))
        {
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Imported descriptors/names do not exactly project the lock.");
        }

        var allContracts = compilation.Contracts.Select(contract => new ReceiptContract(
                contract.ContractKey, contract.Exposure, contract.GeneratedLogicalName, contract.Descriptor, contract.AppUse, contract.RuntimeUse))
            .Concat(imports.Contracts.Select(contract => new ReceiptContract(
                contract.ContractKey, SunderRpcExposure.Public, contract.GeneratedLogicalName, contract.Descriptor)))
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

        var providers = compilation.Contracts.SelectMany(contract => contract.Providers.Select(provider =>
                (ContractKey: contract.ContractKey, Provider: provider)))
            .Concat(compilation.ImportedProviders.Select(provider => (provider.Surface.Contract.ContractKey, provider.Provider)))
            .OrderBy(static item => item.Provider.ProviderId, StringComparer.Ordinal).ToArray();
        if (providers.Length > 128)
        {
            throw new InvalidDataException("rpc-authoring.aggregate-materializability-overflow: Receipt exceeds 128 providers.");
        }
        if (providers.Select(static item => item.Provider.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != providers.Length)
        {
            throw new InvalidDataException("rpc-authoring.identity-collision: Receipt provider identities overlap.");
        }
        var surfaces = imports.ProviderSurfaces.ToDictionary(static surface => surface.Key, StringComparer.Ordinal);
        var intents = contractLock.ProviderImports.Values.Select(import => (import.Key, import.ContractId))
            .Concat(contractLock.PlatformImports.Values.Select(import => (import.Key, import.ContractId))).ToDictionary(import => import.Key, import => import.ContractId, StringComparer.Ordinal);
        if (surfaces.Count != intents.Count || surfaces.Any(pair =>
                !intents.TryGetValue(pair.Key, out var contractId)
                || !importedByKey.TryGetValue(contractId, out var contract)
                || pair.Value.Contract != contract
                || pair.Value.InterfaceMetadataName != "Sunder.Generated.Rpc.I" + char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..] + "Service")
            || compilation.ImportedProviders.Any(provider =>
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
            };
            if (contract.AppUse is { } policy)
                record["appUse"] = ReceiptUsePolicy(policy);
            if (contract.RuntimeUse is { } runtimePolicy)
                record["runtimeUse"] = ReceiptUsePolicy(runtimePolicy);
            return record;
        }));

        // Keep the complete artifact inventory, including metadata-only platform members.
        using var lockDocument = JsonDocument.Parse(contractLock.ExactBytes);
        root["packageArtifacts"] = ReceiptArray(lockDocument.RootElement.GetProperty("packageArtifacts").EnumerateObject().Select(artifact =>
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
        root["packageArtifactImports"] = ReceiptArray(contractLock.PackageArtifacts.Values
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
        root["packageApiImports"] = ReceiptArray(contractLock.PackageImports.Values
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
                    ["useRefs"] = ReceiptArray(contracts.Select(contract => ReceiptReference(contract.ContractId, contract.ResolvedContractVersion, "runtime"))),
                };
            }));
        root["providerImports"] = ReceiptArray(contractLock.ProviderImports.Values.OrderBy(static import => import.Key, StringComparer.Ordinal).Select(import =>
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
        root["platformImports"] = ReceiptArray(contractLock.PlatformImports.Values.OrderBy(import => import.Key, StringComparer.Ordinal).Select(import =>
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
        var uses = contractLock.PackageImports.Values.SelectMany(import =>
                contractLock.PackageArtifacts[import.PackageId].Contracts.Values
                    .Where(static contract => contract.AuthorityKind == "package")
                    .Select(contract => (contract.ContractId, Required: import.RequiredContracts.Contains(contract.ContractId, StringComparer.Ordinal))))
            .GroupBy(static item => item.ContractId, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal);
        var importedUses = uses.Select(group => new JsonObject
        {
            ["role"] = "runtime",
            ["required"] = group.Any(static use => use.Required),
            ["actions"] = new JsonArray(ReceiptActions(importedByKey[group.Key].Descriptor).Select(static action => (JsonNode?)JsonValue.Create(action)).ToArray()),
            ["contractKey"] = group.Key,
        });
        var ownedUses = compilation.Contracts.Where(contract => contract.RuntimeUse is not null).Select(contract => new JsonObject
        {
            ["role"] = "runtime",
            ["required"] = contract.RuntimeUse!.Required,
            ["actions"] = new JsonArray(contract.RuntimeUse.Actions.Select(action => JsonValue.Create(action)).ToArray()),
            ["contractKey"] = contract.ContractKey,
        });
        root["uses"] = ReceiptArray(importedUses.Concat(ownedUses).OrderBy(use => use["contractKey"]!.GetValue<string>(), StringComparer.Ordinal));
        root["generatedPackageFacades"] = ReceiptArray(contractLock.PackageImports.Values
            .OrderBy(static import => import.PackageId, StringComparer.Ordinal).Select(import => new JsonObject
            {
                ["packageId"] = import.PackageId,
                ["localName"] = import.LocalName,
                ["logicalName"] = "Sunder.Generated.Rpc." + char.ToUpperInvariant(import.LocalName[0]) + import.LocalName[1..] + "PackageApi",
            }));
        root["generatedBindings"] = ReceiptArray(allContracts.Select(contract => new JsonObject
        {
            ["contractKey"] = contract.Key,
            ["logicalName"] = contract.LogicalName,
        }));

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

    private sealed record ReceiptContract(string Key, SunderRpcExposure Exposure, string LogicalName, SunderRpcContractDescriptor Descriptor,
        SunderRpcOwnedUsePolicy? AppUse = null, SunderRpcOwnedUsePolicy? RuntimeUse = null);
}
