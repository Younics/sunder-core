namespace Sunder.Package.Format;

public sealed record SunderStackArchiveValidationResult
{
    public SunderStackArchiveValidationResult(
        SunderStackManifest? manifest,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
        : this(
            manifest,
            warnings,
            errors,
            Enumerable.Repeat(SunderStackArchiveErrorCategory.Validation, errors.Count).ToArray())
    {
    }

    internal SunderStackArchiveValidationResult(
        SunderStackManifest? manifest,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        IReadOnlyList<SunderStackArchiveErrorCategory> errorCategories)
    {
        if (errors.Count != errorCategories.Count)
        {
            throw new ArgumentException("Stack archive errors and categories must have matching counts.", nameof(errorCategories));
        }

        Manifest = manifest;
        Warnings = Array.AsReadOnly(warnings.ToArray());
        Errors = Array.AsReadOnly(errors.ToArray());
        ErrorCategories = Array.AsReadOnly(errorCategories.ToArray());
    }

    public SunderStackManifest? Manifest { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<string> Errors { get; }

    internal IReadOnlyList<SunderStackArchiveErrorCategory> ErrorCategories { get; }

    public bool Success => Manifest is not null && Errors.Count == 0;
}

internal enum SunderStackArchiveErrorCategory
{
    Validation,
    MissingEntry,
}
