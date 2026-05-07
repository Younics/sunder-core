namespace Sunder.PackageManagement;

public sealed record SunderPackageArchiveValidationResult(
    SunderPackageManifest? Manifest,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Success => Manifest is not null && Errors.Count == 0;
}
