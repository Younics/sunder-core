using Sunder.PackageManagement;
using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed class SunderPackageArchiveInstaller(RuntimePackagePaths paths, InstalledPackageStore store)
{
    public async Task<PackageOperationResult> InstallFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return PackageOperationResults.Failure("Package path is required.");
        }

        packagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(packagePath))
        {
            return PackageOperationResults.Failure($"Package file '{packagePath}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(packagePath), ".sunderpkg", StringComparison.OrdinalIgnoreCase))
        {
            return PackageOperationResults.Failure($"Package file '{packagePath}' must use the .sunderpkg extension.");
        }

        Directory.CreateDirectory(paths.StagingRootPath);
        var stagingPath = paths.CreateStagingPath();
        var installedPath = string.Empty;

        try
        {
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(packagePath, stagingPath, cancellationToken);
            if (validation.Errors.Count > 0 || validation.Manifest is null)
            {
                return new PackageOperationResult(false, "Package validation failed.", RuntimeSessionApplied: false, RequiresAppRestart: false, validation.Warnings, validation.Errors);
            }

            var manifest = validation.Manifest;
            var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(manifest);
            if (compatibilityErrors.Count > 0)
            {
                return new PackageOperationResult(false, "Package SDK compatibility validation failed.", RuntimeSessionApplied: false, RequiresAppRestart: false, validation.Warnings, compatibilityErrors);
            }

            installedPath = paths.GetInstalledPackagePath(manifest.Id!, manifest.Version!);
            if (Directory.Exists(installedPath))
            {
                return PackageOperationResults.Failure($"Package '{manifest.Id}' version '{manifest.Version}' is already installed.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
            Directory.Move(stagingPath, installedPath);

            var record = CreateInstalledPackageRecord(manifest, installedPath, isEnabled: true);

            var storeResult = await store.InstallAsync(record, cancellationToken);
            if (!storeResult.Success)
            {
                TryDeleteDirectory(installedPath);
                return storeResult;
            }

            return storeResult;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(installedPath))
            {
                TryDeleteDirectory(installedPath);
            }

            return PackageOperationResults.Failure($"Failed to install package '{packagePath}': {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    public async Task<PackageOperationResult> UpgradeFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return PackageOperationResults.Failure("Package id is required.");
        }

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return PackageOperationResults.Failure("Package path is required.");
        }

        packagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(packagePath))
        {
            return PackageOperationResults.Failure($"Package file '{packagePath}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(packagePath), ".sunderpkg", StringComparison.OrdinalIgnoreCase))
        {
            return PackageOperationResults.Failure($"Package file '{packagePath}' must use the .sunderpkg extension.");
        }

        Directory.CreateDirectory(paths.StagingRootPath);
        var stagingPath = paths.CreateStagingPath();
        var installedPath = string.Empty;
        var backupPath = string.Empty;
        var previousInstallPath = string.Empty;
        var installedPathContainsNewPackage = false;

        try
        {
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(packagePath, stagingPath, cancellationToken);
            if (validation.Errors.Count > 0 || validation.Manifest is null)
            {
                return new PackageOperationResult(false, "Package validation failed.", RuntimeSessionApplied: false, RequiresAppRestart: false, validation.Warnings, validation.Errors);
            }

            var manifest = validation.Manifest;
            var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(manifest);
            if (compatibilityErrors.Count > 0)
            {
                return new PackageOperationResult(false, "Package SDK compatibility validation failed.", RuntimeSessionApplied: false, RequiresAppRestart: false, validation.Warnings, compatibilityErrors);
            }

            if (!string.Equals(manifest.Id, packageId, StringComparison.OrdinalIgnoreCase))
            {
                return PackageOperationResults.Failure($"Package archive '{manifest.Id}' does not match selected package '{packageId}'.");
            }

            var installedPackage = await store.GetAsync(packageId, cancellationToken);
            if (installedPackage is null)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' is not installed.");
            }

            if (!PackageVersionRange.TryCompare(manifest.Version!, installedPackage.Version, out var versionComparison))
            {
                return PackageOperationResults.Failure($"Package version '{manifest.Version}' or installed version '{installedPackage.Version}' is invalid.");
            }

            if (versionComparison < 0 && !allowDowngrade)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' cannot be downgraded from {installedPackage.Version} to {manifest.Version} without allowing downgrades.");
            }

            if (versionComparison == 0 && !reinstall)
            {
                return PackageOperationResults.Failure($"Package '{packageId}' version {manifest.Version} is already installed.");
            }

            previousInstallPath = installedPackage.InstallPath;
            installedPath = paths.GetInstalledPackagePath(manifest.Id!, manifest.Version!);
            if (Directory.Exists(installedPath)
                && !string.Equals(installedPath, installedPackage.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                return PackageOperationResults.Failure($"Package '{manifest.Id}' version '{manifest.Version}' is already installed on disk.");
            }

            var record = CreateInstalledPackageRecord(manifest, installedPath, installedPackage.IsEnabled);
            if (string.Equals(installedPath, installedPackage.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                backupPath = installedPackage.InstallPath + ".backup-" + Guid.NewGuid().ToString("N");
                if (Directory.Exists(installedPackage.InstallPath))
                {
                    Directory.Move(installedPackage.InstallPath, backupPath);
                }

                Directory.Move(stagingPath, installedPath);
                installedPathContainsNewPackage = true;
                var result = await store.UpgradeAsync(packageId, record, allowDowngrade, reinstall, cancellationToken);
                if (!result.Success)
                {
                    TryDeleteDirectory(installedPath);
                    RestoreBackupDirectory(backupPath, installedPackage.InstallPath);
                    return result;
                }

                TryDeleteDirectory(backupPath);
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
            Directory.Move(stagingPath, installedPath);
            installedPathContainsNewPackage = true;

            var storeResult = await store.UpgradeAsync(packageId, record, allowDowngrade, reinstall, cancellationToken);
            if (!storeResult.Success)
            {
                TryDeleteDirectory(installedPath);
                return storeResult;
            }

            TryDeleteDirectory(installedPackage.InstallPath);
            return storeResult;
        }
        catch (Exception ex)
        {
            if (installedPathContainsNewPackage && Directory.Exists(installedPath))
            {
                TryDeleteDirectory(installedPath);
            }

            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                RestoreBackupDirectory(backupPath, previousInstallPath);
            }

            return PackageOperationResults.Failure($"Failed to upgrade package '{packagePath}': {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static InstalledPackageRecord CreateInstalledPackageRecord(
        SunderPackageManifest manifest,
        string installedPath,
        bool isEnabled)
        => new(
            manifest.Id!,
            manifest.Name!,
            manifest.Summary,
            manifest.Version!,
            manifest.EntryAssembly!,
            manifest.Icon,
            (manifest.DependsOn ?? [])
                .Select(dependency => new InstalledPackageDependencyRecord(dependency.PackageId!, dependency.VersionRange!))
                .ToArray(),
            installedPath,
            isEnabled,
            DateTimeOffset.UtcNow);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for failed package install staging.
        }
    }

    private static void RestoreBackupDirectory(string backupPath, string restorePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupPath)
                || string.IsNullOrWhiteSpace(restorePath)
                || !Directory.Exists(backupPath)
                || Directory.Exists(restorePath))
            {
                return;
            }

            Directory.Move(backupPath, restorePath);
        }
        catch
        {
            // Best effort rollback for failed same-version package replacement.
        }
    }

}
