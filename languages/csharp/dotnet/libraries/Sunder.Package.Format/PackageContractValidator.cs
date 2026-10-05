using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal static class PackageContractValidator
{
    public static void Validate(
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors,
        bool validateDescriptorPayload = true)
    {
        if (manifest is null)
        {
            errors.Add("Package manifest is null.");
            return;
        }
        PackageRpcImportValidator.Validate(manifest, errors);
        var localContracts = ValidateBundles(
            manifest.ContractBundles,
            physicalFiles,
            errors,
            validateDescriptorPayload);
        var targetRoles = (manifest.Targets ?? [])
            .Take(SunderPackageFormat.MaxTargets)
            .Where(static target => target is not null && SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out _))
            .Select(static target => target!.Role!)
            .ToHashSet(StringComparer.Ordinal);
        var packageApiUses = (manifest.PackageApiImports ?? []).Take(SunderPackageFormat.MaxContractUses)
            .Where(static import => import is not null)
            .SelectMany(static import => (import!.UseRefs ?? []).Take(SunderPackageFormat.MaxContractUses))
            .OfType<SunderPackageContractUseReferenceManifest>()
            .Select(static reference => RoleKey(reference.Role ?? string.Empty,
                ContractKey(reference.ContractId ?? string.Empty, reference.ContractVersion ?? string.Empty)))
            .ToHashSet(StringComparer.Ordinal);
        ValidateUses(manifest.UsesContracts, localContracts, targetRoles, packageApiUses, errors);
        ValidateProviders(manifest.Provides, localContracts, targetRoles, errors);
    }

    private static IReadOnlyDictionary<string, LocalContract> ValidateBundles(
        IReadOnlyList<SunderPackageContractBundleManifest?>? bundles,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors,
        bool validateDescriptorPayload)
    {
        if (bundles is { Count: > SunderPackageFormat.MaxContractBundles })
        {
            errors.Add($"Package manifest declares {bundles.Count} contract bundles; the limit is {SunderPackageFormat.MaxContractBundles}.");
        }

        var contracts = new Dictionary<string, LocalContract>(StringComparer.Ordinal);
        var bundleKeys = new HashSet<string>(StringComparer.Ordinal);
        var descriptorPaths = new ArchivePathRegistry();
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
            if (!PackageContentSignatureValidator.IsLowercaseSha256(bundle.DescriptorSha256))
            {
                errors.Add($"Contract bundle '{bundle.ContractId ?? "unknown"}' must declare a lowercase descriptorSha256.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(bundle.AbiSha256))
            {
                errors.Add($"Contract bundle '{bundle.ContractId ?? "unknown"}' must declare a lowercase abiSha256.");
            }
            if (!SunderPackageFormat.IsContractExposure(bundle.Exposure))
            {
                errors.Add($"Contract bundle '{bundle.ContractId ?? "unknown"}' declares invalid exposure '{bundle.Exposure}'.");
            }

            IReadOnlyList<string>? applicableActions = null;
            if (PackageArchivePathValidator.TryParse(
                    bundle.DescriptorPath,
                    $"contract '{bundle.ContractId ?? "unknown"}' descriptorPath",
                    errors,
                    out var descriptorPath,
                    required: true))
            {
                var logical = descriptorPath.ToString();
                try
                {
                    descriptorPaths.Register(descriptorPath, isDirectory: false);
                }
                catch (InvalidDataException exception)
                {
                    errors.Add($"Contract descriptorPath '{logical}' must be portable and unique: {exception.Message}");
                }

                if (validateDescriptorPayload)
                {
                    applicableActions = ValidateDescriptor(bundle, logical, physicalFiles, errors);
                }
            }

            var duplicateKey = false;
            if (bundle.ContractId is not null && bundle.Version is not null)
            {
                duplicateKey = !bundleKeys.Add(ContractKey(bundle.ContractId, bundle.Version));
                if (duplicateKey)
                {
                    errors.Add($"Contract bundle '{bundle.ContractId}' version '{bundle.Version}' is declared more than once.");
                }
            }

            if (validId && validVersion && !duplicateKey)
            {
                contracts.Add(
                    ContractKey(bundle.ContractId!, bundle.Version!),
                    new LocalContract(
                        bundle.DescriptorSha256 ?? string.Empty,
                        bundle.AbiSha256 ?? string.Empty,
                        bundle.Exposure ?? string.Empty,
                        applicableActions));
            }
        }

        return contracts;
    }

    private static IReadOnlyList<string>? ValidateDescriptor(
        SunderPackageContractBundleManifest bundle,
        string logicalPath,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors)
    {
        var physicalPath = SunderPackageFormat.SharedPayloadRoot + logicalPath;
        if (!physicalFiles.TryGetValue(physicalPath, out var filePath))
        {
            errors.Add($"Contract descriptor '{logicalPath}' must exist physically at '{physicalPath}'.");
            return null;
        }

        SunderRpcContractDescriptor descriptor;
        try
        {
            using var stream = File.OpenRead(filePath);
            var length = stream.Length;
            if (length <= 0 || length > SunderPackageFormat.MaxContractDescriptorBytes)
            {
                errors.Add($"Contract descriptor '{logicalPath}' must be non-empty and {SunderPackageFormat.MaxContractDescriptorBytes} bytes or smaller.");
                return null;
            }
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                errors.Add($"Contract descriptor '{logicalPath}' changed size while being read.");
                return null;
            }
            descriptor = SunderRpcContractDescriptor.Parse(bytes);
        }
        catch (Exception exception) when (exception is SunderRpcDescriptorException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException)
        {
            errors.Add($"Contract descriptor '{logicalPath}' is invalid: {exception.Message}");
            return null;
        }
        if (!string.Equals(descriptor.ContractId, bundle.ContractId, StringComparison.Ordinal))
        {
            errors.Add(
                $"Contract descriptor '{logicalPath}' contractId '{descriptor.ContractId}' does not match bundle contractId '{bundle.ContractId}'.");
        }
        if (!string.Equals(descriptor.Version, bundle.Version, StringComparison.Ordinal))
        {
            errors.Add(
                $"Contract descriptor '{logicalPath}' version '{descriptor.Version}' does not match bundle version '{bundle.Version}'.");
        }
        if (PackageContentSignatureValidator.IsLowercaseSha256(bundle.DescriptorSha256)
            && !string.Equals(descriptor.Sha256, bundle.DescriptorSha256, StringComparison.Ordinal))
        {
            errors.Add($"Contract descriptor '{logicalPath}' descriptorSha256 mismatch.");
        }
        if (PackageContentSignatureValidator.IsLowercaseSha256(bundle.AbiSha256)
            && !string.Equals(descriptor.AbiSha256, bundle.AbiSha256, StringComparison.Ordinal))
        {
            errors.Add($"Contract descriptor '{logicalPath}' abiSha256 mismatch.");
        }
        var actions = new List<string> { SunderPackageFormat.DiscoverContractAction };
        if (descriptor.Services.Any(static service => service.Methods.Any(static method => method.Kind == SunderRpcMethodKind.Unary)))
        {
            actions.Add(SunderPackageFormat.InvokeContractAction);
        }
        if (descriptor.Services.Any(static service => service.Methods.Any(static method => method.Kind == SunderRpcMethodKind.ServerStream)))
        {
            actions.Add(SunderPackageFormat.SubscribeContractAction);
        }
        return actions;
    }

    private static void ValidateUses(
        IReadOnlyList<SunderPackageContractUseManifest?>? uses,
        IReadOnlyDictionary<string, LocalContract> localContracts,
        IReadOnlySet<string> targetRoles,
        IReadOnlySet<string> packageApiUses,
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
            var validRole = SunderPackageFormat.IsHostRole(use.Role);
            if (!validRole)
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' declares unknown role '{use.Role}'.");
            }
            else if (!targetRoles.Contains(use.Role!))
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' role '{use.Role}' has no package target.");
            }

            var validId = PackageId.TryParse(use.ContractId, out _);
            if (!validId)
            {
                errors.Add($"Used contract id '{use.ContractId}' must use lowercase dot-separated ASCII identifiers.");
            }
            if (use.Role is not null
                && use.ContractId is not null
                && !seen.Add(RoleKey(use.Role, use.ContractId)))
            {
                errors.Add($"Contract use '{use.ContractId}' is declared more than once for role '{use.Role}'.");
            }

            var validVersion = SemanticVersion.TryParse(use.ContractVersion, out _);
            if (!validVersion)
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' contractVersion '{use.ContractVersion}' must be strict SemVer 2.0.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(use.AbiSha256))
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' must declare a lowercase abiSha256.");
            }
            if (use.Required is null)
            {
                errors.Add($"Contract use '{use.ContractId ?? "unknown"}' must declare required.");
            }
            ValidateActions(use, errors);

            if (validId && validVersion)
            {
                if (!localContracts.TryGetValue(
                        ContractKey(use.ContractId!, use.ContractVersion!),
                        out var localContract))
                {
                    errors.Add($"Contract use '{use.ContractId}' requires an exact matching local contract bundle.");
                }
                else if (!string.Equals(localContract.AbiSha256, use.AbiSha256, StringComparison.Ordinal))
                {
                    errors.Add($"Contract use '{use.ContractId}' abiSha256 does not match its local contract bundle.");
                }
                else if (localContract.ApplicableActions is { } applicableActions && use.Actions is { Count: <= 3 } declaredActions)
                {
                    var actions = declaredActions.Take(3).ToArray();
                    if (actions.Any(action => action is not null && !applicableActions.Contains(action, StringComparer.Ordinal)))
                    {
                        errors.Add($"Contract use '{use.ContractId}' actions are not applicable to its descriptor method kinds.");
                    }
                    if (packageApiUses.Contains(RoleKey(use.Role ?? string.Empty, ContractKey(use.ContractId!, use.ContractVersion!)))
                        && !actions.SequenceEqual(applicableActions, StringComparer.Ordinal))
                    {
                        errors.Add($"Package API contract use '{use.ContractId}' must declare exactly the descriptor-applicable actions.");
                    }
                }
            }
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
        var previousAction = -1;
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
            var actionIndex = action switch { "discover" => 0, "invoke" => 1, "subscribe" => 2, _ => -1 };
            if (actionIndex >= 0)
            {
                if (actionIndex < previousAction)
                {
                    errors.Add($"Contract use '{use.ContractId ?? "unknown"}' actions must follow discover, invoke, subscribe order.");
                }
                previousAction = actionIndex;
            }
        }
    }

    private static void ValidateProviders(
        IReadOnlyList<SunderPackageProviderManifest?>? providers,
        IReadOnlyDictionary<string, LocalContract> localContracts,
        IReadOnlySet<string> targetRoles,
        ICollection<string> errors)
    {
        if (providers is { Count: > SunderPackageFormat.MaxProviders })
        {
            errors.Add($"Package manifest declares {providers.Count} providers; the limit is {SunderPackageFormat.MaxProviders}.");
        }

        var seenProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in (providers ?? []).Take(SunderPackageFormat.MaxProviders))
        {
            if (provider is null)
            {
                errors.Add("Package provider entry is null.");
                continue;
            }
            var validProviderId = PackageId.TryParse(provider.ProviderId, out _);
            if (!validProviderId)
            {
                errors.Add($"Provider id '{provider.ProviderId}' must use lowercase dot-separated ASCII identifiers.");
            }
            if (provider.Role is not null
                && provider.ProviderId is not null
                && !seenProviders.Add(RoleKey(provider.Role, provider.ProviderId)))
            {
                errors.Add($"Provider id '{provider.ProviderId}' is declared more than once for role '{provider.Role}'.");
            }

            var validContractId = PackageId.TryParse(provider.ContractId, out _);
            if (!validContractId)
            {
                errors.Add($"Provider contract id '{provider.ContractId}' must use lowercase dot-separated ASCII identifiers.");
            }
            var validVersion = SemanticVersion.TryParse(provider.ContractVersion, out _);
            if (!validVersion)
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' contractVersion '{provider.ContractVersion}' must be strict SemVer 2.0.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(provider.DescriptorSha256))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' must declare a lowercase descriptorSha256.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(provider.AbiSha256))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' must declare a lowercase abiSha256.");
            }
            if (!SunderPackageFormat.IsHostRole(provider.Role))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' declares unknown role '{provider.Role}'.");
            }
            else if (provider.Role == SunderPackageFormat.AppHostRole)
            {
                errors.Add(
                    $"Provider '{provider.ProviderId ?? "unknown"}' declares role 'app'. App-role providers are not supported because App provider registration and serving are not available.");
            }
            else if (!targetRoles.Contains(provider.Role!))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' role '{provider.Role}' has no package target.");
            }
            if (!SunderPackageFormat.IsProviderAudience(provider.Audience))
            {
                errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' declares invalid audience '{provider.Audience}'.");
            }

            if (validContractId && validVersion)
            {
                if (!localContracts.TryGetValue(
                        ContractKey(provider.ContractId!, provider.ContractVersion!),
                        out var localContract))
                {
                    errors.Add(
                        $"Provider '{provider.ProviderId ?? "unknown"}' requires an exact matching local contract bundle.");
                }
                else
                {
                    if (!string.Equals(localContract.DescriptorSha256, provider.DescriptorSha256, StringComparison.Ordinal))
                    {
                        errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' descriptorSha256 does not match its local contract bundle.");
                    }
                    if (!string.Equals(localContract.AbiSha256, provider.AbiSha256, StringComparison.Ordinal))
                    {
                        errors.Add($"Provider '{provider.ProviderId ?? "unknown"}' abiSha256 does not match its local contract bundle.");
                    }
                    if (provider.Audience == SunderPackageFormat.PublicProviderAudience
                        && localContract.Exposure != SunderPackageFormat.PublicContractExposure)
                    {
                        errors.Add(
                            $"Provider '{provider.ProviderId ?? "unknown"}' with public audience requires public contract exposure.");
                    }
                }
            }
        }
    }

    private static string ContractKey(string contractId, string version) => contractId + "\0" + version;

    private static string RoleKey(string role, string id) => role + "\0" + id;

    private sealed record LocalContract(
        string DescriptorSha256,
        string AbiSha256,
        string Exposure,
        IReadOnlyList<string>? ApplicableActions);
}
