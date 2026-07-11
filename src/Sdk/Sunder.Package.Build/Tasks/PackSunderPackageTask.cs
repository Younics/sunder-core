using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

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

            var manifestPath = Path.Combine(DevPackagePath, "sunder-package.json");
            if (!File.Exists(manifestPath))
            {
                Log.LogError($"Sunder dev package folder '{DevPackagePath}' does not contain sunder-package.json.");
                return false;
            }

            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            {
                Log.LogError($"Sunder dev package manifest '{manifestPath}' must not be a symbolic link or reparse point.");
                return false;
            }

            var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Package.Build", "pack", Guid.NewGuid().ToString("N"));
            try
            {
                PackageContentLayoutBuilder.Build(DevPackagePath, stagingPath, manifestPath);
                PackageContentIndexer.Write(stagingPath);

                var packageOutputDirectory = Path.GetDirectoryName(PackageOutputPath);
                if (!string.IsNullOrWhiteSpace(packageOutputDirectory))
                {
                    Directory.CreateDirectory(packageOutputDirectory);
                }

                DeterministicPackageArchiveWriter.Write(stagingPath, PackageOutputPath);
                Log.LogMessage(MessageImportance.High, $"Packed Sunder package to {PackageOutputPath}");
                return true;
            }
            finally
            {
                TryDeleteDirectory(stagingPath);
            }
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return false;
        }
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
}
