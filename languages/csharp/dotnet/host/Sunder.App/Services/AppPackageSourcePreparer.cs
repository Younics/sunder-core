using System.Security.Cryptography;
using Sunder.Package.Format;
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
        var archivePath = Path.Combine(
            shadowRoot,
            $".{sequence:D4}-{SanitizeFolderName(snapshot.PackageId)}-{Guid.NewGuid():N}.snapshot.zip");
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

            await SunderArchive.ExtractAtomicAsync(
                archivePath,
                shadowFolder,
                SunderArchiveExtractionOptions.Default with
                {
                    MaxEntries = MaxSnapshotFiles,
                    MaxEntryUncompressedBytes = MaxSnapshotBytes,
                    MaxTotalUncompressedBytes = MaxSnapshotBytes,
                    CancellationToken = cancellationToken,
                }).ConfigureAwait(false);
            File.Delete(archivePath);
            var manifest = AppPackageManifestReader.Read(shadowFolder);
            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                return null;
            }

            prepared = true;
            return new AppPreparedPackageSource(manifest.Id, shadowFolder, manifest);
        }
        finally
        {
            TryDeleteFile(archivePath);
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

    private static string SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "package";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(folderName.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray());
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to delete an app package snapshot archive.", ex);
        }
    }
}
