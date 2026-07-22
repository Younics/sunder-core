using System.IO.Compression;
using System.Text;

namespace Sunder.Package.Format;

internal static class SunderArchiveDeterministicWriter
{
    private const int CopyBufferSize = 81920;
    private static readonly DateTimeOffset DeterministicTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Task WriteAsync(
        string sourceDirectory,
        string archivePath,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            sourceDirectory,
            archivePath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });

    public static async Task WriteAsync(
        string sourceDirectory,
        string archivePath,
        SunderArchiveExtractionOptions options)
    {
        options.Validate();
        var cancellationToken = options.CancellationToken;
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        SunderArchiveFileSystem.EnsureNotLink(sourceRoot);
        var files = SunderArchiveFileSystem.EnumerateFiles(sourceRoot)
            .Select(static file => new SourceFile(
                file.Path,
                file.FullPath,
                new FileInfo(file.FullPath).Length,
                File.GetLastWriteTimeUtc(file.FullPath)))
            .OrderBy(static file => file.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
        ValidateSourceFiles(files, options);

        var fullArchivePath = Path.GetFullPath(archivePath);
        var parentPath = Path.GetDirectoryName(fullArchivePath)
                         ?? throw new InvalidOperationException($"Archive path '{fullArchivePath}' has no parent directory.");
        Directory.CreateDirectory(parentPath);
        var temporaryPath = Path.Combine(parentPath, $".{Path.GetFileName(fullArchivePath)}.write-{Guid.NewGuid():N}");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.Path.ToString(), CompressionLevel.Optimal);
                    entry.LastWriteTime = DeterministicTimestamp;
                    entry.ExternalAttributes = 0;
                    SunderArchiveFileSystem.EnsureNotLink(file.FullPath);
                    await using var input = new FileStream(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        CopyBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    SunderArchiveFileSystem.EnsureNotLink(file.FullPath);
                    if (input.Length != file.ObservedLength)
                    {
                        throw SourceChanged(file.Path);
                    }
                    await using var entryStream = entry.Open();
                    await CopyObservedBytesAsync(input, entryStream, file, cancellationToken);
                    SunderArchiveFileSystem.EnsureNotLink(file.FullPath);
                    if (File.GetLastWriteTimeUtc(file.FullPath) != file.ObservedLastWriteTimeUtc
                        || new FileInfo(file.FullPath).Length != file.ObservedLength)
                    {
                        throw SourceChanged(file.Path);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateWrittenCompressionRatios(temporaryPath, options.MaxCompressionRatio);
            File.Move(temporaryPath, fullArchivePath, overwrite: true);
        }
        catch
        {
            SunderArchiveFileSystem.TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public static void Write(string sourceDirectory, string archivePath)
        => WriteAsync(sourceDirectory, archivePath).GetAwaiter().GetResult();

    private static void ValidateSourceFiles(
        IReadOnlyList<SourceFile> files,
        SunderArchiveExtractionOptions options)
    {
        if (files.Count > options.MaxEntries)
        {
            throw new InvalidDataException($"Archive source contains {files.Count} files; the limit is {options.MaxEntries}.");
        }

        var paths = files
            .Select(file => ArchiveRelativePath.Parse(
                file.Path.ToString(),
                options.MaxPathLength,
                options.MaxPathDepth))
            .ToArray();
        ValidatePortablePaths(paths);

        long totalBytes = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var path = paths[index];
            var length = file.ObservedLength;
            if (length > options.MaxEntryUncompressedBytes)
            {
                throw new InvalidDataException($"Archive source file '{path}' exceeds the {options.MaxEntryUncompressedBytes}-byte per-entry limit.");
            }

            if (IsMetadataPath(path) && length > options.MaxMetadataJsonBytes)
            {
                throw new InvalidDataException($"Metadata file '{path}' exceeds the {options.MaxMetadataJsonBytes}-byte limit.");
            }

            if (length > options.MaxTotalUncompressedBytes - totalBytes)
            {
                throw new InvalidDataException($"Archive source exceeds the {options.MaxTotalUncompressedBytes}-byte total uncompressed limit.");
            }

            totalBytes += length;
        }
    }

    internal static void ValidatePortablePaths(IEnumerable<ArchiveRelativePath> paths)
    {
        var registry = new ArchivePathRegistry();
        foreach (var path in paths)
        {
            registry.Register(path, isDirectory: false);
        }
    }

    private static void ValidateWrittenCompressionRatios(string archivePath, double maxCompressionRatio)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > 0
                && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > maxCompressionRatio))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' exceeds the {maxCompressionRatio:0.##}:1 compression-ratio limit.");
            }
        }
    }

    private static bool IsMetadataPath(ArchiveRelativePath path)
        => path.ToString() is SunderPackageFormat.ManifestPath
            or SunderPackageFormat.ContentIndexPath
            or SunderStackFormat.ManifestPath
            or SunderStackFormat.ContentIndexPath;

    private static async Task CopyObservedBytesAsync(
        FileStream source,
        Stream destination,
        SourceFile file,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        var remaining = file.ObservedLength;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                throw SourceChanged(file.Path);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw SourceChanged(file.Path);
        }
    }

    private static InvalidDataException SourceChanged(ArchiveRelativePath path)
        => new($"Archive source file '{path}' changed after it was inspected.");

    private readonly record struct SourceFile(
        ArchiveRelativePath Path,
        string FullPath,
        long ObservedLength,
        DateTime ObservedLastWriteTimeUtc);
}
