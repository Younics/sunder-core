using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageSnapshotCache
{
    private const int FormatRevision = 1;
    private const string CompleteMarkerName = "complete";
    private const string MetadataFileName = "metadata.json";
    private const string ContentDirectoryName = "content";
    private const long CacheHighWaterBytes = 2L * 1024 * 1024 * 1024;
    private const long CacheLowWaterBytes = 1536L * 1024 * 1024;
    private static readonly TimeSpan ObjectRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan StagingRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(7);
    private readonly Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task> _downloadSnapshotAsync;
    private readonly string _cacheRoot;
    private readonly string _objectRoot;
    private readonly string _stagingRoot;
    private readonly string _quarantineRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hashGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _observedHashes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _materializationGate = new(4, 4);
    private readonly SemaphoreSlim _fillGate = new(2, 2);

    public AppPackageSnapshotCache(
        Func<string> getSessionFolder,
        Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task> downloadSnapshotAsync,
        string? cacheRootPath = null)
    {
        _ = getSessionFolder;
        _downloadSnapshotAsync = downloadSnapshotAsync;
        _cacheRoot = cacheRootPath ?? AppLocalState.GetPath("package-content-cache");
        _objectRoot = Path.Combine(_cacheRoot, "objects");
        _stagingRoot = Path.Combine(_cacheRoot, "staging");
        _quarantineRoot = Path.Combine(_cacheRoot, "quarantine");
    }

    public int Count => _observedHashes.Count;

    public async Task<AppPreparedPackageSource> MaterializeAsync(
        PackageUiSnapshotDescriptor snapshot,
        string generationFolder,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalHash(snapshot.ContentHash))
        {
            throw new InvalidDataException($"Package UI snapshot '{snapshot.SnapshotId}' has an invalid content hash.");
        }

        await _materializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var hash = snapshot.ContentHash.ToLowerInvariant();
        var hashGate = _hashGates.GetOrAdd(hash, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await hashGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cached = await GetOrFillAsync(snapshot, hash, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var packageFolder = Path.Combine(generationFolder, SanitizeFolderName(snapshot.PackageId));
                try
                {
                    CopyDirectory(cached.ContentPath, packageFolder, cancellationToken);
                    _observedHashes.TryAdd(hash, 0);
                    return new AppPreparedPackageSource(cached.PackageId, packageFolder);
                }
                catch
                {
                    AppPackageSourcePreparer.TryDeleteDirectory(packageFolder);
                    throw;
                }
            }
            finally
            {
                hashGate.Release();
            }
        }
        finally
        {
            _materializationGate.Release();
        }
    }

    private async Task<CachedPackageContent> GetOrFillAsync(
        PackageUiSnapshotDescriptor snapshot,
        string hash,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var objectPath = GetObjectPath(hash);
        var validation = await ValidateAsync(objectPath, hash, snapshot.PackageId, cancellationToken).ConfigureAwait(false);
        if (validation.Content is not null)
        {
            Directory.SetLastWriteTimeUtc(objectPath, DateTime.UtcNow);
            AppSessionLog.WriteInfo(
                $"Package content cache hit for '{snapshot.PackageId}' ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms).",
                visibleInDeveloperLog: false);
            return validation.Content;
        }
        if (validation.Corrupt)
        {
            Quarantine(objectPath);
        }

        await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            validation = await ValidateAsync(objectPath, hash, snapshot.PackageId, cancellationToken).ConfigureAwait(false);
            if (validation.Content is not null) return validation.Content;
            if (validation.Corrupt) Quarantine(objectPath);

            var content = await FillAsync(snapshot, hash, objectPath, cancellationToken).ConfigureAwait(false);
            AppSessionLog.WriteInfo(
                $"Package content cache miss for '{snapshot.PackageId}' ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms).",
                visibleInDeveloperLog: false);
            return content;
        }
        finally
        {
            _fillGate.Release();
        }
    }

    public void CollectGarbage(CancellationToken cancellationToken = default)
    {
        try
        {
            DeleteExpiredDirectories(_stagingRoot, StagingRetention, cancellationToken);
            DeleteExpiredDirectories(_quarantineRoot, QuarantineRetention, cancellationToken);
            if (!Directory.Exists(_objectRoot))
            {
                return;
            }

            var objects = Directory.EnumerateDirectories(_objectRoot)
                .Select(path => new CacheObject(
                    path,
                    Path.GetFileName(path),
                    Directory.GetLastWriteTimeUtc(path),
                    GetDirectoryLength(path, cancellationToken)))
                .OrderBy(static item => item.LastAccessUtc)
                .ToArray();
            var totalBytes = objects.Sum(static item => item.Length);
            var retentionCutoff = DateTime.UtcNow - ObjectRetention;
            foreach (var item in objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_observedHashes.ContainsKey(item.Hash))
                {
                    continue;
                }
                if (item.LastAccessUtc >= retentionCutoff && totalBytes <= CacheHighWaterBytes)
                {
                    continue;
                }

                AppPackageSourcePreparer.TryDeleteDirectory(item.Path);
                if (!Directory.Exists(item.Path))
                {
                    totalBytes -= item.Length;
                }
                if (totalBytes <= CacheLowWaterBytes && item.LastAccessUtc >= retentionCutoff)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                "Failed to clean the App package content cache.",
                exception,
                visibleInDeveloperLog: false);
        }
    }

    private async Task<CachedPackageContent> FillAsync(
        PackageUiSnapshotDescriptor snapshot,
        string expectedHash,
        string objectPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_objectRoot);
        Directory.CreateDirectory(_stagingRoot);
        var stagingPath = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(stagingPath, "snapshot.zip");
        var contentPath = Path.Combine(stagingPath, ContentDirectoryName);
        Directory.CreateDirectory(stagingPath);
        try
        {
            string actualHash;
            await using (var output = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var hashingOutput = new BoundedHashingWriteStream(output, AppPackageSourcePreparer.MaxSnapshotBytes))
            {
                await _downloadSnapshotAsync(snapshot, hashingOutput, cancellationToken).ConfigureAwait(false);
                await hashingOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                actualHash = hashingOutput.GetHashAndReset();
            }
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Package UI snapshot hash verification failed.");
            }

            await SunderArchive.ExtractAtomicAsync(
                archivePath,
                contentPath,
                SunderArchiveExtractionOptions.Default with
                {
                    MaxEntries = AppPackageSourcePreparer.MaxSnapshotFiles,
                    MaxEntryUncompressedBytes = AppPackageSourcePreparer.MaxSnapshotBytes,
                    MaxTotalUncompressedBytes = AppPackageSourcePreparer.MaxSnapshotBytes,
                    CancellationToken = cancellationToken,
                }).ConfigureAwait(false);
            File.Delete(archivePath);

            var manifest = AppPackageManifest.Load(Path.Combine(contentPath, "sunder-package.json"));
            if (string.IsNullOrWhiteSpace(manifest?.Id))
            {
                throw new InvalidDataException("Package UI snapshot contains an invalid app-side manifest.");
            }
            if (!string.Equals(manifest.Id, snapshot.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Package UI snapshot '{snapshot.SnapshotId}' resolved to package '{manifest.Id}'.");
            }

            var files = await IndexContentAsync(contentPath, cancellationToken).ConfigureAwait(false);
            var metadata = new CacheMetadata(FormatRevision, expectedHash, manifest.Id, files);
            await File.WriteAllTextAsync(
                Path.Combine(stagingPath, MetadataFileName),
                JsonSerializer.Serialize(metadata),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(stagingPath, CompleteMarkerName), "1", cancellationToken).ConfigureAwait(false);

            try
            {
                Directory.Move(stagingPath, objectPath);
            }
            catch (IOException) when (Directory.Exists(objectPath))
            {
                AppPackageSourcePreparer.TryDeleteDirectory(stagingPath);
                var winner = await ValidateAsync(objectPath, expectedHash, snapshot.PackageId, cancellationToken).ConfigureAwait(false);
                if (winner.Content is not null) return winner.Content;
                throw new InvalidDataException("A concurrently published package content cache object is invalid.");
            }
            return new CachedPackageContent(manifest.Id, Path.Combine(objectPath, ContentDirectoryName));
        }
        finally
        {
            AppPackageSourcePreparer.TryDeleteDirectory(stagingPath);
        }
    }

    private static async Task<CacheValidation> ValidateAsync(
        string objectPath,
        string expectedHash,
        string expectedPackageId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(objectPath)) return CacheValidation.Missing;
        try
        {
            var markerPath = Path.Combine(objectPath, CompleteMarkerName);
            var metadataPath = Path.Combine(objectPath, MetadataFileName);
            var contentPath = Path.Combine(objectPath, ContentDirectoryName);
            if (!File.Exists(markerPath)
                || !File.Exists(metadataPath)
                || !Directory.Exists(contentPath)
                || await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false) != "1")
            {
                return CacheValidation.Invalid;
            }
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
            if (metadata is null
                || metadata.FormatRevision != FormatRevision
                || !string.Equals(metadata.ContentHash, expectedHash, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(metadata.PackageId)
                || metadata.Files is null
                || metadata.Files.Count == 0)
            {
                return CacheValidation.Invalid;
            }

            var actualFiles = await IndexContentAsync(contentPath, cancellationToken).ConfigureAwait(false);
            if (!metadata.Files.SequenceEqual(actualFiles)) return CacheValidation.Invalid;
            var manifest = AppPackageManifest.Load(Path.Combine(contentPath, "sunder-package.json"));
            if (!string.Equals(manifest?.Id, metadata.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                return CacheValidation.Invalid;
            }
            if (!string.Equals(metadata.PackageId, expectedPackageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new CacheIdentityMismatchException(
                    $"Package content hash '{expectedHash}' belongs to '{metadata.PackageId}', not '{expectedPackageId}'.");
            }
            return new CacheValidation(new CachedPackageContent(metadata.PackageId, contentPath), Corrupt: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CacheIdentityMismatchException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return CacheValidation.Invalid;
        }
    }

    private static async Task<IReadOnlyList<CacheFileMetadata>> IndexContentAsync(
        string contentPath,
        CancellationToken cancellationToken)
    {
        var directories = Directory.EnumerateDirectories(contentPath, "*", SearchOption.AllDirectories).ToArray();
        if (directories.Any(static path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("Package content cache contains a linked directory.");
        }
        var paths = Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(contentPath, path), StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0 || paths.Length > AppPackageSourcePreparer.MaxSnapshotFiles)
        {
            throw new InvalidDataException("Package content cache contains an invalid number of files.");
        }

        var files = new List<CacheFileMetadata>(paths.Length);
        long totalLength = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package content cache contains a linked file.");
            }
            var length = new FileInfo(path).Length;
            totalLength = checked(totalLength + length);
            if (totalLength > AppPackageSourcePreparer.MaxSnapshotBytes)
            {
                throw new InvalidDataException("Package content cache exceeds the snapshot byte limit.");
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            files.Add(new CacheFileMetadata(Path.GetRelativePath(contentPath, path).Replace('\\', '/'), length, hash));
        }
        return files;
    }

    private void Quarantine(string objectPath)
    {
        if (!Directory.Exists(objectPath)) return;
        try
        {
            Directory.CreateDirectory(_quarantineRoot);
            Directory.Move(objectPath, Path.Combine(_quarantineRoot, $"{Path.GetFileName(objectPath)}-{Guid.NewGuid():N}"));
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to quarantine a corrupt package content cache object.", exception, visibleInDeveloperLog: false);
        }
    }

    private string GetObjectPath(string hash) => Path.Combine(_objectRoot, hash);

    private static bool IsCanonicalHash(string value)
        => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void CopyDirectory(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static string SanitizeFolderName(string packageId)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(packageId.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray());
    }

    private static void DeleteExpiredDirectories(
        string root,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - retention;
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.GetLastWriteTimeUtc(path) < cutoff)
            {
                AppPackageSourcePreparer.TryDeleteDirectory(path);
            }
        }
    }

    private static long GetDirectoryLength(string root, CancellationToken cancellationToken)
    {
        long length = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            length = checked(length + new FileInfo(path).Length);
        }
        return length;
    }

    private sealed record CachedPackageContent(string PackageId, string ContentPath);
    private sealed record CacheValidation(CachedPackageContent? Content, bool Corrupt)
    {
        public static CacheValidation Missing { get; } = new(null, Corrupt: false);
        public static CacheValidation Invalid { get; } = new(null, Corrupt: true);
    }
    private sealed record CacheMetadata(
        int FormatRevision,
        string ContentHash,
        string PackageId,
        IReadOnlyList<CacheFileMetadata> Files);
    private sealed record CacheFileMetadata(string Path, long Length, string Sha256);
    private sealed record CacheObject(string Path, string Hash, DateTime LastAccessUtc, long Length);

    private sealed class CacheIdentityMismatchException(string message) : IOException(message);

    private sealed class BoundedHashingWriteStream(Stream inner, long maxLength) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            AddLength(buffer.Length);
            _hash.AppendData(buffer);
            inner.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            AddLength(buffer.Length);
            _hash.AppendData(buffer.Span);
            return inner.WriteAsync(buffer, cancellationToken);
        }
        private void AddLength(int count)
        {
            _length = checked(_length + count);
            if (_length > maxLength) throw new InvalidDataException($"Package UI snapshot exceeds the {maxLength} byte limit.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            _hash.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
