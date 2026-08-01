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

    public override bool Execute()
    {
        try
        {
            if (!Directory.Exists(DevPackagePath))
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' does not exist.");
                return false;
            }

            if ((File.GetAttributes(DevPackagePath) & FileAttributes.ReparsePoint) != 0)
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' must not be a symbolic link or reparse point.");
                return false;
            }

            var manifestPath = ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath).ToPlatformPath(DevPackagePath);
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
                PackageContentIndexer.Write(DevPackagePath);

                var stagingValidation = SunderPackageArchiveInspector
                    .ValidateExtractedPackageAsync(DevPackagePath)
                    .GetAwaiter()
                    .GetResult();
                if (!LogValidation(stagingValidation, logWarnings: false))
                {
                    return false;
                }

                var packageOutputDirectory = Path.GetDirectoryName(PackageOutputPath);
                if (!string.IsNullOrWhiteSpace(packageOutputDirectory))
                {
                    Directory.CreateDirectory(packageOutputDirectory);
                }

                DeterministicPackageArchiveWriter.Write(DevPackagePath, PackageOutputPath);
                try
                {
                    var archiveValidation = SunderPackageArchiveInspector
                        .ExtractAndValidateAsync(PackageOutputPath, archiveValidationPath)
                        .GetAwaiter()
                        .GetResult();
                    if (!LogValidation(archiveValidation))
                    {
                        TryDeleteFile(PackageOutputPath);
                        return false;
                    }
                }
                catch
                {
                    TryDeleteFile(PackageOutputPath);
                    throw;
                }
                Log.LogMessage(MessageImportance.High, $"Packed Sunder package to {PackageOutputPath}");
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
