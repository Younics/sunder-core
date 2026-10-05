using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeSourceMaterializer
{
    private const int MaxSourceEntries = SunderExecutionRuntimeFormat.MaxContentIndexEntries - 1;

    public static async Task<IReadOnlyDictionary<string, string>> MaterializeAsync(
        string lockKind,
        ExecutionRuntimeLockEntry entry,
        string sourceArchivePath,
        string workingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKind);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingRoot);
        if (entry.Source is null || entry.Recipe is null || entry.Profile is null || entry.License is null)
        {
            throw new InvalidDataException("A validated execution-runtime lock entry is required.");
        }

        Directory.CreateDirectory(workingRoot);
        var snapshotPath = Path.Combine(workingRoot, "source.snapshot");
        await SnapshotAndVerifyAsync(sourceArchivePath, snapshotPath, entry.Source.Sha256!, cancellationToken);

        var extractedRoot = Path.Combine(workingRoot, "source");
        Directory.CreateDirectory(extractedRoot);
        var registry = new SourceFileRegistry(
            extractedRoot,
            entry.Source.SourceRoot!,
            entry.Recipe.ExcludedSourcePaths!);
        switch (entry.Source.ArchiveFormat)
        {
            case ExecutionRuntimeLockPolicy.FixtureArchiveFormat
                when lockKind == ExecutionRuntimeLockPolicy.TestFixtureKind:
                await MaterializeFixtureAsync(snapshotPath, registry, cancellationToken);
                break;
            case ExecutionRuntimeLockPolicy.ZipArchiveFormat:
                await MaterializeZipAsync(snapshotPath, registry, cancellationToken);
                break;
            case ExecutionRuntimeLockPolicy.TarGzipArchiveFormat:
                await MaterializeTarGzipAsync(snapshotPath, registry, cancellationToken);
                break;
            default:
                throw new InvalidDataException(
                    $"Source archive format '{entry.Source.ArchiveFormat}' is not enabled for lock kind '{lockKind}'.");
        }

        var sourceFiles = registry.Complete();
        if (!sourceFiles.TryGetValue(entry.Recipe.LauncherSource!, out var launcherPath)
            || new FileInfo(launcherPath).Length <= 0)
        {
            throw new InvalidDataException(
                $"Locked source does not contain the non-empty launcher '{entry.Recipe.LauncherSource}'.");
        }

        foreach (var licenseFile in entry.License.Files!)
        {
            if (licenseFile is null
                || !sourceFiles.TryGetValue(licenseFile.Path!, out var licensePath))
            {
                throw new InvalidDataException(
                    $"Locked source does not contain license file '{licenseFile?.Path}'.");
            }
            var actualLicenseHash = await HashFileAsync(licensePath, cancellationToken);
            if (!string.Equals(actualLicenseHash, licenseFile.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Locked source license file '{licenseFile.Path}' does not match SHA-256 '{licenseFile.Sha256}'.");
            }
        }

        var payloadFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sourcePath, materializedPath) in sourceFiles)
        {
            payloadFiles.Add($"{entry.Recipe.PayloadRoot}/{sourcePath}", materializedPath);
        }
        if (!payloadFiles.ContainsKey(entry.Profile.LaunchExecutable!))
        {
            throw new InvalidDataException(
                $"Locked launch executable '{entry.Profile.LaunchExecutable}' is not produced by the source-tree recipe.");
        }
        return payloadFiles;
    }

    public static async Task ValidateAsync(
        string lockKind,
        ExecutionRuntimeLockEntry entry,
        string sourceArchivePath,
        CancellationToken cancellationToken = default)
    {
        var workingRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build",
            "validate-source",
            Guid.NewGuid().ToString("N"));
        try
        {
            await MaterializeAsync(lockKind, entry, sourceArchivePath, workingRoot, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task SnapshotAndVerifyAsync(
        string sourcePath,
        string snapshotPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Locked source archive '{sourcePath}' was not found.", sourcePath);
        }
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Locked source archive '{sourcePath}' must not be a link.");
        }

        var observed = new FileInfo(sourcePath);
        if (observed.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes)
        {
            throw new InvalidDataException(
                $"Locked source archive must contain between 1 and {SunderExecutionRuntimeFormat.MaxArchiveBytes} bytes.");
        }
        var observedLength = observed.Length;
        var observedWriteTime = observed.LastWriteTimeUtc;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         snapshotPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            long copied = 0;
            while (copied < observedLength)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, observedLength - copied)),
                    cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException("Locked source archive changed while it was being snapshotted.");
                }
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
            {
                throw new InvalidDataException("Locked source archive changed while it was being snapshotted.");
            }
        }

        observed.Refresh();
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0
            || observed.Length != observedLength
            || observed.LastWriteTimeUtc != observedWriteTime)
        {
            throw new InvalidDataException("Locked source archive changed while it was being snapshotted.");
        }

        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualSha256),
                Encoding.ASCII.GetBytes(expectedSha256)))
        {
            throw new InvalidDataException(
                $"Locked source archive SHA-256 mismatch: expected '{expectedSha256}', observed '{actualSha256}'.");
        }
    }

    private static async Task MaterializeFixtureAsync(
        string sourcePath,
        SourceFileRegistry registry,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(sourcePath).Length > ExecutionRuntimeLockPolicy.MaxLockBytes)
        {
            throw new InvalidDataException(
                $"Execution-runtime fixture source exceeds {ExecutionRuntimeLockPolicy.MaxLockBytes} bytes.");
        }
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var fixture = StrictJson.Deserialize<FixtureSourceArchive>(bytes, "Execution-runtime fixture source");
        if (fixture.SchemaVersion != 1 || fixture.Files is null || fixture.Files.Count == 0)
        {
            throw new InvalidDataException("Execution-runtime fixture source must contain a non-empty V1 file list.");
        }
        foreach (var file in fixture.Files)
        {
            if (file is null || file.Text is null)
            {
                throw new InvalidDataException("Execution-runtime fixture source contains an incomplete file record.");
            }
            if (registry.ShouldExclude(file.Path))
            {
                continue;
            }
            var content = Encoding.UTF8.GetBytes(file.Text);
            await using var stream = new MemoryStream(content, writable: false);
            await registry.AddAsync(file.Path, stream, content.Length, cancellationToken);
        }
    }

    private static async Task MaterializeZipAsync(
        string sourcePath,
        SourceFileRegistry registry,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.Entries.Count > SunderExecutionRuntimeFormat.MaxArchiveEntries)
        {
            throw new InvalidDataException("Locked ZIP source contains too many entries.");
        }
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registry.ShouldExclude(entry.FullName))
            {
                continue;
            }
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var unixMode = (entry.ExternalAttributes >> 16) & 0xffff;
            var fileType = unixMode & 0xf000;
            if (fileType != 0 && fileType != 0x8000)
            {
                throw new InvalidDataException($"Locked ZIP source entry '{entry.FullName}' is not a regular file.");
            }
            if (entry.Length > 0
                && (entry.CompressedLength == 0
                    || entry.Length / (double)entry.CompressedLength > SunderExecutionRuntimeFormat.MaxCompressionRatio))
            {
                throw new InvalidDataException($"Locked ZIP source entry '{entry.FullName}' exceeds the compression-ratio limit.");
            }

            await using var stream = entry.Open();
            await registry.AddAsync(entry.FullName, stream, entry.Length, cancellationToken);
        }
    }

    private static async Task MaterializeTarGzipAsync(
        string sourcePath,
        SourceFileRegistry registry,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);
        var entryCount = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            entryCount++;
            if (entryCount > SunderExecutionRuntimeFormat.MaxArchiveEntries)
            {
                throw new InvalidDataException("Locked tar.gz source contains too many entries.");
            }
            if (registry.ShouldExclude(entry.Name))
            {
                continue;
            }
            if (entry.EntryType == TarEntryType.Directory)
            {
                continue;
            }
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile
                || entry.DataStream is null)
            {
                throw new InvalidDataException($"Locked tar.gz source entry '{entry.Name}' is not a regular file.");
            }

            await registry.AddAsync(entry.Name, entry.DataStream, entry.Length, cancellationToken);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class SourceFileRegistry(
        string outputRoot,
        string sourceRoot,
        IReadOnlyList<string?> excludedSourcePaths)
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _portablePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly string[] _excludedSourcePaths = excludedSourcePaths
            .Select(static path => path ?? throw new InvalidDataException("Excluded source paths must not contain null."))
            .ToArray();
        private readonly HashSet<string> _observedExclusions = new(StringComparer.Ordinal);
        private long _totalBytes;

        public bool ShouldExclude(string? archivePath)
        {
            var normalized = NormalizeArchivePath(archivePath);
            if (normalized is null)
            {
                return false;
            }
            var relativePath = GetRelativePath(archivePath);
            foreach (var excludedPath in _excludedSourcePaths)
            {
                if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal)
                    || relativePath.StartsWith(excludedPath + "/", StringComparison.Ordinal))
                {
                    _observedExclusions.Add(excludedPath);
                    return true;
                }
            }
            return false;
        }

        public async Task AddAsync(
            string? archivePath,
            Stream source,
            long declaredLength,
            CancellationToken cancellationToken)
        {
            var relativePath = GetRelativePath(archivePath);
            if (_files.ContainsKey(relativePath))
            {
                throw new InvalidDataException($"Locked source contains duplicate regular file '{relativePath}'.");
            }
            if (_portablePaths.TryGetValue(relativePath, out var existing))
            {
                throw new InvalidDataException(
                    $"Locked source files '{existing}' and '{relativePath}' differ only by case.");
            }
            if (_files.Count >= MaxSourceEntries)
            {
                throw new InvalidDataException("Locked source contains too many regular files for the execution-runtime format.");
            }
            if (declaredLength < 0
                || declaredLength > SunderExecutionRuntimeFormat.MaxEntryUncompressedBytes
                || declaredLength > SunderExecutionRuntimeFormat.MaxTotalUncompressedBytes - _totalBytes)
            {
                throw new InvalidDataException($"Locked source file '{relativePath}' exceeds execution-runtime size limits.");
            }

            var destinationPath = Path.Combine([outputRoot, .. relativePath.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            var remaining = declaredLength;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException($"Locked source file '{relativePath}' ended before its declared size.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
            {
                throw new InvalidDataException($"Locked source file '{relativePath}' exceeds its declared size.");
            }

            _files.Add(relativePath, destinationPath);
            _portablePaths.Add(relativePath, relativePath);
            _totalBytes += declaredLength;
        }

        public IReadOnlyDictionary<string, string> Complete()
        {
            if (_files.Count == 0)
            {
                throw new InvalidDataException("Locked source contains no regular files below its exact source root.");
            }
            var missingExclusions = _excludedSourcePaths
                .Where(path => !_observedExclusions.Contains(path))
                .ToArray();
            if (missingExclusions.Length > 0)
            {
                throw new InvalidDataException(
                    "Locked source did not contain exact excluded recipe path(s): "
                    + string.Join(", ", missingExclusions));
            }
            return new SortedDictionary<string, string>(_files, StringComparer.Ordinal);
        }

        private string GetRelativePath(string? archivePath)
        {
            var normalized = NormalizeArchivePath(archivePath)
                             ?? throw new InvalidDataException(
                                 $"Locked source regular file '{archivePath}' cannot be the source root directory.");
            if (string.Equals(sourceRoot, ".", StringComparison.Ordinal))
            {
                return normalized;
            }
            if (!normalized.StartsWith(sourceRoot + "/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Locked source entry '{archivePath}' is unsafe or outside exact source root '{sourceRoot}'.");
            }
            var relativePath = normalized[(sourceRoot.Length + 1)..];
            if (!ExecutionRuntimeLockPolicy.TryNormalizeRelativePath(relativePath, out relativePath))
            {
                throw new InvalidDataException($"Locked source entry '{archivePath}' has an unsafe relative path.");
            }
            return relativePath;
        }

        private string? NormalizeArchivePath(string? archivePath)
        {
            var pathWithoutDirectorySuffix = archivePath?.EndsWith("/", StringComparison.Ordinal) == true
                ? archivePath[..^1]
                : archivePath;
            if (pathWithoutDirectorySuffix?.StartsWith("./", StringComparison.Ordinal) == true)
            {
                pathWithoutDirectorySuffix = pathWithoutDirectorySuffix[2..];
            }
            if (string.IsNullOrEmpty(pathWithoutDirectorySuffix)
                || string.Equals(pathWithoutDirectorySuffix, ".", StringComparison.Ordinal))
            {
                if (string.Equals(sourceRoot, ".", StringComparison.Ordinal)
                    || string.Equals(pathWithoutDirectorySuffix, sourceRoot, StringComparison.Ordinal))
                {
                    return null;
                }
                throw new InvalidDataException(
                    $"Locked source entry '{archivePath}' is outside exact source root '{sourceRoot}'.");
            }
            if (!ExecutionRuntimeLockPolicy.TryNormalizeRelativePath(pathWithoutDirectorySuffix, out var normalized))
            {
                throw new InvalidDataException($"Locked source entry '{archivePath}' has an unsafe path.");
            }
            if (string.Equals(normalized, sourceRoot, StringComparison.Ordinal))
            {
                return null;
            }
            return normalized;
        }
    }

    private sealed record FixtureSourceArchive
    {
        [JsonPropertyName("schemaVersion"), JsonRequired]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("files"), JsonRequired]
        public IReadOnlyList<FixtureSourceFile?>? Files { get; init; }
    }

    private sealed record FixtureSourceFile
    {
        [JsonPropertyName("path"), JsonRequired]
        public string? Path { get; init; }

        [JsonPropertyName("text"), JsonRequired]
        public string? Text { get; init; }
    }
}
