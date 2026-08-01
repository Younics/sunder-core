namespace Sunder.Package.Format;

public sealed record SunderPackageExtractedProjection(
    string RootPath,
    SunderPackageProjectionKey Key,
    IReadOnlyList<ArchiveRelativePath> PayloadPaths);

public sealed record SunderPackageProjectionArchiveValidationResult(
    SunderPackageProjectionDescriptor? Descriptor,
    SunderPackageManifest? Manifest,
    SunderPackageContentIndex? ContentIndex,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public SunderPackageExtractedProjection? ExtractedProjection { get; init; }

    public bool Success => Descriptor is not null
                           && Manifest is not null
                           && ContentIndex is not null
                           && ExtractedProjection is not null
                           && Errors.Count == 0;
}
