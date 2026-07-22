namespace Sunder.Package.Format;

internal static class StackArchivePathValidator
{
    public static bool TryParse(
        string? value,
        string label,
        ICollection<string> errors,
        out ArchiveRelativePath path,
        bool required = false)
    {
        if (ArchiveRelativePath.TryParse(value, int.MaxValue, int.MaxValue, out path, out var error))
        {
            return true;
        }

        errors.Add(required && string.IsNullOrWhiteSpace(value)
            ? $"Stack {label} is required."
            : $"Stack {label} '{value}' is unsafe: {error}.");
        return false;
    }
}
