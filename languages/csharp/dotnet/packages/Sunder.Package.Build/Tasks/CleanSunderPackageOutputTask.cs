using Microsoft.Build.Framework;

namespace Sunder.Package.Build.Tasks;

public sealed class CleanSunderPackageOutputTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    [Required]
    public string OutputDiscoveryRoot { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    [Required]
    public string TargetDirectory { get; set; } = string.Empty;

    public bool IsTargetLeaf { get; set; }

    public override bool Execute()
    {
        try
        {
            if (!GeneratedOutputPathSafety.TryValidateDirectory(
                    OutputPath,
                    ProjectDirectory,
                    TargetDirectory,
                    ValidateSunderDevOutputPathTask.GeneratedDirectoryName,
                    out var output,
                    out var pathError))
            {
                Log.LogError($"Sunder composed output '{OutputPath}' is unsafe. {pathError}");
                return false;
            }

            var markerPath = IsTargetLeaf ? ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(output)
                : output + AggregateSunderPackageTask.MarkerFileSuffix;
            var discoveryKey = GeneratedOutputLock.TargetLeafDiscoveryKey(OutputDiscoveryRoot, output);
            using var outputLock = GeneratedOutputLock.Acquire([discoveryKey], [output]);
            GeneratedOutputTransaction.RecoverAndCleanup(output, markerPath);

            if (File.Exists(output))
            {
                Log.LogError($"Sunder composed output '{OutputPath}' is a file and will not be deleted.");
                return false;
            }

            if (Directory.Exists(output))
            {
                if (!File.Exists(markerPath)
                    || GeneratedOutputPathSafety.IsReparsePoint(output)
                    || GeneratedOutputPathSafety.IsReparsePoint(markerPath))
                {
                    Log.LogError(
                        $"Sunder composed output '{OutputPath}' is not a safely marked generated directory and will not be deleted.");
                    return false;
                }

                Directory.Delete(output, recursive: true);
            }

            if (File.Exists(markerPath))
            {
                if (GeneratedOutputPathSafety.IsReparsePoint(markerPath))
                {
                    Log.LogError($"Sunder composed output marker '{markerPath}' is unsafe and will not be deleted.");
                    return false;
                }
                File.Delete(markerPath);
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or TimeoutException or AggregateException)
        {
            Log.LogError($"Could not clean Sunder composed output '{OutputPath}': {exception.Message}");
            return false;
        }
    }
}
