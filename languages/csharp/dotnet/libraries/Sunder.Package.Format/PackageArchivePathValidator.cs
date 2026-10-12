namespace Sunder.Package.Format;

internal static class PackageArchivePathValidator
{
    public static bool TryParse(
        string? value,
        string label,
        ICollection<string> errors,
        out ArchiveRelativePath path,
        bool required = false,
        int maxLength = SunderPackageFormat.MaxLogicalPathLength,
        int maxDepth = SunderPackageFormat.MaxLogicalPathDepth)
    {
        if (string.Equals(value, SunderPackageFormat.RpcModelProjectionLogicalPath, StringComparison.OrdinalIgnoreCase))
        {
            path = default;
            errors.Add($"Package {label} cannot use the tool-reserved model projection logical path '{SunderPackageFormat.RpcModelProjectionLogicalPath}'.");
            return false;
        }
        if (ArchiveRelativePath.TryParse(
                value,
                maxLength,
                maxDepth,
                out path,
                out var error))
        {
            return true;
        }

        if (required && string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Package {label} is required.");
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Package {label} '{value}' is unsafe: {error}.");
        }
        return false;
    }
}
