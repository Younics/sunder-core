namespace Sunder.Package.Format;

public static class SunderPackageFormat
{
    public const int CurrentArchiveFormatVersion = 1;
    public const int CurrentManifestVersion = 1;
    public const int CurrentContentIndexVersion = 1;
    public const int CurrentSdkApiVersion = 1;
    public const string AppHostRole = "app";
    public const string RuntimeHostRole = "runtime";
    public const string ContractOnlyHostRole = "contract-only";
    public const string Extension = ".sunderpkg";
    public const string ManifestPath = "manifest/sunder-package.json";
    public const string ContentIndexPath = "manifest/content-index.json";
    public const string LibraryPayloadRoot = "payload/lib/";
    public const string AssetPayloadRoot = "payload/assets/";
    public const long MaxIconBytes = 1024L * 1024L;
    public const int MaxIconDimension = 8192;

    public static bool IsContentIndexPath(string path)
        => string.Equals(path, ContentIndexPath, StringComparison.Ordinal);

    public static string? GetContentRole(string path)
    {
        if (string.Equals(path, ManifestPath, StringComparison.Ordinal)) return "manifest";
        if (path.StartsWith(LibraryPayloadRoot + "runtimes/", StringComparison.Ordinal)) return "native";
        if (path.StartsWith(LibraryPayloadRoot, StringComparison.Ordinal)
            && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) return "assembly";
        if (path.StartsWith(LibraryPayloadRoot, StringComparison.Ordinal)) return "file";
        if (path.StartsWith(AssetPayloadRoot, StringComparison.Ordinal)) return "asset";
        return null;
    }

    public static bool IsAllowedArchivePath(string path)
        => IsContentIndexPath(path) || GetContentRole(path) is not null;

    public static bool IsSdkCapabilityId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value[0] == '.'
            || value[^1] == '.'
            || value.Contains("..", StringComparison.Ordinal)
            || value.Any(static character => character is not (>= 'a' and <= 'z')
                                                    and not (>= '0' and <= '9')
                                                    and not '.'
                                                    and not '-'))
        {
            return false;
        }

        var versionSeparator = value.LastIndexOf(".v", StringComparison.Ordinal);
        return versionSeparator > 0
               && versionSeparator + 2 < value.Length
               && value[(versionSeparator + 2)..].All(static character => character is >= '0' and <= '9')
               && value[versionSeparator + 2] != '0'
               && value[..versionSeparator].Split('.').All(static segment => segment.Length > 0
                                                                            && segment[0] != '-'
                                                                            && segment[^1] != '-');
    }
}
