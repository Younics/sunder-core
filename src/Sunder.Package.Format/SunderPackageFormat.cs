namespace Sunder.Package.Format;

public static class SunderPackageFormat
{
    private static readonly IReadOnlyList<string> Rids = Array.AsReadOnly(
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);

    public const int CurrentArchiveFormatVersion = 1;
    public const int CurrentManifestVersion = 1;
    public const int CurrentContentIndexVersion = 1;
    public const string AppHostRole = "app";
    public const string RuntimeHostRole = "runtime";
    public const string AvaloniaTargetKind = "avalonia";
    public const string WebTargetKind = "web";
    public const string DotnetTargetKind = "dotnet";
    public const string ProcessTargetKind = "process";
    public const string DiscoverContractAction = "discover";
    public const string InvokeContractAction = "invoke";
    public const string SubscribeContractAction = "subscribe";
    public const string Extension = ".sunderpkg";
    public const string ManifestPath = "manifest/sunder-package.json";
    public const string ContentIndexPath = "manifest/content-index.json";
    public const string SharedPayloadRoot = "payload/shared/";
    public const string AppPayloadRoot = "payload/app/";
    public const string RuntimePayloadRoot = "payload/runtime/";
    public const long MaxIconBytes = 1024L * 1024L;
    public const int MaxIconDimension = 8192;
    public const long MaxContractDescriptorBytes = 4L * 1024L * 1024L;
    public const int MaxArchivePathLength = 240;
    public const int MaxArchivePathDepth = 32;
    public const int MaxLogicalPathLength = 200;
    public const int MaxLogicalPathDepth = 24;
    public const int MaxTargets = 12;
    public const int MaxWebViewsPerTarget = 32;
    public const int MaxWebViewDisplayNameLength = 128;
    public const int MaxWebViewRouteLength = 200;
    public const int MaxDependencies = 128;
    public const int MaxContractBundles = 128;
    public const int MaxContractUses = 128;
    public const int MaxProviders = 128;
    public const int MaxHostCapabilities = 64;
    public const int MaxHostCapabilityLength = 128;
    public const int MaxContentIndexEntries = 4096;

    public static IReadOnlyList<string> SupportedRuntimeIdentifiers => Rids;

    public static bool IsContentIndexPath(string path)
        => string.Equals(path, ContentIndexPath, StringComparison.Ordinal);

    public static bool IsHostRole(string? value)
        => value is AppHostRole or RuntimeHostRole;

    public static bool IsRuntimeIdentifier(string? value)
        => value is not null && Rids.Contains(value, StringComparer.Ordinal);

    public static bool IsTargetKind(string? role, string? kind)
        => role switch
        {
            AppHostRole => kind is AvaloniaTargetKind or WebTargetKind,
            RuntimeHostRole => kind is DotnetTargetKind or ProcessTargetKind,
            _ => false,
        };

    public static bool IsContractAction(string? value)
        => value is DiscoverContractAction or InvokeContractAction or SubscribeContractAction;

    public static bool IsWebViewPlacement(string? value)
        => value is "leftTop" or "middle" or "rightTop" or "leftBottom" or "rightBottom";

    public static bool IsWebViewRoute(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxWebViewRouteLength
            || value[0] != '/'
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal)
            || value.Contains('#', StringComparison.Ordinal)
            || value.Contains('%', StringComparison.Ordinal)
            || value.Any(static character => character is < '!' or > '~'))
        {
            return false;
        }

        if (value == "/")
        {
            return true;
        }

        if (value[^1] == '/')
        {
            return false;
        }

        return value[1..].Split('/').All(static segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && segment.All(static character => char.IsAsciiLetterOrDigit(character)
                                               || character is '-' or '_' or '.' or '~'));
    }

    internal static bool TryClassifyPayloadPath(
        string path,
        out SunderPackagePayloadLayer layer,
        out string? role,
        out string? rid,
        out string logicalPath)
    {
        layer = default;
        role = null;
        rid = null;
        logicalPath = string.Empty;
        if (TryStrip(path, SharedPayloadRoot, out logicalPath))
        {
            layer = SunderPackagePayloadLayer.Shared;
            return true;
        }

        foreach (var candidateRole in new[] { AppHostRole, RuntimeHostRole })
        {
            var roleRoot = $"payload/{candidateRole}/";
            if (TryStrip(path, roleRoot + "shared/", out logicalPath))
            {
                layer = SunderPackagePayloadLayer.RoleShared;
                role = candidateRole;
                return true;
            }

            foreach (var candidateRid in Rids)
            {
                if (!TryStrip(path, roleRoot + candidateRid + "/", out logicalPath)) continue;
                layer = SunderPackagePayloadLayer.Target;
                role = candidateRole;
                rid = candidateRid;
                return true;
            }
        }

        return false;
    }

    public static bool IsAllowedArchivePath(string path)
        => IsContentIndexPath(path)
           || string.Equals(path, ManifestPath, StringComparison.Ordinal)
           || TryClassifyPayloadPath(path, out _, out _, out _, out _);

    internal static bool IsAllowedArchiveDirectoryPath(string path)
    {
        if (path is "manifest" or "payload" or "payload/shared" or "payload/app" or "payload/runtime")
        {
            return true;
        }
        if (TryStrip(path, SharedPayloadRoot, out _))
        {
            return true;
        }

        foreach (var role in new[] { AppHostRole, RuntimeHostRole })
        {
            var root = $"payload/{role}/";
            if (path == root + "shared" || TryStrip(path, root + "shared/", out _))
            {
                return true;
            }
            foreach (var rid in Rids)
            {
                if (path == root + rid || TryStrip(path, root + rid + "/", out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsSdkCapabilityId(string? value)
        => IsCapabilityId(value);

    public static bool IsHostCapabilityId(string? value)
        => IsCapabilityId(value);

    private static bool IsCapabilityId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxHostCapabilityLength
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

    private static bool TryStrip(string path, string prefix, out string logicalPath)
    {
        if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
        {
            logicalPath = path[prefix.Length..];
            return ArchiveRelativePath.TryParse(
                logicalPath,
                MaxLogicalPathLength,
                MaxLogicalPathDepth,
                out _,
                out _);
        }

        logicalPath = string.Empty;
        return false;
    }
}
