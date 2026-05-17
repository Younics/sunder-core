namespace Sunder.App.Services;

internal static class CliPathList
{
    public static bool Contains(string? pathValue, string expectedPath, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var separator = isWindows ? ';' : ':';
        return pathValue
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => PathsEqual(entry, expectedPath, isWindows));
    }

    public static string Append(string? pathValue, string pathEntry, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return pathEntry;
        }

        var trimmedPath = pathValue.Trim();
        var separator = isWindows ? ';' : ':';
        if (trimmedPath.EndsWith(separator))
        {
            return $"{trimmedPath}{pathEntry}";
        }

        return $"{trimmedPath}{separator}{pathEntry}";
    }

    private static bool PathsEqual(string path, string expectedPath, bool isWindows)
    {
        var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizePathForComparison(path), NormalizePathForComparison(expectedPath), comparison);
    }

    private static string NormalizePathForComparison(string path)
    {
        var normalized = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // Keep the raw expanded value when the PATH entry is not a valid file-system path.
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
