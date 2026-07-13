namespace Sunder.Package.Format;

internal static class StackArchivePathValidator
{
    public static bool TryParse(
        string? value,
        string label,
        ICollection<string> errors,
        out ArchiveRelativePath path)
    {
        if (ArchiveRelativePath.TryParse(value, int.MaxValue, int.MaxValue, out path, out var error))
        {
            return true;
        }

        errors.Add($"Stack {label} '{value}' is unsafe: {error}.");
        return false;
    }
}
