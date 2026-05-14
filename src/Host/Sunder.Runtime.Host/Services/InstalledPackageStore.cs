using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.PackageManagement;
using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed class InstalledPackageStore(RuntimePackagePaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<InstalledPackageRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packages = (await ReadStateAsync(cancellationToken)).ToList();
            var recoveredPackages = RecoverCopiedPackages(packages);
            if (recoveredPackages.Count > 0)
            {
                packages.AddRange(recoveredPackages);
                await WriteStateAsync(packages, cancellationToken);
            }

            return packages;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InstalledPackageRecord?> GetAsync(string packageId, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken)).FirstOrDefault(package =>
            string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public async Task<PackageOperationResult> InstallAsync(InstalledPackageRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packages = (await ReadStateAsync(cancellationToken)).ToList();
            if (packages.Any(package => string.Equals(package.PackageId, record.PackageId, StringComparison.OrdinalIgnoreCase)))
            {
                return PackageOperationResults.Failure($"Package '{record.PackageId}' is already installed.");
            }

            var dependencyError = ValidateDependencies(record, packages, requireEnabled: true);
            if (dependencyError is not null)
            {
                return PackageOperationResults.Failure(dependencyError);
            }

            packages.Add(record);
            await WriteStateAsync(packages, cancellationToken);
            return PackageOperationResults.Success(
                $"Installed package '{record.Name}' {record.Version}.",
                impactedPackageIds: BuildDependencyImpactSet(record, packages));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageOperationResult> SetEnabledAsync(string packageId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packages = (await ReadStateAsync(cancellationToken)).ToList();
            var index = packages.FindIndex(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' is not installed.");
            }

            var package = packages[index];
            if (package.IsEnabled == isEnabled)
            {
                return PackageOperationResults.Success($"Package '{package.Name}' is already {(isEnabled ? "enabled" : "disabled")}.", requiresAppRestart: false);
            }

            if (isEnabled)
            {
                var dependencyError = ValidateDependencies(package, packages, requireEnabled: true);
                if (dependencyError is not null)
                {
                    return PackageOperationResults.Failure(dependencyError);
                }
            }
            else
            {
                var dependent = packages.FirstOrDefault(candidate => candidate.IsEnabled
                    && candidate.DependsOn.Any(dependency => string.Equals(dependency.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase)));
                if (dependent is not null)
                {
                    return PackageOperationResults.Failure($"Package '{package.PackageId}' cannot be disabled because enabled package '{dependent.PackageId}' depends on it.");
                }
            }

            packages[index] = package with { IsEnabled = isEnabled };
            await WriteStateAsync(packages, cancellationToken);
            return PackageOperationResults.Success(
                $"{(isEnabled ? "Enabled" : "Disabled")} package '{package.Name}'.",
                impactedPackageIds: BuildDependencyImpactSet(package, packages));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageOperationResult> UpgradeAsync(
        string packageId,
        InstalledPackageRecord record,
        bool allowDowngrade,
        bool reinstall,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(packageId, record.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                return PackageOperationResults.Failure($"Package archive '{record.PackageId}' does not match selected package '{packageId}'.");
            }

            var packages = (await ReadStateAsync(cancellationToken)).ToList();
            var index = packages.FindIndex(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' is not installed.");
            }

            var currentPackage = packages[index];
            if (!PackageVersionRange.TryCompare(record.Version, currentPackage.Version, out var versionComparison))
            {
                return PackageOperationResults.Failure($"Package version '{record.Version}' or installed version '{currentPackage.Version}' is invalid.");
            }

            if (versionComparison < 0 && !allowDowngrade)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' cannot be downgraded from {currentPackage.Version} to {record.Version} without allowing downgrades.");
            }

            if (versionComparison == 0 && !reinstall)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' version {record.Version} is already installed.");
            }

            var replacementPackage = record with { IsEnabled = currentPackage.IsEnabled };
            packages[index] = replacementPackage;

            var dependencyError = ValidateDependencies(replacementPackage, packages, requireEnabled: replacementPackage.IsEnabled);
            if (dependencyError is not null)
            {
                return PackageOperationResults.Failure(dependencyError);
            }

            var dependentError = ValidateDependents(replacementPackage, packages);
            if (dependentError is not null)
            {
                return PackageOperationResults.Failure(dependentError);
            }

            await WriteStateAsync(packages, cancellationToken);
            return PackageOperationResults.Success(
                $"Updated package '{replacementPackage.Name}' from {currentPackage.Version} to {replacementPackage.Version}.",
                impactedPackageIds: BuildUpgradeImpactSet(replacementPackage, packages));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageOperationResult> UninstallAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packages = (await ReadStateAsync(cancellationToken)).ToList();
            var package = packages.FirstOrDefault(candidate => string.Equals(candidate.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (package is null)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' is not installed.");
            }

            var packagesToUninstall = BuildUninstallSet(package, packages);

            foreach (var packageToUninstall in packagesToUninstall)
            {
                try
                {
                    if (Directory.Exists(packageToUninstall.InstallPath))
                    {
                        Directory.Delete(packageToUninstall.InstallPath, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    return PackageOperationResults.Failure($"Failed to delete installed package files for '{packageToUninstall.PackageId}': {ex.Message}");
                }
            }

            var removedPackageIds = packagesToUninstall.Select(candidate => candidate.PackageId).ToArray();
            var removedPackageIdSet = removedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            packages.RemoveAll(candidate => removedPackageIdSet.Contains(candidate.PackageId));
            await WriteStateAsync(packages, cancellationToken);
            var message = packagesToUninstall.Count == 1
                ? $"Uninstalled package '{package.Name}'."
                : $"Uninstalled package '{package.Name}' and {packagesToUninstall.Count - 1} dependent package(s).";
            return PackageOperationResults.Success(
                message,
                impactedPackageIds: removedPackageIds);
        }
        finally
        {
            _gate.Release();
        }
    }

    public InstalledPackageDescriptor ToDescriptor(InstalledPackageRecord package)
    {
        var icon = string.IsNullOrWhiteSpace(package.Icon)
            ? null
            : new PackageIconDescriptor(null, package.Icon);
        var dependencies = package.DependsOn
            .Select(dependency => new PackageDependencyDescriptor(dependency.PackageId, dependency.VersionRange))
            .ToArray();
        return new InstalledPackageDescriptor(
            package.PackageId,
            package.Name,
            package.Version,
            package.Summary,
            icon,
            package.IsEnabled,
            dependencies,
            package.InstalledAtUtc,
            package.IsEnabled ? null : "Disabled");
    }

    public async Task<string?> TryResolvePackageAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        var package = await GetAsync(packageId, cancellationToken);
        return package is null
            ? null
            : PackageAssetPathResolver.TryResolveInstalledAssetPath(package.InstallPath, assetPath);
    }

    private static string? ValidateDependencies(
        InstalledPackageRecord package,
        IReadOnlyList<InstalledPackageRecord> packages,
        bool requireEnabled)
    {
        foreach (var dependency in package.DependsOn)
        {
            var installedDependency = packages.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageId, dependency.PackageId, StringComparison.OrdinalIgnoreCase));
            if (installedDependency is null)
            {
                return $"Package '{package.PackageId}' depends on missing package '{dependency.PackageId}'.";
            }

            if (requireEnabled && !installedDependency.IsEnabled)
            {
                return $"Package '{package.PackageId}' depends on disabled package '{dependency.PackageId}'.";
            }

            if (!PackageVersionRange.IsSatisfiedBy(installedDependency.Version, dependency.VersionRange))
            {
                return $"Package '{package.PackageId}' requires '{dependency.PackageId}' version '{dependency.VersionRange}', but '{installedDependency.Version}' is installed.";
            }
        }

        return null;
    }

    private static string? ValidateDependents(
        InstalledPackageRecord package,
        IReadOnlyList<InstalledPackageRecord> packages)
    {
        foreach (var dependent in packages)
        {
            if (string.Equals(dependent.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var dependency in dependent.DependsOn)
            {
                if (!string.Equals(dependency.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PackageVersionRange.IsSatisfiedBy(package.Version, dependency.VersionRange))
                {
                    return $"Package '{dependent.PackageId}' requires '{package.PackageId}' version '{dependency.VersionRange}', but '{package.Version}' would be installed.";
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildDependencyImpactSet(
        InstalledPackageRecord package,
        IReadOnlyList<InstalledPackageRecord> packages)
    {
        var packagesById = packages.ToDictionary(candidate => candidate.PackageId, StringComparer.OrdinalIgnoreCase);
        var impactedPackageIds = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPackageAndDependencies(InstalledPackageRecord currentPackage)
        {
            if (!visited.Add(currentPackage.PackageId))
            {
                return;
            }

            impactedPackageIds.Add(currentPackage.PackageId);
            foreach (var dependency in currentPackage.DependsOn)
            {
                if (packagesById.TryGetValue(dependency.PackageId, out var dependencyPackage))
                {
                    AddPackageAndDependencies(dependencyPackage);
                }
            }
        }

        AddPackageAndDependencies(package);
        return impactedPackageIds;
    }

    private static IReadOnlyList<string> BuildUpgradeImpactSet(
        InstalledPackageRecord package,
        IReadOnlyList<InstalledPackageRecord> packages)
    {
        var packagesById = packages.ToDictionary(candidate => candidate.PackageId, StringComparer.OrdinalIgnoreCase);
        var dependentsByDependencyId = packages
            .SelectMany(candidate => candidate.DependsOn.Select(dependency => new { DependencyId = dependency.PackageId, Package = candidate }))
            .GroupBy(entry => entry.DependencyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Package).ToArray(), StringComparer.OrdinalIgnoreCase);
        var impactedPackageIds = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPackageAndRelated(InstalledPackageRecord currentPackage)
        {
            if (!visited.Add(currentPackage.PackageId))
            {
                return;
            }

            impactedPackageIds.Add(currentPackage.PackageId);
            foreach (var dependency in currentPackage.DependsOn)
            {
                if (packagesById.TryGetValue(dependency.PackageId, out var dependencyPackage))
                {
                    AddPackageAndRelated(dependencyPackage);
                }
            }

            if (!dependentsByDependencyId.TryGetValue(currentPackage.PackageId, out var dependents))
            {
                return;
            }

            foreach (var dependent in dependents)
            {
                AddPackageAndRelated(dependent);
            }
        }

        AddPackageAndRelated(package);
        return impactedPackageIds;
    }

    private static IReadOnlyList<InstalledPackageRecord> BuildUninstallSet(
        InstalledPackageRecord package,
        IReadOnlyList<InstalledPackageRecord> packages)
    {
        var dependentsByDependencyId = packages
            .SelectMany(candidate => candidate.DependsOn.Select(dependency => new { DependencyId = dependency.PackageId, Package = candidate }))
            .GroupBy(entry => entry.DependencyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Package).OrderBy(candidate => candidate.PackageId, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var packagesToUninstall = new List<InstalledPackageRecord>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDependentsThenPackage(InstalledPackageRecord currentPackage)
        {
            if (!visited.Add(currentPackage.PackageId))
            {
                return;
            }

            if (dependentsByDependencyId.TryGetValue(currentPackage.PackageId, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    AddDependentsThenPackage(dependent);
                }
            }

            packagesToUninstall.Add(currentPackage);
        }

        AddDependentsThenPackage(package);
        return packagesToUninstall;
    }

    private async Task<IReadOnlyList<InstalledPackageRecord>> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.StateFilePath))
        {
            return [];
        }

        var state = JsonSerializer.Deserialize<InstalledPackageStateFile>(await File.ReadAllTextAsync(paths.StateFilePath, cancellationToken), JsonOptions);
        return state?.SchemaVersion == 1 ? state.Packages : [];
    }

    private IReadOnlyList<InstalledPackageRecord> RecoverCopiedPackages(IReadOnlyList<InstalledPackageRecord> existingPackages)
    {
        if (!Directory.Exists(paths.InstalledRootPath))
        {
            return [];
        }

        var existingPackageIds = existingPackages
            .Select(package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = EnumerateInstallCandidates()
            .Select(TryCreateRecoveredPackageRecord)
            .Where(package => package is not null)
            .Select(package => package!)
            .Where(package => !existingPackageIds.Contains(package.PackageId))
            .GroupBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(SelectLatestPackage)
            .ToArray();

        return candidates;
    }

    private IEnumerable<string> EnumerateInstallCandidates()
    {
        foreach (var packageFolder in EnumerateDirectories(paths.InstalledRootPath))
        {
            yield return packageFolder;

            foreach (var versionFolder in EnumerateDirectories(packageFolder))
            {
                yield return versionFolder;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static InstalledPackageRecord? TryCreateRecoveredPackageRecord(string installPath)
    {
        var manifestPath = InstalledPackageRecord.TryResolveManifestPath(installPath);
        var libraryFolder = InstalledPackageRecord.TryResolveLibraryFolder(installPath);
        if (manifestPath is null || libraryFolder is null)
        {
            return null;
        }

        SunderPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch
        {
            return null;
        }

        if (manifest?.ManifestVersion != 1
            || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly)
            || !File.Exists(Path.Combine(libraryFolder, manifest.EntryAssembly)))
        {
            return null;
        }

        var installedAtUtc = Directory.Exists(installPath)
            ? new DateTimeOffset(Directory.GetCreationTimeUtc(installPath), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        return new InstalledPackageRecord(
            manifest.Id.Trim(),
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id.Trim() : manifest.Name.Trim(),
            string.IsNullOrWhiteSpace(manifest.Summary) ? null : manifest.Summary.Trim(),
            manifest.Version.Trim(),
            manifest.EntryAssembly.Trim(),
            string.IsNullOrWhiteSpace(manifest.Icon) ? null : manifest.Icon.Trim(),
            (manifest.DependsOn ?? [])
                .Where(dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                                     && !string.IsNullOrWhiteSpace(dependency.VersionRange))
                .Select(dependency => new InstalledPackageDependencyRecord(dependency.PackageId!.Trim(), dependency.VersionRange!.Trim()))
                .ToArray(),
            Path.GetFullPath(installPath),
            IsEnabled: true,
            installedAtUtc);
    }

    private static InstalledPackageRecord SelectLatestPackage(IEnumerable<InstalledPackageRecord> packages)
    {
        var selected = packages.First();
        foreach (var candidate in packages.Skip(1))
        {
            if (IsNewerPackage(candidate, selected))
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private static bool IsNewerPackage(InstalledPackageRecord candidate, InstalledPackageRecord current)
    {
        if (PackageVersionRange.TryCompare(candidate.Version, current.Version, out var comparison))
        {
            return comparison > 0;
        }

        return string.Compare(candidate.Version, current.Version, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private async Task WriteStateAsync(IReadOnlyList<InstalledPackageRecord> packages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootPath);
        var state = new InstalledPackageStateFile(1, packages.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray());
        var temporaryPath = paths.StateFilePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(temporaryPath, paths.StateFilePath, overwrite: true);
    }
}
