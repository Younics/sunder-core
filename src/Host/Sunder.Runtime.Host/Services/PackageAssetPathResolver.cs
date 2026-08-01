namespace Sunder.Runtime.Host.Services;

internal static class PackageAssetPathResolver
{
    public static string? TryResolveDevAssetPath(string packageRootPath, string assetPath)
        => TryResolveAssetPath(Path.Combine(packageRootPath, "payload", "shared"), assetPath);

    public static string? TryResolveInstalledAssetPath(string installPath, string assetPath)
        => TryResolveAssetPath(Path.Combine(installPath, "payload", "shared"), assetPath);

    private static string? TryResolveAssetPath(string assetRootPath, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        var normalized = assetPath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            return null;
        }

        if (segments.Length == 0)
        {
            return null;
        }

        var assetRoot = Path.GetFullPath(assetRootPath);
        var candidatePath = Path.GetFullPath(Path.Combine([assetRoot, .. segments]));
        if (!IsUnderRoot(assetRoot, candidatePath) || !File.Exists(candidatePath))
        {
            return null;
        }

        return candidatePath;
    }

    private static bool IsUnderRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
