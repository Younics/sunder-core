using System.IO.Compression;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageSourcePreparer(string? sessionFolder)
{
    internal const long MaxSnapshotBytes = 256L * 1024 * 1024;
    internal const int MaxSnapshotFiles = 20_000;
    private int _shadowFolderSequence;

    public async Task<AppPreparedPackageSource?> PrepareAsync(
        PackageUiSnapshotDescriptor snapshot,
        Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task> downloadSnapshotAsync,
        CancellationToken cancellationToken)
    {
        var shadowRoot = sessionFolder ?? AppPackageSessionDirectories.CreateSessionFolder();
        Directory.CreateDirectory(shadowRoot);
        var sequence = Interlocked.Increment(ref _shadowFolderSequence);
        var shadowFolder = Path.Combine(shadowRoot, $"{sequence:D4}-{SanitizeFolderName(snapshot.PackageId)}");
        Directory.CreateDirectory(shadowFolder);
        var archivePath = Path.Combine(shadowFolder, ".snapshot.zip");
        var prepared = false;
        try
        {
            await using (var destination = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await downloadSnapshotAsync(snapshot, destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                if (destination.Length > MaxSnapshotBytes)
                {
                    throw new InvalidDataException($"Package UI snapshot exceeds the {MaxSnapshotBytes} byte limit.");
                }
            }

            await using (var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(actualHash, snapshot.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Package UI snapshot hash verification failed.");
                }
            }

            ExtractSnapshot(archivePath, shadowFolder, cancellationToken);
            File.Delete(archivePath);
            var manifestPath = Path.Combine(shadowFolder, "sunder-package.json");
            var manifest = File.Exists(manifestPath) ? AppPackageManifest.Load(manifestPath) : null;
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

    private static void ExtractSnapshot(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxSnapshotFiles)
        {
            throw new InvalidDataException($"Package UI snapshot exceeds the {MaxSnapshotFiles} file limit.");
        }

        long totalLength = 0;
        var root = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalLength += entry.Length;
            if (totalLength > MaxSnapshotBytes)
            {
                throw new InvalidDataException($"Package UI snapshot exceeds the {MaxSnapshotBytes} uncompressed byte limit.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Package UI snapshot entry '{entry.FullName}' escapes the destination root.");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: false);
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
