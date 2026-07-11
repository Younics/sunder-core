using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal static class PackageContentLayoutBuilder
{
    public static void Build(string devPackagePath, string stagingPath, string manifestPath)
    {
        var manifestOutputPath = Path.Combine(stagingPath, "manifest", "sunder-package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestOutputPath)!);
        File.Copy(manifestPath, manifestOutputPath, overwrite: true);
        CopyDirectoryIfExists(Path.Combine(devPackagePath, "lib"), Path.Combine(stagingPath, "payload", "lib"));
        CopyDirectoryIfExists(Path.Combine(devPackagePath, "assets"), Path.Combine(stagingPath, "payload", "assets"));
    }

    private static void CopyDirectoryIfExists(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        foreach (var sourceFile in SunderArchive.EnumerateFiles(sourcePath))
        {
            var destinationFile = sourceFile.Path.ToPlatformPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile.FullPath, destinationFile, overwrite: true);
        }
    }
}
