using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal static class PackageContractValidator
{
    public static void Validate(
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors)
    {
        var localContracts = ValidateBundles(manifest.ContractBundles, physicalFiles, errors);
        ValidateUses(manifest.UsesContracts, localContracts, errors);
        ValidateProviders(manifest, localContracts, errors);
    }

    private static IReadOnlyDictionary<string, LocalContract> ValidateBundles(
        IReadOnlyList<SunderPackageContractBundleManifest?>? bundles,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors)
    {
        if (bundles is { Count: > SunderPackageFormat.MaxContractBundles })
        {
            errors.Add($"Package manifest declares {bundles.Count} contract bundles; the limit is {SunderPackageFormat.MaxContractBundles}.");
        }

        var contracts = new Dictionary<string, LocalContract>(StringComparer.Ordinal);
        foreach (var bundle in (bundles ?? []).Take(SunderPackageFormat.MaxContractBundles))
        {
            if (bundle is null)
            {
                errors.Add("Package contract bundle entry is null.");
                continue;
            }

            var validId = PackageId.TryParse(bundle.ContractId, out _);
            var validVersion = SemanticVersion.TryParse(bundle.Version, out _);
            if (!validId)
            {
                errors.Add($"Contract id '{bundle.ContractId}' must use lowercase dot-separated ASCII identifiers.");
            }
            if (!validVersion)
            {
                errors.Add($"Contract '{bundle.ContractId ?? "unknown"}' version '{bundle.Version}' must be strict SemVer 2.0.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(bundle.Sha256))
            {
                errors.Add($"Contract bundle '{bundle.ContractId ?? "unknown"}' must declare a lowercase SHA-256 hash.");
            }

            if (!PackageArchivePathValidator.TryParse(
                    bundle.DescriptorPath,
                    $"contract '{bundle.ContractId ?? "unknown"}' descriptorPath",
                    errors,
                    out var descriptorPath,
                    required: true))
            {
                continue;
            }
            var logical = descriptorPath.ToString();
            var physicalPath = SunderPackageFormat.SharedPayloadRoot + logical;
            if (!physicalFiles.TryGetValue(physicalPath, out var filePath))
            {
                errors.Add($"Contract descriptor '{logical}' must exist physically at '{physicalPath}'.");
                continue;
            }
            var length = new FileInfo(filePath).Length;
            if (length <= 0 || length > SunderPackageFormat.MaxContractDescriptorBytes)
            {
                errors.Add($"Contract descriptor '{logical}' must be non-empty and {SunderPackageFormat.MaxContractDescriptorBytes} bytes or smaller.");
                continue;
            }
            SunderRpcContractDescriptor descriptor;
            try
            {
                descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(filePath));
            }
            catch (Exception exception) when (exception is SunderRpcDescriptorException
                                                   or IOException
                                                   or UnauthorizedAccessException)
            {
                errors.Add($"Contract descriptor '{logical}' is invalid: {exception.Message}");
                continue;
            }
            if (!string.Equals(descriptor.ContractId, bundle.ContractId, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Contract descriptor '{logical}' contractId '{descriptor.ContractId}' does not match bundle contractId '{bundle.ContractId}'.");
            }
            if (!string.Equals(descriptor.Version, bundle.Version, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Contract descriptor '{logical}' version '{descriptor.Version}' does not match bundle version '{bundle.Version}'.");
            }
            if (PackageContentSignatureValidator.IsLowercaseSha256(bundle.Sha256)
                && !string.Equals(descriptor.Sha256, bundle.Sha256, StringComparison.Ordinal))
            {
                errors.Add($"Contract descriptor '{logical}' SHA-256 mismatch.");
            }
            if (validId && validVersion)
            {
                var key = ContractKey(bundle.ContractId!, bundle.Version!);
                if (!contracts.TryAdd(
                        key,
                        new LocalContract(
                            bundle.ContractId!,
                            bundle.Version!,
                            bundle.Sha256 ?? string.Empty,
                            descriptor)))
                {
                    errors.Add($"Contract bundle '{bundle.ContractId}' version '{bundle.Version}' is declared more than once.");
                }
            }
        }

        return contracts;
    }

    private static void ValidateUses(
        IReadOnlyList<SunderPackageContractUseManifest?>? uses,
        IReadOnlyDictionary<string, LocalContract> localContracts,
        ICollection<string> errors)
    {
        if (uses is { Count: > SunderPackageFormat.MaxContractUses })
        {
            errors.Add($"Package manifest declares {uses.Count} contract uses; the limit is {SunderPackageFormat.MaxContractUses}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var use in (uses ?? []).Take(SunderPackageFormat.MaxContractUses))
        {
            if (use is null)
            {
                errors.Add("Package contract use entry is null.");
                continue;
            }
            if (!PackageId.TryParse(use.ContractId, out _))
            {
                errors.Add($"Used contract id '{use.ContractId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seen.Add(use.ContractId!))
            {
                errors.Add($"Contract use '{use.ContractId}' is declared more than once.");
            }
            if (!PackageVersionRange.TryParse(use.VersionRange, out var versionRange))
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' has unsupported versionRange '{use.VersionRange}'.");
            }
            else if (use.ContractId is not null
                     && !localContracts.Values.Any(contract =>
                         string.Equals(contract.ContractId, use.ContractId, StringComparison.Ordinal)
                         && SemanticVersion.TryParse(contract.Version, out var version)
                         && versionRange.IsSatisfiedBy(version)))
            {
                errors.Add(
                    $"Contract use '{use.ContractId}' range '{use.VersionRange}' requires a matching local contract bundle.");
            }
            if (use.Required is null)
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' must declare required.");
            }
            ValidateActions(use, errors);
        }
    }

    private static void ValidateActions(SunderPackageContractUseManifest use, ICollection<string> errors)
    {
        if (use.Actions is null || use.Actions.Count == 0)
        {
            errors.Add($"Contract use '{use.ContractId ?? "unknown"}' must declare at least one action.");
            return;
        }
        if (use.Actions.Count > 3)
        {
            errors.Add($"Contract use '{use.ContractId ?? "unknown"}' declares more than three actions.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in use.Actions.Take(3))
        {
            if (!SunderPackageFormat.IsContractAction(action))
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' declares unknown action '{action}'.");
            }
            else if (!seen.Add(action!))
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' declares action '{action}' more than once.");
            }
        }
    }

    private static void ValidateProviders(
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, LocalContract> localContracts,
        ICollection<string> errors)
    {
        var providers = manifest.Provides;
        if (providers is { Count: > SunderPackageFormat.MaxProviders })
        {
            errors.Add($"Package manifest declares {providers.Count} providers; the limit is {SunderPackageFormat.MaxProviders}.");
        }

        var targetRoles = (manifest.Targets ?? [])
            .Where(static target => target is not null && SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out _))
            .Select(static target => target!.Role!)
            .ToHashSet(StringComparer.Ordinal);
        var seenProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in (providers ?? []).Take(SunderPackageFormat.MaxProviders))
        {
            if (provider is null)
            {
                errors.Add("Package provider entry is null.");
                continue;
            }
            if (!PackageId.TryParse(provider.ProviderId, out _))
            {
                errors.Add($"Provider id '{provider.ProviderId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenProviders.Add(provider.ProviderId + "\0" + provider.Role))
            {
                errors.Add($"Provider id '{provider.ProviderId}' is declared more than once for role '{provider.Role}'.");
            }
            if (!PackageId.TryParse(provider.ContractId, out _))
            {
                errors.Add($"Provider contract id '{provider.ContractId}' must use lowercase dot-separated ASCII identifiers.");
            }
            if (!SemanticVersion.TryParse(provider.ContractVersion, out _))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' contractVersion '{provider.ContractVersion}' must be strict SemVer 2.0.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(provider.ContractSha256))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' must declare a lowercase contractSha256.");
            }
            if (!SunderPackageFormat.IsHostRole(provider.Role))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' declares unknown role '{provider.Role}'.");
            }
            else if (!targetRoles.Contains(provider.Role!))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' role '{provider.Role}' has no package target.");
            }

            if (provider.ContractId is not null && provider.ContractVersion is not null)
            {
                if (!localContracts.TryGetValue(
                        ContractKey(provider.ContractId, provider.ContractVersion),
                        out var localContract))
                {
                    errors.Add(
                        $"Provider '{provider.ProviderId ?? "unknown"}' requires a matching local contract bundle.");
                }
                else if (!string.Equals(localContract.Sha256, provider.ContractSha256, StringComparison.Ordinal))
                {
                    errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' contractSha256 does not match its local contract bundle.");
                }
            }
        }
    }

    private static string ContractKey(string contractId, string version) => contractId + "\0" + version;

    private sealed record LocalContract(
        string ContractId,
        string Version,
        string Sha256,
        SunderRpcContractDescriptor Descriptor);
}
