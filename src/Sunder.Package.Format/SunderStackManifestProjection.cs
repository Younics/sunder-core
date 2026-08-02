using System.Collections.ObjectModel;

namespace Sunder.Package.Format;

internal static class SunderStackManifestProjection
{
    public static SunderStackManifest Create(SunderStackManifest source)
        => new()
        {
            SchemaVersion = source.SchemaVersion,
            MinReaderVersion = source.MinReaderVersion,
            Features = ReadOnly(source.Features, static feature => feature),
            RequiredFeatures = ReadOnly(source.RequiredFeatures, static feature => feature),
            StackId = source.StackId,
            Name = source.Name,
            Summary = source.Summary,
            ReadmeMarkdown = source.ReadmeMarkdown,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Packages = ReadOnly(source.Packages, CopyPackage),
            Fragments = ReadOnly(source.Fragments, CopyFragment),
            Media = ReadOnly(source.Media, CopyMedia),
        };

    private static SunderStackPackageRequirement CopyPackage(SunderStackPackageRequirement source)
        => new()
        {
            PackageId = source.PackageId,
            InstallTag = source.InstallTag,
            CreatedWithVersion = source.CreatedWithVersion,
            MinimumVersion = source.MinimumVersion,
            Required = source.Required,
        };

    private static SunderStackFragmentManifest CopyFragment(SunderStackFragmentManifest source)
        => new()
        {
            FragmentId = source.FragmentId,
            OwnerPackageId = source.OwnerPackageId,
            ContributorId = source.ContributorId,
            SchemaId = source.SchemaId,
            SchemaVersion = source.SchemaVersion,
            DisplayName = source.DisplayName,
            Description = source.Description,
            DefaultSelected = source.DefaultSelected,
            PayloadPath = source.PayloadPath,
            RequiredInputs = ReadOnly(source.RequiredInputs, CopyInput),
            Preview = source.Preview is null ? null : CopyPreview(source.Preview),
        };

    private static SunderStackRequiredInputManifest CopyInput(SunderStackRequiredInputManifest source)
        => new()
        {
            InputId = source.InputId,
            Label = source.Label,
            Description = source.Description,
            Sensitivity = source.Sensitivity,
            DefaultValue = source.DefaultValue,
            Required = source.Required,
        };

    private static SunderStackFragmentPreview CopyPreview(SunderStackFragmentPreview source)
        => new()
        {
            SourceItemId = source.SourceItemId,
            Kind = source.Kind,
            DisplayDetails = ReadOnly(source.DisplayDetails, CopyDisplayDetail),
        };

    private static SunderStackFragmentDisplayDetail CopyDisplayDetail(SunderStackFragmentDisplayDetail source)
        => new()
        {
            Label = source.Label,
            Value = source.Value,
            Behavior = source.Behavior,
        };

    private static SunderStackMediaManifest CopyMedia(SunderStackMediaManifest source)
        => new()
        {
            Path = source.Path,
            FileName = source.FileName,
            ContentType = source.ContentType,
            Size = source.Size,
            AltText = source.AltText,
            SortOrder = source.SortOrder,
        };

    private static IReadOnlyList<TResult>? ReadOnly<TSource, TResult>(
        IReadOnlyList<TSource>? source,
        Func<TSource, TResult> projection)
        => source is null
            ? null
            : new ReadOnlyCollection<TResult>(source.Select(projection).ToArray());
}
