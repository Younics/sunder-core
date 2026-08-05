using Microsoft.Build.Framework;

namespace Sunder.Package.Build.Tasks;

public sealed class ValidateSunderDevOutputPathTask : Microsoft.Build.Utilities.Task
{
    public const string GeneratedDirectoryName = "sunder-dev";
    public const string MarkerFileName = ".sunder-generated-output";

    [Required]
    public string DevOutputPath { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    [Required]
    public string TargetDirectory { get; set; } = string.Empty;

    [Required]
    public string DiscoveryRoot { get; set; } = string.Empty;

    [Output]
    public string NormalizedDevOutputPath { get; private set; } = string.Empty;

    [Output]
    public string OwnershipMarkerPath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            if (!GeneratedOutputPathSafety.TryValidateDirectory(
                    DevOutputPath,
                    ProjectDirectory,
                    TargetDirectory,
                    GeneratedDirectoryName,
                    out var output,
                    out var error))
            {
                Log.LogError($"SunderDevOutputPath '{DevOutputPath}' is unsafe. {error}");
                return false;
            }

            var markerPath = GetOwnershipMarkerPath(output);
            var discoveryKey = GeneratedOutputLock.TargetLeafDiscoveryKey(DiscoveryRoot, output);
            using var outputLock = GeneratedOutputLock.Acquire([discoveryKey], [output]);
            GeneratedOutputTransaction.RecoverAndCleanup(output, markerPath);
            if (Directory.Exists(output))
            {
                if (GeneratedOutputPathSafety.IsReparsePoint(output))
                {
                    Log.LogError($"SunderDevOutputPath '{DevOutputPath}' is unsafe because it is a symbolic link or reparse point.");
                    return false;
                }

                if (!File.Exists(markerPath)
                    || GeneratedOutputPathSafety.IsReparsePoint(markerPath))
                {
                    Log.LogError(
                        $"SunderDevOutputPath '{DevOutputPath}' already exists without the generated-output marker '{MarkerFileName}'; it will not be deleted.");
                    return false;
                }
            }

            NormalizedDevOutputPath = Path.EndsInDirectorySeparator(output)
                ? output
                : output + Path.DirectorySeparatorChar;
            OwnershipMarkerPath = GetOwnershipMarkerPath(output);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or TimeoutException or AggregateException)
        {
            Log.LogError($"SunderDevOutputPath '{DevOutputPath}' is invalid: {exception.Message}");
            return false;
        }
    }

    public static string GetOwnershipMarkerPath(string devOutputPath)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(devOutputPath)) + MarkerFileName;
}
