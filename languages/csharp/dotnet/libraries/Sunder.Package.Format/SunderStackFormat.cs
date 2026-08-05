namespace Sunder.Package.Format;

public static class SunderStackFormat
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentContentIndexVersion = 1;
    public const int CurrentReaderVersion = 1;
    public const string Extension = ".sunderstack";
    public const string ManifestPath = "manifest/sunder-stack.json";
    public const string ContentIndexPath = "manifest/content-index.json";
    public const string PayloadRoot = "payload/";
    public const string FragmentPayloadRoot = "payload/fragments/";
    public const string FragmentFileRoot = "payload/files/";
    public const string MediaPayloadRoot = "payload/media/";
    public const string FragmentJsonFeature = "fragment-json.v1";
    public const string MediaFeature = "media.v1";
    public const string RequiredInputsFeature = "required-inputs.v1";

    public static IReadOnlySet<string> SupportedFeatures { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FragmentJsonFeature,
        MediaFeature,
        RequiredInputsFeature,
    };

    public static bool IsContentIndexPath(string path)
        => string.Equals(path, ContentIndexPath, StringComparison.Ordinal);

    public static bool IsAllowedArchivePath(string path)
        => string.Equals(path, ManifestPath, StringComparison.Ordinal)
           || IsContentIndexPath(path)
           || path.StartsWith(FragmentPayloadRoot, StringComparison.Ordinal)
           || path.StartsWith(FragmentFileRoot, StringComparison.Ordinal)
           || path.StartsWith(MediaPayloadRoot, StringComparison.Ordinal);

    public static string BuildStackFileName(string stackId)
    {
        var fileName = string.IsNullOrWhiteSpace(stackId)
            ? "sunder-stack"
            : string.Concat(stackId.Trim().Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? char.ToLowerInvariant(character) : '-'));
        return fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + Extension;
    }
}
