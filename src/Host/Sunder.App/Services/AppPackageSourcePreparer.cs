using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class AppPackageSourcePreparer(string? sessionFolder)
{
    private int _shadowFolderSequence;

    public AppPreparedPackageSource? Prepare(PackageSourceDescriptor source)
    {
        if (string.IsNullOrWhiteSpace(source.Folder) || !Directory.Exists(source.Folder))
        {
            return null;
        }

        var shadowRoot = sessionFolder ?? AppPackageSessionDirectories.CreateSessionFolder();
        Directory.CreateDirectory(shadowRoot);
        var sequence = Interlocked.Increment(ref _shadowFolderSequence);
        var shadowFolder = Path.Combine(shadowRoot, $"{sequence:D4}-{SanitizeFolderName(source.PackageId)}");
        Directory.CreateDirectory(shadowFolder);
        var prepared = false;
        try
        {
            switch (source.Kind)
            {
                case PackageSourceKind.Dev:
                    CopyDirectory(source.Folder, shadowFolder);
                    break;
                case PackageSourceKind.Installed:
                    PrepareInstalledPackageSource(source.Folder, shadowFolder);
                    break;
                default:
                    return null;
            }

            var manifestPath = Path.Combine(shadowFolder, "sunder-package.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifest = AppPackageManifest.Load(manifestPath);
            if (string.IsNullOrWhiteSpace(manifest?.Id))
            {
                return null;
            }

            prepared = true;
            return new AppPreparedPackageSource(manifest.Id, shadowFolder);
        }
        finally
        {
            if (!prepared)
            {
                TryDeleteDirectory(shadowFolder);
            }
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to delete an app package session folder.", ex);
        }
    }

    private static void PrepareInstalledPackageSource(string sourceFolder, string shadowFolder)
    {
        var manifestPath = ResolveInstalledPackageManifestPath(sourceFolder);
        if (File.Exists(manifestPath))
        {
            File.Copy(manifestPath, Path.Combine(shadowFolder, "sunder-package.json"), overwrite: true);
        }

        var libraryFolder = ResolveInstalledPackageFolder(sourceFolder, "lib");
        if (Directory.Exists(libraryFolder))
        {
            CopyDirectory(libraryFolder, Path.Combine(shadowFolder, "lib"));
        }

        var assetFolder = ResolveInstalledPackageFolder(sourceFolder, "assets");
        if (Directory.Exists(assetFolder))
        {
            CopyDirectory(assetFolder, Path.Combine(shadowFolder, "assets"));
        }
    }

    private static string ResolveInstalledPackageManifestPath(string sourceFolder)
    {
        var packagedManifestPath = Path.Combine(sourceFolder, "manifest", "sunder-package.json");
        return File.Exists(packagedManifestPath)
            ? packagedManifestPath
            : Path.Combine(sourceFolder, "sunder-package.json");
    }

    private static string ResolveInstalledPackageFolder(string sourceFolder, string folderName)
    {
        var packagedFolder = Path.Combine(sourceFolder, "payload", folderName);
        return Directory.Exists(packagedFolder)
            ? packagedFolder
            : Path.Combine(sourceFolder, folderName);
    }

    private static void CopyDirectory(string sourceFolder, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, sourceFile);
            var destinationPath = Path.Combine(destinationFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourceFile, destinationPath, overwrite: true);
        }
    }

    private static string SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "package";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(folderName.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray());
    }
}
