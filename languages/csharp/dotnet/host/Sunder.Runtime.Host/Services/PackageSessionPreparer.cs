using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionPreparer
{
    private readonly string _runtimeIdentifier;

    public PackageSessionPreparer(string? runtimeIdentifier = null)
    {
        _runtimeIdentifier = runtimeIdentifier ?? PackageTargetSelection.GetCurrentRuntimeIdentifier();
        if (!SunderPackageFormat.IsRuntimeIdentifier(_runtimeIdentifier))
        {
            throw new ArgumentException(
                $"Runtime package target selection requires one of the six supported exact RIDs; '{_runtimeIdentifier}' is unsupported.",
                nameof(runtimeIdentifier));
        }
    }

    public async Task<PreparedRuntimePackage?> PrepareDevPackageAsync(
        int index,
        string folder,
        string sessionFolder,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            errors.Add($"Dev package folder '{folder}' does not exist.");
            return null;
        }

        var validation = await ValidateSourceAsync(folder, "Dev package", errors, cancellationToken);
        if (validation is null)
        {
            return null;
        }

        var packageFolderName = $"{index:D2}-{SanitizeFolderName(validation.Manifest.Id)}";
        var canonicalShadow = Path.Combine(sessionFolder, packageFolderName, "source");
        try
        {
            await PackageProjectionMaterializer.MaterializeCanonicalPackageAsync(
                folder,
                canonicalShadow,
                validation.ContentIndex,
                cancellationToken);
            return await PrepareValidatedPackageAsync(
                folder,
                canonicalShadow,
                canonicalShadow,
                Path.Combine(sessionFolder, packageFolderName, "runtime"),
                PackageSourceKind.Dev,
                validation.Manifest,
                validation.ContentIndex,
                ComputeContentIdentity(canonicalShadow),
                errors,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            errors.Add($"Failed to materialize dev package '{validation.Manifest.Id}': {exception.Message}");
            return null;
        }
    }

    public async Task<PreparedRuntimePackage?> PrepareInstalledPackageAsync(
        int index,
        InstalledPackageRecord package,
        string physicalSourceRoot,
        string sessionFolder,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (DevProcessLaunchMetadata.RejectForInstalledSource(physicalSourceRoot, errors))
        {
            return null;
        }
        var validation = await ValidateInstalledSourceAsync(
            package,
            physicalSourceRoot,
            errors,
            cancellationToken);
        if (validation is null)
        {
            return null;
        }

        var packageFolderName = $"{index:D2}-{SanitizeFolderName(package.PackageId)}";
        return await PrepareValidatedPackageAsync(
            package.InstallPath,
            PathsEqual(package.InstallPath, physicalSourceRoot) ? null : physicalSourceRoot,
            physicalSourceRoot,
            Path.Combine(sessionFolder, packageFolderName, "runtime"),
            PackageSourceKind.Installed,
            validation.Manifest,
            validation.ContentIndex,
            package.ContentIdentity,
            errors,
            cancellationToken);
    }

    public async Task<RuntimePackageActivationState?> ReadInstalledActivationStateAsync(
        InstalledPackageRecord package,
        string physicalSourceRoot,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateInstalledSourceAsync(
            package,
            physicalSourceRoot,
            errors,
            cancellationToken);
        if (validation is null)
        {
            return null;
        }
        return CreateActivationState(validation.Manifest, selectedTargetKey: null, selectedTarget: null);
    }

    private async Task<PreparedRuntimePackage?> PrepareValidatedPackageAsync(
        string sourceFolder,
        string? snapshotFolder,
        string physicalSourceRoot,
        string projectionFolder,
        PackageSourceKind sourceKind,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        string contentIdentity,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var hostRoles = PackageTargetSelection.GetHostRoles(manifest);
        var dependencies = (manifest.DependsOn ?? [])
            .Select(static dependency => new PackageDependencyDescriptor(
                dependency.PackageId!,
                dependency.VersionRange!))
            .ToArray();
        SunderPackageTargetKey? selectedTargetKey = null;
        SunderPackageTargetManifest? selectedTarget = null;
        string? entryAssemblyPath = null;
        DevProcessLaunchMetadata? devProcessLaunch = null;
        var libraryFolder = Path.Combine(projectionFolder, "lib");
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> rpcContracts =
            new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);

        if ((hostRoles & PackageHostRoles.Runtime) != 0)
        {
            var runtimeTargetKey = new SunderPackageTargetKey(
                SunderPackageFormat.RuntimeHostRole,
                _runtimeIdentifier);
            if (!SunderPackageTargetResolver.TryResolveTarget(manifest, runtimeTargetKey, out var runtimeTarget))
            {
                errors.Add(
                    $"Package '{manifest.Id}' declares Runtime targets but does not declare the exact current Runtime target '{runtimeTargetKey}'. RID fallback is not supported.");
                return null;
            }
            var exactRuntimeTarget = runtimeTarget
                ?? throw new InvalidDataException($"Package target resolver returned no metadata for '{runtimeTargetKey}'.");
            if (exactRuntimeTarget.Kind is not (
                    SunderPackageFormat.DotnetTargetKind
                    or SunderPackageFormat.WorkerTargetKind
                    or SunderPackageFormat.ProcessTargetKind))
            {
                errors.Add(
                    $"Package '{manifest.Id}' exact Runtime target '{runtimeTargetKey}' has unsupported target kind '{exactRuntimeTarget.Kind}'. The Runtime Host supports '{SunderPackageFormat.DotnetTargetKind}', '{SunderPackageFormat.WorkerTargetKind}', and legacy '{SunderPackageFormat.ProcessTargetKind}' targets.");
                return null;
            }

            var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(manifest.Id, runtimeTargetKey, exactRuntimeTarget);
            if (compatibilityErrors.Count > 0)
            {
                foreach (var error in compatibilityErrors) errors.Add(error);
                return null;
            }

            try
            {
                await PackageProjectionMaterializer.MaterializeProjectionAsync(
                    physicalSourceRoot,
                    projectionFolder,
                    manifest,
                    contentIndex,
                    runtimeTargetKey,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(
                    $"Package '{manifest.Id}' target '{runtimeTargetKey}' projection failed: {exception.Message}");
                return null;
            }

            selectedTargetKey = runtimeTargetKey;
            selectedTarget = exactRuntimeTarget;
            var entryPoint = ArchiveRelativePath.Parse(
                exactRuntimeTarget.EntryPoint!,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth);
            entryAssemblyPath = entryPoint.ToPlatformPath(projectionFolder);
            if (!File.Exists(entryAssemblyPath))
            {
                errors.Add(
                    $"Package '{manifest.Id}' target '{runtimeTargetKey}' declared entry point '{exactRuntimeTarget.EntryPoint}' that was not materialized.");
                return null;
            }
            if (exactRuntimeTarget.Kind is SunderPackageFormat.WorkerTargetKind or SunderPackageFormat.ProcessTargetKind
                && !OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(entryAssemblyPath);
                mode &= ~(UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                mode |= UnixFileMode.UserExecute;
                File.SetUnixFileMode(entryAssemblyPath, mode);
            }
            if (!Directory.Exists(libraryFolder))
            {
                libraryFolder = Path.GetDirectoryName(entryAssemblyPath)!;
            }
            rpcContracts = await LoadRpcContractsAsync(projectionFolder, manifest, cancellationToken);
            if (sourceKind == PackageSourceKind.Dev
                && exactRuntimeTarget.Kind is SunderPackageFormat.WorkerTargetKind or SunderPackageFormat.ProcessTargetKind)
            {
                devProcessLaunch = DevProcessLaunchMetadata.LoadForDevSource(
                    sourceFolder,
                    manifest,
                    runtimeTargetKey,
                    exactRuntimeTarget,
                    contentIdentity,
                    errors);
                if (File.Exists(DevProcessLaunchMetadata.GetPath(sourceFolder)) && devProcessLaunch is null)
                {
                    return null;
                }
            }
        }
        else
        {
            Directory.CreateDirectory(projectionFolder);
        }

        var preparedSource = new RuntimePackageSource(
            manifest.Id!,
            sourceKind,
            sourceFolder,
            snapshotFolder,
            hostRoles,
            dependencies,
            contentIdentity,
            manifest,
            contentIndex,
            selectedTargetKey,
            selectedTarget);
        return new PreparedRuntimePackage(
            sourceFolder,
            preparedSource,
            projectionFolder,
            libraryFolder,
            manifest.Id!,
            manifest.Version!,
            hostRoles,
            CreateActivationState(manifest, selectedTargetKey, selectedTarget),
            entryAssemblyPath,
            selectedTargetKey,
            selectedTarget,
            dependencies,
            rpcContracts,
            GetManifestSha256(contentIndex))
        {
            DevProcessLaunch = devProcessLaunch,
            SessionId = new DirectoryInfo(projectionFolder).Parent?.Parent?.Name
                        ?? Guid.NewGuid().ToString("N"),
        };
    }

    private static async Task<ValidatedPackage?> ValidateSourceAsync(
        string sourceRoot,
        string label,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
            sourceRoot,
            cancellationToken);
        if (!validation.Success || validation.Manifest is null || validation.ContentIndex is null)
        {
            foreach (var error in validation.Errors)
            {
                errors.Add($"{label} '{sourceRoot}' is invalid: {error}");
            }
            return null;
        }
        return new ValidatedPackage(validation.Manifest, validation.ContentIndex);
    }

    private static async Task<ValidatedPackage?> ValidateInstalledSourceAsync(
        InstalledPackageRecord package,
        string physicalSourceRoot,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(physicalSourceRoot))
        {
            errors.Add($"Installed package folder '{physicalSourceRoot}' does not exist.");
            return null;
        }
        var validation = await ValidateSourceAsync(
            physicalSourceRoot,
            $"Installed package '{package.PackageId}'",
            errors,
            cancellationToken);
        if (validation is null)
        {
            return null;
        }
        if (!string.Equals(validation.Manifest.Id, package.PackageId, StringComparison.Ordinal)
            || !string.Equals(validation.Manifest.Version, package.Version, StringComparison.Ordinal)
            || !string.Equals(validation.Manifest.Name, package.Name, StringComparison.Ordinal)
            || !string.Equals(validation.Manifest.Summary, package.Summary, StringComparison.Ordinal)
            || !string.Equals(validation.Manifest.Icon, package.Icon, StringComparison.Ordinal))
        {
            errors.Add($"Installed package '{package.PackageId}' catalog identity does not match its strict manifest.");
            return null;
        }
        if (!string.Equals(ComputeContentIdentity(physicalSourceRoot), package.ContentIdentity, StringComparison.Ordinal)
            || !InventoryMatches(validation.ContentIndex, package.ContentInventory))
        {
            errors.Add($"Installed package '{package.PackageId}' content identity or inventory does not match its strict package content.");
            return null;
        }
        return validation;
    }

    private static RuntimePackageActivationState CreateActivationState(
        SunderPackageManifest manifest,
        SunderPackageTargetKey? selectedTargetKey,
        SunderPackageTargetManifest? selectedTarget)
        => new(
            manifest.Id!,
            manifest.Name!,
            manifest.Version!,
            PackageTargetSelection.GetHostRoles(manifest),
            manifest.Icon,
            selectedTargetKey,
            selectedTarget,
            manifest.UsesContracts);

    internal static bool InventoryMatches(
        SunderPackageContentIndex contentIndex,
        IReadOnlyList<InstalledPackageContentRecord> inventory)
    {
        var indexed = (contentIndex.Files ?? [])
            .Select(static entry => (entry!.Path!, entry.Sha256!, entry.Size))
            .OrderBy(static entry => entry.Item1, StringComparer.Ordinal)
            .ToArray();
        var installed = inventory
            .Select(static entry => (entry.Path, entry.Sha256, entry.Size))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        return indexed.SequenceEqual(installed);
    }

    internal static string ComputeContentIdentity(string sourceRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in new[] { SunderPackageFormat.ManifestPath, SunderPackageFormat.ContentIndexPath })
        {
            var path = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> LoadRpcContractsAsync(
        string projectionFolder,
        SunderPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        foreach (var bundle in manifest.ContractBundles ?? [])
        {
            if (bundle is null) continue;
            var descriptorPath = ArchiveRelativePath.Parse(
                bundle.DescriptorPath!,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth).ToPlatformPath(projectionFolder);
            var bytes = await File.ReadAllBytesAsync(descriptorPath, cancellationToken);
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            contracts.Add(ContractKey(descriptor.ContractId, descriptor.Version), descriptor);
        }
        return contracts;
    }

    private static string GetManifestSha256(SunderPackageContentIndex contentIndex)
        => contentIndex.Files?.Single(entry => string.Equals(
            entry?.Path,
            SunderPackageFormat.ManifestPath,
            StringComparison.Ordinal)).Sha256
           ?? throw new InvalidDataException("Package content index is missing its manifest hash.");

    internal static string ContractKey(string contractId, string version) => contractId + "\0" + version;

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "package";
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(folderName.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }

    private sealed record ValidatedPackage(
        SunderPackageManifest Manifest,
        SunderPackageContentIndex ContentIndex);
}
