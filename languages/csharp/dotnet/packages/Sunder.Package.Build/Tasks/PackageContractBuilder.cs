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
    IReadOnlySet<string> RpcRoles,
    ITaskItem[] Files);

internal static class PackageContractBuilder
{
    public static PackageContractBuildResult? Build(
        string manifestOutputPath,
        string projectDirectory,
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        IReadOnlyList<ITaskItem> bundleItems,
        IReadOnlyList<ITaskItem> bindingSnapshotItems,
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
        var normalizedProjectDirectory = Path.GetFullPath(projectDirectory);
        var bindingSnapshots = BuildBindingSnapshots(
            normalizedProjectDirectory,
            bindingSnapshotItems,
            log);
        if (bindingSnapshots is null) return null;
        var bundles = new List<SunderPackageContractBundleManifest?>();
        var files = new List<ITaskItem>();
        var local = new Dictionary<string, SunderPackageContractBundleManifest>(StringComparer.Ordinal);
        var descriptorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in bundleItems)
        {
            string sourcePath;
            try
            {
                sourcePath = Path.GetFullPath(item.ItemSpec, normalizedProjectDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                log.LogError($"SunderContractBundle descriptor path '{item.ItemSpec}' is invalid: {exception.Message}");
                continue;
            }
            var contractId = Required(item, "ContractId", "SunderContractBundle", log);
            var version = Required(item, "Version", "SunderContractBundle", log);
            var exposure = Required(item, "Exposure", "SunderContractBundle", log);
            var descriptorPathValue = Optional(item, "DescriptorPath")
                                      ?? $"contracts/{Path.GetFileName(sourcePath)}";
            RejectAuthoredMetadata(
                item,
                "SunderContractBundle",
                log,
                "Sha256",
                "DescriptorSha256",
                "AbiSha256",
                "ContractSha256");
            if (!PackageId.TryParse(contractId, out _) || !SemanticVersion.TryParse(version, out _))
            {
                log.LogError($"SunderContractBundle '{item.ItemSpec}' must declare a lowercase contract id and strict SemVer Version.");
                continue;
            }
            if (!SunderPackageFormat.IsContractExposure(exposure))
            {
                log.LogError($"SunderContractBundle '{item.ItemSpec}' Exposure must be exactly 'package' or 'public'.");
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

            SunderRpcDescriptorFileSnapshot descriptorSnapshot;
            try
            {
                descriptorSnapshot = bindingSnapshots.TryGetValue(sourcePath, out var bindingSnapshot)
                    ? bindingSnapshot
                    : SunderRpcDescriptorFileReader.Read(
                        sourcePath,
                        "SunderContractBundle descriptor");
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or SunderRpcDescriptorException)
            {
                log.LogError($"SunderContractBundle descriptor '{item.ItemSpec}' is invalid: {exception.Message}");
                continue;
            }
            var descriptor = descriptorSnapshot.Descriptor;
            if (descriptor.DescriptorVersion != SunderRpcProtocol.DescriptorVersionV2
                || !IsLowercaseSha256(descriptor.AbiSha256)
                || descriptor.GetCanonicalAbiProjectionUtf8() is not { Length: > 0 })
            {
                log.LogError(
                    $"SunderContractBundle descriptor '{item.ItemSpec}' must be descriptorVersion {SunderRpcProtocol.DescriptorVersionV2} with a valid canonical V2 ABI snapshot; package manifest V{SunderPackageFormat.CurrentManifestVersion} does not accept V1 RPC descriptors.");
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
            File.WriteAllBytes(canonicalPath, descriptorSnapshot.CanonicalUtf8);
            var bundle = new SunderPackageContractBundleManifest
            {
                ContractId = descriptor.ContractId,
                Version = descriptor.Version,
                DescriptorPath = descriptorPath.ToString(),
                DescriptorSha256 = descriptor.Sha256,
                AbiSha256 = descriptor.AbiSha256,
                Exposure = exposure,
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

        var uses = BuildUses(useItems, local, targets, log);
        var providers = BuildProviders(providerItems, local, targets, log);
        return log.HasLoggedErrors || uses is null || providers is null
            ? null
            : new PackageContractBuildResult(
                bundles,
                uses,
                providers,
                uses.Where(static use => use?.Role is not null).Select(static use => use!.Role!)
                    .Concat(providers.Where(static provider => provider?.Role is not null).Select(static provider => provider!.Role!))
                    .ToHashSet(StringComparer.Ordinal),
                files.ToArray());
    }

    private static IReadOnlyDictionary<string, SunderRpcDescriptorFileSnapshot>? BuildBindingSnapshots(
        string projectDirectory,
        IReadOnlyList<ITaskItem> items,
        TaskLoggingHelper log)
    {
        var snapshots = new Dictionary<string, SunderRpcDescriptorFileSnapshot>(PathComparer);
        foreach (var item in items)
        {
            var sourceValue = Optional(item, "SourceDescriptorPath");
            var generatedDirectoryValue = Optional(item, "GeneratedDirectory");
            var markerValue = Optional(item, "OwnershipMarkerPath");
            if (sourceValue is null || generatedDirectoryValue is null || markerValue is null)
            {
                log.LogError(
                    $"Sunder RPC binding snapshot '{item.ItemSpec}' is missing source or generated-output ownership metadata.");
                continue;
            }

            try
            {
                var sourcePath = Path.GetFullPath(sourceValue, projectDirectory);
                var snapshotPath = Path.GetFullPath(item.ItemSpec, projectDirectory);
                var generatedDirectory = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(generatedDirectoryValue, projectDirectory));
                var markerPath = Path.GetFullPath(markerValue, projectDirectory);
                var expectedSnapshotDirectory = Path.Combine(
                    generatedDirectory,
                    GenerateSunderRpcBindingsTask.SnapshotDirectoryName);
                var expectedMarkerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
                if (!PathsEqual(Path.GetDirectoryName(snapshotPath) ?? string.Empty, expectedSnapshotDirectory)
                    || !PathsEqual(markerPath, expectedMarkerPath)
                    || !GenerateSunderRpcBindingsTask.HasValidOwnershipMarker(markerPath))
                {
                    throw new InvalidDataException(
                        "The snapshot is not under a safely marked Sunder RPC generated directory.");
                }

                var snapshot = SunderRpcDescriptorFileReader.Read(
                    snapshotPath,
                    "Sunder RPC binding snapshot",
                    requireCanonical: true);
                var descriptor = snapshot.Descriptor;
                if (descriptor.DescriptorVersion != SunderRpcProtocol.DescriptorVersionV2
                    || !IsLowercaseSha256(descriptor.AbiSha256)
                    || descriptor.GetCanonicalAbiProjectionUtf8() is not { Length: > 0 })
                {
                    throw new InvalidDataException(
                        $"The canonical binding snapshot must use descriptorVersion {SunderRpcProtocol.DescriptorVersionV2} and contain a valid V2 ABI hash.");
                }
                if (!string.Equals(
                        Path.GetFileName(snapshotPath),
                        descriptor.Sha256 + ".rpc.json",
                        StringComparison.Ordinal)
                    || !MetadataEquals(item, "DescriptorVersion", descriptor.DescriptorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    || !MetadataEquals(item, "ContractId", descriptor.ContractId)
                    || !MetadataEquals(item, "ContractVersion", descriptor.Version)
                    || !MetadataEquals(item, "DescriptorSha256", descriptor.Sha256)
                    || !MetadataEquals(item, "AbiSha256", descriptor.AbiSha256 ?? string.Empty))
                {
                    throw new InvalidDataException(
                        "The canonical snapshot does not match the immutable binding snapshot metadata.");
                }
                if (!snapshots.TryAdd(
                        sourcePath,
                        new SunderRpcDescriptorFileSnapshot(sourcePath, descriptor, snapshot.CanonicalUtf8)))
                {
                    throw new InvalidDataException(
                        $"More than one binding snapshot identifies source descriptor '{sourcePath}'.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or SunderRpcDescriptorException)
            {
                log.LogError($"Sunder RPC binding snapshot '{item.ItemSpec}' is invalid: {exception.Message}");
            }
        }
        return log.HasLoggedErrors ? null : snapshots;
    }

    private static IReadOnlyList<SunderPackageContractUseManifest?>? BuildUses(
        IReadOnlyList<ITaskItem> items,
        IReadOnlyDictionary<string, SunderPackageContractBundleManifest> bundles,
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        TaskLoggingHelper log)
    {
        var result = new List<SunderPackageContractUseManifest?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetRoles = TargetRoles(targets);
        foreach (var item in items)
        {
            var contractId = item.ItemSpec.Trim();
            var role = Required(item, "Role", "SunderUsesContract", log);
            var contractVersion = Required(item, "ContractVersion", "SunderUsesContract", log);
            var requiredValue = Required(item, "Required", "SunderUsesContract", log);
            var actionValue = Required(item, "Actions", "SunderUsesContract", log);
            RejectAuthoredMetadata(
                item,
                "SunderUsesContract",
                log,
                "VersionRange",
                "Sha256",
                "DescriptorSha256",
                "AbiSha256",
                "ContractSha256");
            if (!PackageId.TryParse(contractId, out _)
                || !SemanticVersion.TryParse(contractVersion, out _))
            {
                log.LogError(
                    $"SunderUsesContract Include '{item.ItemSpec}' must be a lowercase contract id with a strict SemVer ContractVersion.");
                continue;
            }
            if (!SunderPackageFormat.IsHostRole(role) || !targetRoles.Contains(role))
            {
                log.LogError($"SunderUsesContract '{contractId}' Role '{role}' must identify an existing app or runtime target.");
                continue;
            }
            if (!seen.Add(RoleKey(role, contractId)))
            {
                log.LogError($"SunderUsesContract '{contractId}' is declared more than once for Role '{role}'.");
                continue;
            }
            var required = requiredValue switch
            {
                "true" => true,
                "false" => false,
                _ => (bool?)null,
            };
            if (required is null)
            {
                log.LogError($"SunderUsesContract '{contractId}' Required must be exactly 'true' or 'false'.");
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
            if (!bundles.TryGetValue(Key(contractId, contractVersion), out var bundle))
            {
                log.LogError(
                    $"SunderUsesContract '{contractId}' {contractVersion} requires an exact matching local SunderContractBundle.");
                continue;
            }
            result.Add(new SunderPackageContractUseManifest
            {
                Role = role,
                ContractId = contractId,
                ContractVersion = contractVersion,
                AbiSha256 = bundle.AbiSha256,
                Required = required.Value,
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
        var targetRoles = TargetRoles(targets);
        foreach (var item in items)
        {
            var providerId = item.ItemSpec.Trim();
            var contractId = Required(item, "ContractId", "SunderRpcProvider", log);
            var contractVersion = Required(item, "ContractVersion", "SunderRpcProvider", log);
            var role = Required(item, "Role", "SunderRpcProvider", log);
            var audience = Required(item, "Audience", "SunderRpcProvider", log);
            RejectAuthoredMetadata(
                item,
                "SunderRpcProvider",
                log,
                "Sha256",
                "DescriptorSha256",
                "AbiSha256",
                "ContractSha256");
            if (role == SunderPackageFormat.AppHostRole)
            {
                log.LogError(
                    $"SunderRpcProvider '{providerId}' declares Role 'app'. App-role providers are not supported because App provider registration and serving are not available.");
                continue;
            }
            if (!PackageId.TryParse(providerId, out _)
                || !PackageId.TryParse(contractId, out _)
                || !SemanticVersion.TryParse(contractVersion, out _)
                || !SunderPackageFormat.IsHostRole(role)
                || !targetRoles.Contains(role)
                || !SunderPackageFormat.IsProviderAudience(audience))
            {
                log.LogError(
                    $"SunderRpcProvider '{item.ItemSpec}' must declare valid identity, contract, version, target Role, and owner or public Audience metadata.");
                continue;
            }
            if (!seen.Add(RoleKey(role, providerId)))
            {
                log.LogError($"SunderRpcProvider '{providerId}' is declared more than once for Role '{role}'.");
                continue;
            }
            if (!bundles.TryGetValue(Key(contractId, contractVersion), out var bundle))
            {
                log.LogError(
                    $"SunderRpcProvider '{providerId}' requires an exact matching local SunderContractBundle.");
                continue;
            }
            if (audience == SunderPackageFormat.PublicProviderAudience
                && bundle.Exposure != SunderPackageFormat.PublicContractExposure)
            {
                log.LogError(
                    $"SunderRpcProvider '{providerId}' with public Audience requires a public SunderContractBundle Exposure; package exposure permits only owner audience.");
                continue;
            }
            result.Add(new SunderPackageProviderManifest
            {
                ProviderId = providerId,
                ContractId = contractId,
                ContractVersion = contractVersion,
                DescriptorSha256 = bundle.DescriptorSha256,
                AbiSha256 = bundle.AbiSha256,
                Role = role,
                Audience = audience,
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

    private static string RoleKey(string role, string id) => role + "\0" + id;

    private static HashSet<string> TargetRoles(IEnumerable<SunderPackageTargetManifest?> targets)
        => targets.Where(static target => target?.Role is not null)
            .Select(static target => target!.Role!)
            .ToHashSet(StringComparer.Ordinal);

    private static void RejectAuthoredMetadata(
        ITaskItem item,
        string itemType,
        TaskLoggingHelper log,
        params string[] names)
    {
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(item.GetMetadata(name))))
        {
            log.LogError(
                $"{itemType} '{item.ItemSpec}' must not author legacy or derived '{name}' metadata; Manifest V2 derives hashes from the canonical descriptor snapshot and uses exact ContractVersion binding.");
        }
    }

    private static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool MetadataEquals(ITaskItem item, string name, string expected)
        => string.Equals(item.GetMetadata(name), expected, StringComparison.Ordinal);

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
