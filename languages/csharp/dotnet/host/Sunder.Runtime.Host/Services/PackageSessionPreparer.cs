using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionPreparer
{
    private readonly string _runtimeIdentifier;
    private readonly ILogger _logger;

    public PackageSessionPreparer(string? runtimeIdentifier = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
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
        var preparationStarted = Stopwatch.GetTimestamp();
        if (!Directory.Exists(folder))
        {
            errors.Add($"Dev package folder '{folder}' does not exist.");
            _logger.LogWarning(
                "Dev package validation failed in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            return null;
        }

        var validationStarted = Stopwatch.GetTimestamp();
        var validation = await ValidateSourceAsync(folder, "Dev package", errors, cancellationToken);
        if (validation is null)
        {
            _logger.LogWarning(
                "Dev package validation failed in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            return null;
        }
        _logger.LogInformation(
            "Validated {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
            PackageSourceKind.Dev,
            validation.Manifest.Id,
            Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
        using var preparationScope = _logger.BeginScope(
            "Preparing {PackageSourceKind} package {PackageId}",
            PackageSourceKind.Dev,
            validation.Manifest.Id);

        var packageFolderName = $"{index:D2}-{SanitizeFolderName(validation.Manifest.Id)}";
        var canonicalShadow = Path.Combine(sessionFolder, packageFolderName, "source");
        var canonicalShadowStarted = Stopwatch.GetTimestamp();
        var canonicalShadowMaterialized = false;
        try
        {
            await PackageProjectionMaterializer.MaterializeCanonicalPackageAsync(
                folder,
                canonicalShadow,
                validation.ContentIndex,
                cancellationToken);
            canonicalShadowMaterialized = true;
            _logger.LogInformation(
                "Materialized canonical shadow for package {PackageId} in {ElapsedMilliseconds} ms",
                validation.Manifest.Id,
                Stopwatch.GetElapsedTime(canonicalShadowStarted).TotalMilliseconds);
            var prepared = await PrepareValidatedPackageAsync(
                folder,
                canonicalShadow,
                canonicalShadow,
                Path.Combine(sessionFolder, packageFolderName, "runtime"),
                PackageSourceKind.Dev,
                validation.Manifest,
                validation.ContentIndex,
                ComputeContentIdentity(canonicalShadow),
                approvedSensitiveCapabilities: [],
                errors,
                cancellationToken);
            if (prepared is null)
            {
                _logger.LogWarning(
                    "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Dev,
                    validation.Manifest.Id,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation(
                    "Prepared {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                    PackageSourceKind.Dev,
                    validation.Manifest.Id,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            return prepared;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Canceled preparation for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Dev,
                validation.Manifest.Id,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            if (!canonicalShadowMaterialized)
            {
                _logger.LogWarning(
                    "Canonical shadow materialization failed for package {PackageId} after {ElapsedMilliseconds} ms",
                    validation.Manifest.Id,
                    Stopwatch.GetElapsedTime(canonicalShadowStarted).TotalMilliseconds);
            }
            _logger.LogWarning(
                "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Dev,
                validation.Manifest.Id,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
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
        var preparationStarted = Stopwatch.GetTimestamp();
        using var preparationScope = _logger.BeginScope(
            "Preparing {PackageSourceKind} package {PackageId}",
            PackageSourceKind.Installed,
            package.PackageId);
        if (DevProcessLaunchMetadata.RejectForInstalledSource(physicalSourceRoot, errors))
        {
            _logger.LogWarning(
                "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Installed,
                package.PackageId,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            return null;
        }
        try
        {
            var validation = await ValidateInstalledSourceAsync(
                package,
                physicalSourceRoot,
                errors,
                cancellationToken);
            if (validation is null)
            {
                _logger.LogWarning(
                    "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
                return null;
            }

            var packageFolderName = $"{index:D2}-{SanitizeFolderName(package.PackageId)}";
            var prepared = await PrepareValidatedPackageAsync(
                package.InstallPath,
                PathsEqual(package.InstallPath, physicalSourceRoot) ? null : physicalSourceRoot,
                physicalSourceRoot,
                Path.Combine(sessionFolder, packageFolderName, "runtime"),
                PackageSourceKind.Installed,
                validation.Manifest,
                validation.ContentIndex,
                package.ContentIdentity,
                package.CapabilityGrants.Select(static grant => grant.Capability).ToArray(),
                errors,
                cancellationToken);
            if (prepared is null)
            {
                _logger.LogWarning(
                    "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation(
                    "Prepared {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            }
            return prepared;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Canceled preparation for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Installed,
                package.PackageId,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            throw;
        }
        catch
        {
            _logger.LogWarning(
                "Preparation failed for {PackageSourceKind} package {PackageId} after {ElapsedMilliseconds} ms",
                PackageSourceKind.Installed,
                package.PackageId,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            throw;
        }
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
        IReadOnlyList<string> approvedSensitiveCapabilities,
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
            if (!string.Equals(
                    exactRuntimeTarget.Kind,
                    SunderPackageFormat.WorkerTargetKind,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    $"Package '{manifest.Id}' exact Runtime target '{runtimeTargetKey}' has unsupported target kind '{exactRuntimeTarget.Kind}'. The Runtime Host requires '{SunderPackageFormat.WorkerTargetKind}'.");
                return null;
            }

            var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(manifest.Id, runtimeTargetKey, exactRuntimeTarget);
            if (compatibilityErrors.Count > 0)
            {
                foreach (var error in compatibilityErrors) errors.Add(error);
                return null;
            }

            var projectionStarted = Stopwatch.GetTimestamp();
            try
            {
                await PackageProjectionMaterializer.MaterializeProjectionAsync(
                    physicalSourceRoot,
                    projectionFolder,
                    manifest,
                    contentIndex,
                    runtimeTargetKey,
                    cancellationToken);
                _logger.LogInformation(
                    "Materialized Runtime target projection for package {PackageId} in {ElapsedMilliseconds} ms",
                    manifest.Id,
                    Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Runtime target projection failed for package {PackageId} after {ElapsedMilliseconds} ms",
                    manifest.Id,
                    Stopwatch.GetElapsedTime(projectionStarted).TotalMilliseconds);
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
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(entryAssemblyPath);
                mode &= ~(UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                mode |= UnixFileMode.UserExecute;
                File.SetUnixFileMode(entryAssemblyPath, mode);
            }
            var contractLoadStarted = Stopwatch.GetTimestamp();
            try
            {
                rpcContracts = await LoadRpcContractsAsync(projectionFolder, manifest, cancellationToken);
                _logger.LogInformation(
                    "Loaded {ContractCount} RPC contract(s) for package {PackageId} in {ElapsedMilliseconds} ms",
                    rpcContracts.Count,
                    manifest.Id,
                    Stopwatch.GetElapsedTime(contractLoadStarted).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _logger.LogWarning(
                    "RPC contract loading failed for package {PackageId} after {ElapsedMilliseconds} ms",
                    manifest.Id,
                    Stopwatch.GetElapsedTime(contractLoadStarted).TotalMilliseconds);
                throw;
            }
            if (sourceKind == PackageSourceKind.Dev)
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
            ApprovedSensitiveCapabilities = approvedSensitiveCapabilities,
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

    private async Task<ValidatedPackage?> ValidateInstalledSourceAsync(
        InstalledPackageRecord package,
        string physicalSourceRoot,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var validationStarted = Stopwatch.GetTimestamp();
        var outcome = "Failed";
        try
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
            outcome = "Succeeded";
            return validation;
        }
        catch (OperationCanceledException)
        {
            outcome = "Canceled";
            throw;
        }
        finally
        {
            if (string.Equals(outcome, "Succeeded", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Validated {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Validation for {PackageSourceKind} package {PackageId} completed with {ValidationOutcome} after {ElapsedMilliseconds} ms",
                    PackageSourceKind.Installed,
                    package.PackageId,
                    outcome,
                    Stopwatch.GetElapsedTime(validationStarted).TotalMilliseconds);
            }
        }
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

    internal static async Task<IReadOnlyDictionary<string, SunderRpcContractDescriptor>> LoadRpcContractsAsync(
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
            RuntimeRpcPackageMetadata.ValidateContractDescriptor(
                bundle,
                descriptor,
                bundle.DescriptorPath!);
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
