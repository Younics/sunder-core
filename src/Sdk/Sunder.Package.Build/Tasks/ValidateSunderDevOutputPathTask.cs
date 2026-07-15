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

    [Output]
    public string NormalizedDevOutputPath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var output = Normalize(DevOutputPath);
            var project = Normalize(ProjectDirectory);
            var target = Normalize(TargetDirectory);
            var root = Path.GetPathRoot(output);
            if (string.IsNullOrWhiteSpace(root)
                || PathsEqual(output, root)
                || PathsEqual(output, project)
                || PathsEqual(output, target)
                || !IsDescendantOf(output, target)
                || !PathsEqual(Path.GetDirectoryName(output) ?? string.Empty, target)
                || !string.Equals(Path.GetFileName(output), GeneratedDirectoryName, PathComparison)
                || IsSourceDirectory(target, project))
            {
                Log.LogError(
                    $"SunderDevOutputPath '{DevOutputPath}' is unsafe. It must be a dedicated child directory "
                    + $"named '{GeneratedDirectoryName}' directly under TargetDir and TargetDir must not be a source directory.");
                return false;
            }

            if (Directory.Exists(output))
            {
                if ((File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
                {
                    Log.LogError($"SunderDevOutputPath '{DevOutputPath}' is unsafe because it is a symbolic link or reparse point.");
                    return false;
                }

                var markerPath = Path.Combine(output, MarkerFileName);
                if (!File.Exists(markerPath)
                    || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
                {
                    Log.LogError(
                        $"SunderDevOutputPath '{DevOutputPath}' already exists without the generated-output marker '{MarkerFileName}'; it will not be deleted.");
                    return false;
                }
            }

            NormalizedDevOutputPath = Path.EndsInDirectorySeparator(output)
                ? output
                : output + Path.DirectorySeparatorChar;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            Log.LogError($"SunderDevOutputPath '{DevOutputPath}' is invalid: {exception.Message}");
            return false;
        }
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, PathComparison);

    private static bool IsDescendantOf(string path, string parent)
    {
        var prefix = parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool IsSourceDirectory(string target, string project)
        => PathsEqual(target, Path.Combine(project, "Assets"))
           || IsDescendantOf(target, Path.Combine(project, "Assets"))
           || PathsEqual(target, Path.Combine(project, "src"))
           || IsDescendantOf(target, Path.Combine(project, "src"));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
