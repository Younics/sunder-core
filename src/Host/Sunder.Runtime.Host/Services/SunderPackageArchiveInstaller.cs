using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class SunderPackageArchiveInstaller(RuntimePackagePaths paths)
{
    internal RuntimePackagePaths Paths => paths;

    public async Task<PackageArchiveMutationPreparationResult> PrepareAsync(
        string packagePath,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return PreparationFailure("Package path is required.");
        }

        packagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(packagePath))
        {
            return PreparationFailure($"Package file '{packagePath}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(packagePath), ".sunderpkg", StringComparison.OrdinalIgnoreCase))
        {
            return PreparationFailure($"Package file '{packagePath}' must use the .sunderpkg extension.");
        }

        Directory.CreateDirectory(paths.StagingRootPath);
        var stagingPath = paths.CreateStagingPath();
        try
        {
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                packagePath,
                stagingPath,
                cancellationToken);
            if (validation.Errors.Count > 0 || validation.Manifest is null)
            {
                TryDeleteDirectory(stagingPath);
                return new PackageArchiveMutationPreparationResult(
                    null,
                    new PackageOperationResult(
                        false,
                        "Package validation failed.",
                        RuntimeSessionApplied: false,
                        RequiresAppRestart: false,
                        validation.Warnings,
                        validation.Errors));
            }

            var manifest = validation.Manifest;
            var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(manifest);
            if (compatibilityErrors.Count > 0)
            {
                TryDeleteDirectory(stagingPath);
                return new PackageArchiveMutationPreparationResult(
                    null,
                    new PackageOperationResult(
                        false,
                        "Package SDK compatibility validation failed.",
                        RuntimeSessionApplied: false,
                        RequiresAppRestart: false,
                        validation.Warnings,
                        compatibilityErrors));
            }

            var installedPath = Path.GetFullPath(paths.GetInstalledPackagePath(manifest.Id!, manifest.Version!));
            return new PackageArchiveMutationPreparationResult(
                new PreparedPackageArchiveMutation(
                    stagingPath,
                    installedPath,
                    CreateInstalledPackageRecord(manifest, stagingPath, isEnabled),
                    CreateInstalledPackageRecord(manifest, installedPath, isEnabled)),
                null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(stagingPath);
            return PreparationFailure($"Failed to prepare package '{packagePath}': {ex.Message}");
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

    private static PackageArchiveMutationPreparationResult PreparationFailure(string message)
        => new(null, PackageOperationResults.Failure(message));

    internal static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Staging and committed garbage collection retries this cleanup later.
        }
    }
}
