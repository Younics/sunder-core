using System.IO.Compression;
using System.Text;

namespace Sunder.Package.Format;

public static class SunderArchive
{
    private const int CopyBufferSize = 81920;
    private static readonly DateTimeOffset DeterministicTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
            TryDeleteDirectory(temporaryPath);
            throw;
        }
    }

    public static void ExtractAtomic(
        string archivePath,
        string destinationPath,
        SunderArchiveExtractionOptions? options = null)
        => ExtractAtomicAsync(archivePath, destinationPath, options).GetAwaiter().GetResult();

    public static async Task WriteDeterministicAsync(
        string sourceDirectory,
        string archivePath,
        CancellationToken cancellationToken = default)
        => await WriteDeterministicAsync(
            sourceDirectory,
            archivePath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });

    public static async Task WriteDeterministicAsync(
        string sourceDirectory,
        string archivePath,
        SunderArchiveExtractionOptions options)
    {
        options.Validate();
        var cancellationToken = options.CancellationToken;
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        EnsureNotLink(sourceRoot);
        var files = EnumerateFiles(sourceRoot)
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
                    await using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var entryStream = entry.Open();
                    await input.CopyToAsync(entryStream, CopyBufferSize, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateWrittenCompressionRatios(temporaryPath, options.MaxCompressionRatio);
            File.Move(temporaryPath, fullArchivePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public static void WriteDeterministic(string sourceDirectory, string archivePath)
        => WriteDeterministicAsync(sourceDirectory, archivePath).GetAwaiter().GetResult();

    public static IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> EnumerateFiles(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        EnsureNotLink(root);
        var files = new List<(ArchiveRelativePath Path, string FullPath)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNotLink(entryPath);
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entryPath);
                    continue;
                }

                var relative = Path.GetRelativePath(root, entryPath).Replace(Path.DirectorySeparatorChar, '/');
                files.Add((ArchiveRelativePath.Parse(relative), entryPath));
            }
        }

        return files;
    }

    public static string ResolveFile(string rootPath, ArchiveRelativePath relativePath)
    {
        var root = Path.GetFullPath(rootPath);
        EnsureNotLink(root);
        var current = root;
        foreach (var segment in relativePath.ToString().Split('/'))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                EnsureNotLink(current);
            }
        }

        return current;
    }

    public static async Task<string> ReadMetadataJsonAsync(
        string rootPath,
        ArchiveRelativePath relativePath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var filePath = ResolveFile(rootPath, relativePath);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"Metadata file '{relativePath}' exceeds the {maxBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return StrictUtf8.GetString(bytes);
    }

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

        var registry = new PathRegistry();
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

    private static void ValidateSourceFiles(
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> files,
        SunderArchiveExtractionOptions options)
    {
        if (files.Count > options.MaxEntries)
        {
            throw new InvalidDataException($"Archive source contains {files.Count} files; the limit is {options.MaxEntries}.");
        }

        long totalBytes = 0;
        foreach (var file in files)
        {
            var path = ArchiveRelativePath.Parse(file.Path.ToString(), options.MaxPathLength, options.MaxPathDepth);
            var length = new FileInfo(file.FullPath).Length;
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
            or SunderStackFormat.ManifestPath;

    private static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Path '{path}' is a symbolic link or reparse point.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The caller still receives the extraction failure; cleanup is best effort.
        }
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
        catch
        {
            // The caller still receives the write failure; cleanup is best effort.
        }
    }

    private sealed class PathRegistry
    {
        private readonly Dictionary<string, string> _portablePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _archiveEntries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Register(ArchiveRelativePath path, bool isDirectory)
        {
            var value = path.ToString();
            if (!_archiveEntries.Add(value))
            {
                throw new InvalidDataException($"Archive contains duplicate path '{value}'.");
            }

            var segments = value.Split('/');
            var current = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                current = index == 0 ? segments[index] : $"{current}/{segments[index]}";
                RegisterPortableSpelling(current);
                var isFinal = index == segments.Length - 1;
                if (!isFinal || isDirectory)
                {
                    if (_files.Contains(current))
                    {
                        throw new InvalidDataException($"Archive path '{value}' collides with file '{current}'.");
                    }

                    _directories.Add(current);
                }
            }

            if (!isDirectory)
            {
                if (_directories.Contains(value))
                {
                    throw new InvalidDataException($"Archive file '{value}' collides with a directory.");
                }

                _files.Add(value);
            }
        }

        private void RegisterPortableSpelling(string value)
        {
            if (_portablePaths.TryGetValue(value, out var existing)
                && !string.Equals(existing, value, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Archive paths '{existing}' and '{value}' differ only by case.");
            }

            _portablePaths.TryAdd(value, value);
        }
    }
}
