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
