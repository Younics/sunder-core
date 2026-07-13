namespace Sunder.Package.Format;

public sealed record SunderStackArchiveValidationResult
{
    public SunderStackArchiveValidationResult(
        SunderStackManifest? manifest,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        Manifest = manifest;
        Warnings = Array.AsReadOnly(warnings.ToArray());
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public SunderStackManifest? Manifest { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool Success => Manifest is not null && Errors.Count == 0;
}
