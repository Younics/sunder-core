namespace Sunder.Package.Format;

public sealed record SunderPackageArchiveValidationResult(
    SunderPackageManifest? Manifest,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public SunderPackageContentIndex? ContentIndex { get; init; }

    public bool Success => Manifest is not null && ContentIndex is not null && Errors.Count == 0;
}
