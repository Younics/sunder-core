using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class GeneratedOutputPathSafety
{
    public static bool TryValidateDirectory(
        string outputPath,
        string projectDirectory,
        string targetDirectory,
        string requiredDirectoryName,
        out string normalizedOutputPath,
        out string error)
    {
        normalizedOutputPath = string.Empty;
        var output = NormalizeDirectory(outputPath);
        var project = NormalizeDirectory(projectDirectory);
        var target = NormalizeDirectory(targetDirectory);
        var root = Path.GetPathRoot(output);
        if (string.IsNullOrWhiteSpace(root)
            || PathsEqual(output, root)
            || PathsEqual(output, project)
            || PathsEqual(output, target)
            || !IsDescendantOf(output, target)
            || !PathsEqual(Path.GetDirectoryName(output) ?? string.Empty, target)
            || !string.Equals(Path.GetFileName(output), requiredDirectoryName, PathComparison)
            || IsSourceDirectory(target, project))
        {
            error = $"It must be a dedicated child directory named '{requiredDirectoryName}' directly under "
                    + "TargetDir, and TargetDir must not be a source directory.";
            return false;
        }

        normalizedOutputPath = output;
        error = string.Empty;
        return true;
    }

    public static bool TryValidateArchive(
        string outputPath,
        string projectDirectory,
        out string normalizedOutputPath,
        out string error)
    {
        normalizedOutputPath = string.Empty;
        var output = Path.GetFullPath(outputPath);
        var project = NormalizeDirectory(projectDirectory);
        var outputDirectoryValue = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(outputDirectoryValue))
        {
            error = "It must have a parent directory.";
            return false;
        }

        var outputDirectory = NormalizeDirectory(outputDirectoryValue);
        var root = Path.GetPathRoot(outputDirectory);
        if (string.IsNullOrWhiteSpace(root)
            || PathsEqual(outputDirectory, root)
            || IsSourceDirectory(outputDirectory, project))
        {
            error = "It must be written outside source directories and filesystem roots.";
            return false;
        }
        if (!string.Equals(Path.GetExtension(output), SunderPackageFormat.Extension, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(output)))
        {
            error = $"It must be a file with the '{SunderPackageFormat.Extension}' extension.";
            return false;
        }
        if (Directory.Exists(output))
        {
            error = "It resolves to an existing directory.";
            return false;
        }
        if (File.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
        {
            error = "It must not be a symbolic link or reparse point.";
            return false;
        }

        normalizedOutputPath = output;
        error = string.Empty;
        return true;
    }

    public static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, PathComparison);

    private static bool IsDescendantOf(string path, string parent)
    {
        var prefix = parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool IsSourceDirectory(string candidate, string project)
        => PathsEqual(candidate, project)
           || PathsEqual(candidate, Path.Combine(project, "Assets"))
           || IsDescendantOf(candidate, Path.Combine(project, "Assets"))
           || PathsEqual(candidate, Path.Combine(project, "src"))
           || IsDescendantOf(candidate, Path.Combine(project, "src"));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
