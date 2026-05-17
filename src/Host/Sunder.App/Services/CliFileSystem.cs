using System.Security.Cryptography;

namespace Sunder.App.Services;

internal static class CliFileSystem
{
    public static bool SyncDirectoryIfNeeded(
        string sourceDirectory,
        string destinationDirectory,
        StringComparer relativePathComparer)
    {
        var changed = false;
        var expectedRelativePaths = new HashSet<string>(relativePathComparer);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            expectedRelativePaths.Add(relativePath);

            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

            if (File.Exists(destinationPath) && FilesHaveSameHash(sourcePath, destinationPath))
            {
                continue;
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
            changed = true;
        }

        if (Directory.Exists(destinationDirectory))
        {
            foreach (var destinationPath in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(destinationDirectory, destinationPath);
                if (expectedRelativePaths.Contains(relativePath))
                {
                    continue;
                }

                DeleteFileIfExists(destinationPath);
                changed = true;
            }

            DeleteEmptySubdirectories(destinationDirectory);
        }

        return changed;
    }

    public static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Uninstall/repair should best-effort clean stale CLI files.
        }
    }

    public static void DeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Leaving an empty directory behind is preferable to failing app startup.
        }
    }

    public static void DeleteDirectoryIfExists(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Uninstall/repair should best-effort clean stale CLI files.
        }
    }

    private static bool FilesHaveSameHash(string firstPath, string secondPath)
        => string.Equals(ComputeSha256(firstPath), ComputeSha256(secondPath), StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void DeleteEmptySubdirectories(string rootDirectory)
    {
        foreach (var directory in Directory
            .EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length))
        {
            DeleteDirectoryIfEmpty(directory);
        }
    }
}
