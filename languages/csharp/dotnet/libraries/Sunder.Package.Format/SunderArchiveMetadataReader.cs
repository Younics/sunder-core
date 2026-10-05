using System.Text;

namespace Sunder.Package.Format;

internal static class SunderArchiveMetadataReader
{
    private const int CopyBufferSize = 81920;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string> ReadJsonAsync(
        string rootPath,
        ArchiveRelativePath relativePath,
        int maxBytes,
        CancellationToken cancellationToken,
        ISunderPackageValidationIoObserver? ioObserver = null)
    {
        var filePath = SunderArchiveFileSystem.ResolveFile(rootPath, relativePath);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"Metadata file '{relativePath}' exceeds the {maxBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var startPosition = stream.Position;
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return StrictUtf8.GetString(bytes);
        }
        finally
        {
            try
            {
                ioObserver.TryRecord(stream.Position - startPosition, bytesHashed: 0);
            }
            catch
            {
                // Diagnostics must not affect metadata reads.
            }
        }
    }
}
