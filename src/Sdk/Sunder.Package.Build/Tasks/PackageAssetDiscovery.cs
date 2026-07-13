namespace Sunder.Package.Build.Tasks;

internal sealed class PackageAssetDiscovery(string projectDirectory)
{
    public bool Exists(string assetPath)
    {
        var normalizedPath = assetPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(projectDirectory, normalizedPath)))
        {
            return true;
        }

        const string assetsPrefix = "assets/";
        var forwardPath = NormalizePath(assetPath);
        if (!forwardPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceAssetPath = Path.Combine(
            projectDirectory,
            "Assets",
            forwardPath[assetsPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(sourceAssetPath);
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/');
}
