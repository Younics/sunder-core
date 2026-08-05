using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

public sealed class PackSunderPackageTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string DevPackagePath { get; set; } = string.Empty;

    [Required]
    public string PackageOutputPath { get; set; } = string.Empty;

    [Required]
    public string DevPackageDiscoveryRoot { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public override bool Execute()
    {
        string? stagedPackagePath = null;
        try
        {
            if (!GeneratedOutputPathSafety.TryValidateArchive(
                    PackageOutputPath,
                    ProjectDirectory,
                    out var packageOutputPath,
                    out var pathError))
            {
                Log.LogError($"Sunder package output path '{PackageOutputPath}' is unsafe. {pathError}");
                return false;
            }

            var devPackagePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DevPackagePath));
            var discoveryKey = GeneratedOutputLock.TargetLeafDiscoveryKey(DevPackageDiscoveryRoot, devPackagePath);
            using var outputLock = GeneratedOutputLock.Acquire([discoveryKey], [devPackagePath, packageOutputPath]);
            GeneratedOutputTransaction.RecoverAndCleanup(packageOutputPath);
            GeneratedOutputTransaction.RecoverAndCleanup(
                devPackagePath,
                ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(devPackagePath),
                devPackagePath + AggregateSunderPackageTask.MarkerFileSuffix);
            if (Directory.Exists(packageOutputPath)
                || File.Exists(packageOutputPath)
                && GeneratedOutputPathSafety.IsReparsePoint(packageOutputPath))
            {
                Log.LogError($"Sunder package output path '{PackageOutputPath}' is no longer a safe regular file after lock acquisition.");
                return false;
            }

            if (!Directory.Exists(devPackagePath))
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' does not exist.");
                return false;
            }

            if ((File.GetAttributes(devPackagePath) & FileAttributes.ReparsePoint) != 0)
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' must not be a symbolic link or reparse point.");
                return false;
            }

            var manifestPath = ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath).ToPlatformPath(devPackagePath);
            if (!File.Exists(manifestPath))
            {
                Log.LogError(
                    $"Sunder dev package folder '{DevPackagePath}' does not contain {SunderPackageFormat.ManifestPath}.");
                return false;
            }

            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            {
                Log.LogError($"Sunder dev package manifest '{manifestPath}' must not be a symbolic link or reparse point.");
                return false;
            }

            var archiveValidationPath = Path.Combine(
                Path.GetTempPath(),
                "Sunder.Package.Build",
                "pack-validation",
                Guid.NewGuid().ToString("N"));
            try
            {
                PackageContentIndexer.Write(devPackagePath);

                var stagingValidation = SunderPackageArchiveInspector
                    .ValidateExtractedPackageAsync(devPackagePath)
                    .GetAwaiter()
                    .GetResult();
                if (!LogValidation(stagingValidation, logWarnings: false))
                {
                    return false;
                }

                var packageOutputDirectory = Path.GetDirectoryName(packageOutputPath);
                if (!string.IsNullOrWhiteSpace(packageOutputDirectory))
                {
                    Directory.CreateDirectory(packageOutputDirectory);
                }

                stagedPackagePath = packageOutputPath + ".stage-" + Guid.NewGuid().ToString("N");
                DeterministicPackageArchiveWriter.Write(devPackagePath, stagedPackagePath);
                var archiveValidation = SunderPackageArchiveInspector
                    .ExtractAndValidateAsync(stagedPackagePath, archiveValidationPath)
                    .GetAwaiter()
                    .GetResult();
                if (!LogValidation(archiveValidation))
                {
                    return false;
                }
                GeneratedOutputTransaction.Commit(
                    new GeneratedOutputTransaction.StagedOutput(packageOutputPath, stagedPackagePath));
                stagedPackagePath = null;
                Log.LogMessage(MessageImportance.High, $"Packed Sunder package to {packageOutputPath}");
                return true;
            }
            finally
            {
                TryDeleteDirectory(archiveValidationPath);
            }
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
        finally
        {
            if (stagedPackagePath is not null) TryDeleteFile(stagedPackagePath);
        }
    }

    private bool LogValidation(SunderPackageArchiveValidationResult validation, bool logWarnings = true)
    {
        if (logWarnings)
        {
            foreach (var warning in validation.Warnings)
            {
                Log.LogWarning(warning);
            }
        }
        foreach (var error in validation.Errors)
        {
            Log.LogError(error);
        }
        return validation.Success;
    }

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
            // Best effort cleanup for temporary package staging.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for a package archive that failed final validation.
        }
    }
}
