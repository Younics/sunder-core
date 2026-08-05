using System.IO.Compression;
using System.Text;

namespace Sunder.Package.Format;

internal static class SunderArchiveExtractor
{
    private const int CopyBufferSize = 81920;

    public static async Task ExtractAtomicAsync(
        string archivePath,
        string destinationPath,
        SunderArchiveExtractionOptions? options = null)
    {
        options ??= SunderArchiveExtractionOptions.Default;
        options.Validate();
        var cancellationToken = options.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (Directory.Exists(fullDestinationPath) || File.Exists(fullDestinationPath))
        {
            throw new IOException($"Archive destination '{fullDestinationPath}' already exists.");
        }

        var parentPath = Path.GetDirectoryName(fullDestinationPath)
                         ?? throw new InvalidOperationException($"Archive destination '{fullDestinationPath}' has no parent directory.");
        Directory.CreateDirectory(parentPath);
        var temporaryPath = Path.Combine(parentPath, $".{Path.GetFileName(fullDestinationPath)}.extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);

        try
        {
            await ExtractCoreAsync(archivePath, temporaryPath, options);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryPath, fullDestinationPath);
        }
        catch
        {
            SunderArchiveFileSystem.TryDeleteDirectory(temporaryPath);
            throw;
        }
    }

    public static void ExtractAtomic(
        string archivePath,
        string destinationPath,
        SunderArchiveExtractionOptions? options = null)
        => ExtractAtomicAsync(archivePath, destinationPath, options).GetAwaiter().GetResult();

    private static async Task ExtractCoreAsync(
        string archivePath,
        string destinationPath,
        SunderArchiveExtractionOptions options)
    {
        var cancellationToken = options.CancellationToken;
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: Encoding.UTF8);
        if (archive.Entries.Count > options.MaxEntries)
        {
            throw new InvalidDataException($"Archive contains {archive.Entries.Count} entries; the limit is {options.MaxEntries}.");
        }

        var registry = new ArchivePathRegistry();
        long declaredTotal = 0;
        long observedTotal = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinkEntry(entry);
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory && (!string.IsNullOrEmpty(entry.Name) || entry.Length != 0))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' has ambiguous directory metadata.");
            }

            var pathText = isDirectory ? entry.FullName[..^1] : entry.FullName;
            var relativePath = ArchiveRelativePath.Parse(pathText, options.MaxPathLength, options.MaxPathDepth);
            registry.Register(relativePath, isDirectory);

            if (entry.Length < 0 || entry.Length > options.MaxEntryUncompressedBytes)
            {
                throw new InvalidDataException($"Archive entry '{relativePath}' exceeds the {options.MaxEntryUncompressedBytes}-byte per-entry limit.");
            }

            if (entry.Length > options.MaxTotalUncompressedBytes - declaredTotal)
            {
                throw new InvalidDataException($"Archive exceeds the {options.MaxTotalUncompressedBytes}-byte total uncompressed limit.");
            }

            declaredTotal += entry.Length;
            if (entry.Length > 0
                && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > options.MaxCompressionRatio))
            {
                throw new InvalidDataException($"Archive entry '{relativePath}' exceeds the {options.MaxCompressionRatio:0.##}:1 compression-ratio limit.");
            }

            var destination = relativePath.ToPlatformPath(destinationPath);
            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var entryStream = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[CopyBufferSize];
            long entryBytes = 0;
            while (true)
            {
                var read = await entryStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > options.MaxEntryUncompressedBytes - entryBytes
                    || read > options.MaxTotalUncompressedBytes - observedTotal)
                {
                    throw new InvalidDataException($"Archive entry '{relativePath}' exceeded extraction byte limits while streaming.");
                }

                entryBytes += read;
                observedTotal += read;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException($"Archive entry '{relativePath}' length metadata does not match its content.");
            }
        }
    }

    private static void RejectLinkEntry(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xffff;
        var unixFileType = unixMode & 0xf000;
        if (unixFileType == 0xa000
            || (unixFileType != 0 && unixFileType is not 0x4000 and not 0x8000)
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Archive entry '{entry.FullName}' is a link or unsupported special file.");
        }
    }

}
