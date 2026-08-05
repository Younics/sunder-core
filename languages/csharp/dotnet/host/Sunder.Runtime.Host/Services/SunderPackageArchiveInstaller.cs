using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class SunderPackageArchiveInstaller(RuntimePackagePaths paths)
{
    internal RuntimePackagePaths Paths => paths;

    public async Task<PackageArchiveMutationPreparationResult> PrepareAsync(
        string packagePath,
        bool isEnabled,
        CancellationToken cancellationToken = default,
        InstalledPackageProvenanceRecord? provenance = null)
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

        provenance ??= InstalledPackageProvenanceRecord.LocalArchive(
            await ComputeFileHashAsync(packagePath, cancellationToken));

        Directory.CreateDirectory(paths.StagingRootPath);
        var stagingPath = paths.CreateStagingPath();
        try
        {
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                packagePath,
                stagingPath,
                cancellationToken);
            if (!validation.Success || validation.Manifest is null || validation.ContentIndex is null)
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
            var installedPath = Path.GetFullPath(paths.GetInstalledPackagePath(manifest.Id!, manifest.Version!));
            var installedRecord = CreateInstalledPackageRecord(
                manifest,
                validation.ContentIndex,
                stagingPath,
                installedPath,
                isEnabled,
                provenance);
            return new PackageArchiveMutationPreparationResult(
                new PreparedPackageArchiveMutation(
                    stagingPath,
                    installedPath,
                    installedRecord),
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
        SunderPackageContentIndex contentIndex,
        string sourcePath,
        string installedPath,
        bool isEnabled,
        InstalledPackageProvenanceRecord provenance)
        => new(
            manifest.Id!,
            manifest.Name!,
            manifest.Summary,
            manifest.Version!,
            manifest.Icon,
            installedPath,
            Path.Combine(installedPath, SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar)),
            ComputeContentIdentity(sourcePath),
            contentIndex.Files!
                .Select(entry => new InstalledPackageContentRecord(entry!.Path!, entry.Sha256!, entry.Size))
                .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
                .ToArray(),
            (manifest.DependsOn ?? [])
                .Select(dependency => new InstalledPackageDependencyRecord(dependency.PackageId!, dependency.VersionRange!))
                .ToArray(),
            isEnabled,
            DateTimeOffset.UtcNow,
            provenance);

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string ComputeContentIdentity(string sourcePath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in new[] { SunderPackageFormat.ManifestPath, SunderPackageFormat.ContentIndexPath })
        {
            var path = Path.Combine(sourcePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

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
