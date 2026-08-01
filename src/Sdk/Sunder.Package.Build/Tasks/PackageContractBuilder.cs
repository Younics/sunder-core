using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Tasks;

internal sealed record PackageContractBuildResult(
    IReadOnlyList<SunderPackageContractBundleManifest?> Bundles,
    IReadOnlyList<SunderPackageContractUseManifest?> Uses,
    IReadOnlyList<SunderPackageProviderManifest?> Providers,
    ITaskItem[] Files);

internal static class PackageContractBuilder
{
    public static PackageContractBuildResult? Build(
        string manifestOutputPath,
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        IReadOnlyList<ITaskItem> bundleItems,
        IReadOnlyList<ITaskItem> useItems,
        IReadOnlyList<ITaskItem> providerItems,
        TaskLoggingHelper log)
    {
        if (bundleItems.Count > SunderPackageFormat.MaxContractBundles
            || useItems.Count > SunderPackageFormat.MaxContractUses
            || providerItems.Count > SunderPackageFormat.MaxProviders)
        {
            log.LogError("Sunder RPC contract authoring exceeds a package manifest item limit.");
            return null;
        }

        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(manifestOutputPath))!,
            "sunder-contracts");
        var bundles = new List<SunderPackageContractBundleManifest?>();
        var files = new List<ITaskItem>();
        var local = new Dictionary<string, SunderPackageContractBundleManifest>(StringComparer.Ordinal);
        var descriptorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in bundleItems)
        {
            var sourcePath = Path.GetFullPath(item.ItemSpec);
            var contractId = Required(item, "ContractId", "SunderContractBundle", log);
            var version = Required(item, "Version", "SunderContractBundle", log);
            var descriptorPathValue = Optional(item, "DescriptorPath")
                                      ?? $"contracts/{Path.GetFileName(sourcePath)}";
            if (!File.Exists(sourcePath))
            {
                log.LogError($"SunderContractBundle descriptor '{item.ItemSpec}' does not exist.");
                continue;
            }
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            {
                log.LogError($"SunderContractBundle descriptor '{item.ItemSpec}' must not be a symbolic link or reparse point.");
                continue;
            }
            if (!PackageId.TryParse(contractId, out _) || !SemanticVersion.TryParse(version, out _))
            {
                log.LogError($"SunderContractBundle '{item.ItemSpec}' must declare a lowercase contract id and strict SemVer Version.");
                continue;
            }
            ArchiveRelativePath descriptorPath;
            try
            {
                descriptorPath = ArchiveRelativePath.Parse(
                    descriptorPathValue,
                    SunderPackageFormat.MaxLogicalPathLength,
                    SunderPackageFormat.MaxLogicalPathDepth);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                log.LogError($"SunderContractBundle '{item.ItemSpec}' DescriptorPath is invalid: {exception.Message}");
                continue;
            }
            if (!descriptorPath.ToString().StartsWith("contracts/", StringComparison.Ordinal)
                || !descriptorPaths.Add(descriptorPath.ToString()))
            {
                log.LogError($"SunderContractBundle DescriptorPath '{descriptorPath}' must be unique and under 'contracts/'.");
                continue;
            }

            SunderRpcContractDescriptor descriptor;
            try
            {
                descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(sourcePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SunderRpcDescriptorException)
            {
                log.LogError($"SunderContractBundle descriptor '{item.ItemSpec}' is invalid: {exception.Message}");
                continue;
            }
            if (!string.Equals(descriptor.ContractId, contractId, StringComparison.Ordinal)
                || !string.Equals(descriptor.Version, version, StringComparison.Ordinal))
            {
                log.LogError($"SunderContractBundle '{item.ItemSpec}' metadata does not match descriptor identity '{descriptor.ContractId}' {descriptor.Version}.");
                continue;
            }

            Directory.CreateDirectory(outputDirectory);
            var canonicalPath = Path.Combine(outputDirectory, descriptor.Sha256 + ".json");
            File.WriteAllBytes(canonicalPath, descriptor.GetCanonicalUtf8());
            var bundle = new SunderPackageContractBundleManifest
            {
                ContractId = descriptor.ContractId,
                Version = descriptor.Version,
                DescriptorPath = descriptorPath.ToString(),
                Sha256 = descriptor.Sha256,
            };
            if (!local.TryAdd(Key(descriptor.ContractId, descriptor.Version), bundle))
            {
                log.LogError($"SunderContractBundle declares '{descriptor.ContractId}' {descriptor.Version} more than once.");
                continue;
            }
            bundles.Add(bundle);
            var file = new TaskItem(canonicalPath);
            file.SetMetadata("DescriptorPath", descriptorPath.ToString());
            files.Add(file);
        }

        var uses = BuildUses(useItems, local.Values, log);
        var providers = BuildProviders(providerItems, local, targets, log);
        return log.HasLoggedErrors || uses is null || providers is null
            ? null
            : new PackageContractBuildResult(bundles, uses, providers, files.ToArray());
    }

    private static IReadOnlyList<SunderPackageContractUseManifest?>? BuildUses(
        IReadOnlyList<ITaskItem> items,
        IEnumerable<SunderPackageContractBundleManifest> bundles,
        TaskLoggingHelper log)
    {
        var result = new List<SunderPackageContractUseManifest?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var contractId = item.ItemSpec.Trim();
            var rangeValue = Required(item, "VersionRange", "SunderUsesContract", log);
            var requiredValue = Required(item, "Required", "SunderUsesContract", log);
            var actionValue = Required(item, "Actions", "SunderUsesContract", log);
            if (!PackageId.TryParse(contractId, out _) || !seen.Add(contractId))
            {
                log.LogError($"SunderUsesContract Include '{item.ItemSpec}' must be a unique lowercase contract id.");
                continue;
            }
            if (!PackageVersionRange.TryParse(rangeValue, out var range))
            {
                log.LogError($"SunderUsesContract '{contractId}' VersionRange '{rangeValue}' is invalid.");
                continue;
            }
            if (!bool.TryParse(requiredValue, out var required))
            {
                log.LogError($"SunderUsesContract '{contractId}' Required must be exactly true or false.");
                continue;
            }
            var actions = actionValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (actions.Length is 0 or > 3
                || actions.Any(static action => !SunderPackageFormat.IsContractAction(action))
                || actions.Distinct(StringComparer.Ordinal).Count() != actions.Length)
            {
                log.LogError($"SunderUsesContract '{contractId}' Actions must contain unique discover, invoke, or subscribe values.");
                continue;
            }
            if (!bundles.Any(bundle => string.Equals(bundle.ContractId, contractId, StringComparison.Ordinal)
                                       && SemanticVersion.TryParse(bundle.Version, out var version)
                                       && range.IsSatisfiedBy(version)))
            {
                log.LogError($"SunderUsesContract '{contractId}' requires a matching local SunderContractBundle.");
                continue;
            }
            result.Add(new SunderPackageContractUseManifest
            {
                ContractId = contractId,
                VersionRange = rangeValue,
                Required = required,
                Actions = actions,
            });
        }
        return log.HasLoggedErrors ? null : result;
    }

    private static IReadOnlyList<SunderPackageProviderManifest?>? BuildProviders(
        IReadOnlyList<ITaskItem> items,
        IReadOnlyDictionary<string, SunderPackageContractBundleManifest> bundles,
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        TaskLoggingHelper log)
    {
        var result = new List<SunderPackageProviderManifest?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetRoles = targets.Where(static target => target?.Role is not null)
            .Select(static target => target!.Role!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var providerId = item.ItemSpec.Trim();
            var contractId = Required(item, "ContractId", "SunderRpcProvider", log);
            var contractVersion = Required(item, "ContractVersion", "SunderRpcProvider", log);
            var role = Required(item, "Role", "SunderRpcProvider", log);
            if (!PackageId.TryParse(providerId, out _)
                || !PackageId.TryParse(contractId, out _)
                || !SemanticVersion.TryParse(contractVersion, out _)
                || !SunderPackageFormat.IsHostRole(role)
                || !targetRoles.Contains(role)
                || !seen.Add(providerId + "\0" + role))
            {
                log.LogError($"SunderRpcProvider '{item.ItemSpec}' has invalid or duplicate identity, contract, version, or target Role metadata.");
                continue;
            }
            if (!bundles.TryGetValue(Key(contractId, contractVersion), out var bundle))
            {
                log.LogError($"SunderRpcProvider '{providerId}' requires a matching local SunderContractBundle.");
                continue;
            }
            result.Add(new SunderPackageProviderManifest
            {
                ProviderId = providerId,
                ContractId = contractId,
                ContractVersion = contractVersion,
                ContractSha256 = bundle.Sha256,
                Role = role,
            });
        }
        return log.HasLoggedErrors ? null : result;
    }

    private static string Required(ITaskItem item, string name, string itemType, TaskLoggingHelper log)
    {
        var value = Optional(item, name);
        if (value is null) log.LogError($"{itemType} '{item.ItemSpec}' requires '{name}' metadata.");
        return value ?? string.Empty;
    }

    private static string? Optional(ITaskItem item, string name)
    {
        var value = item.GetMetadata(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Key(string contractId, string version) => contractId + "\0" + version;
}
