using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackageUiSnapshotObject(string Path, string ContentHash, long Length);

internal sealed class PackageUiSnapshotObjectCache
{
    internal const int FormatRevision = 1;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _objectRoot;
    private readonly string _sourceRoot;
    private readonly string _temporaryRoot;
    private readonly string _quarantineRoot;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, object> _sourceGates = new(StringComparer.Ordinal);

    public PackageUiSnapshotObjectCache(string cacheRoot, ILogger? logger)
    {
        var root = Path.Combine(cacheRoot, "package-ui-snapshots", $"r{FormatRevision}");
        _objectRoot = Path.Combine(root, "objects");
        _sourceRoot = Path.Combine(root, "sources");
        _temporaryRoot = Path.Combine(root, "tmp");
        _quarantineRoot = Path.Combine(root, "quarantine");
        _logger = logger;
    }

    public PackageUiSnapshotObject GetOrCreate(RuntimePackageSource source)
    {
        var started = Stopwatch.GetTimestamp();
        var files = EnumerateFiles(source);
        var fingerprint = ComputeSourceFingerprint(source, files);
        lock (_sourceGates.GetOrAdd(fingerprint, static _ => new object()))
        {
            if (TryGet(fingerprint, out var cached))
            {
                _logger?.LogInformation(
                    "Package UI snapshot cache hit for {PackageId} ({ElapsedMilliseconds} ms)",
                    source.PackageId,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return cached;
            }

            var created = Create(source, files, fingerprint);
            _logger?.LogInformation(
                "Package UI snapshot cache miss for {PackageId} ({ElapsedMilliseconds} ms)",
                source.PackageId,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return created;
        }
    }

    public void CollectGarbage(IReadOnlySet<string> activeHashes)
    {
        try
        {
            if (!Directory.Exists(_sourceRoot) && !Directory.Exists(_objectRoot)) return;
            var cutoff = DateTime.UtcNow - Retention;
            var referenced = new HashSet<string>(activeHashes, StringComparer.Ordinal);
            if (Directory.Exists(_sourceRoot))
            {
                foreach (var mappingPath in Directory.EnumerateFiles(_sourceRoot, "*.json"))
                {
                    if (File.GetLastWriteTimeUtc(mappingPath) < cutoff)
                    {
                        TryDeleteFile(mappingPath);
                        continue;
                    }
                    if (TryReadMapping(mappingPath, out var mapping)) referenced.Add(mapping!.ContentHash);
                }
            }
            if (Directory.Exists(_objectRoot))
            {
                foreach (var objectPath in Directory.EnumerateFiles(_objectRoot, "*.snapshot"))
                {
                    if (!referenced.Contains(Path.GetFileNameWithoutExtension(objectPath))
                        && File.GetLastWriteTimeUtc(objectPath) < cutoff)
                    {
                        TryDeleteFile(objectPath);
                    }
                }
            }
            if (Directory.Exists(_temporaryRoot))
            {
                foreach (var temporaryPath in Directory.EnumerateFiles(_temporaryRoot))
                {
                    if (File.GetLastWriteTimeUtc(temporaryPath) < cutoff) TryDeleteFile(temporaryPath);
                }
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Package UI snapshot cache garbage collection failed");
        }
    }

    private bool TryGet(string fingerprint, out PackageUiSnapshotObject snapshotObject)
    {
        snapshotObject = null!;
        var mappingPath = GetMappingPath(fingerprint);
        if (!TryReadMapping(mappingPath, out var mapping))
        {
            if (File.Exists(mappingPath)) Quarantine(mappingPath, "mapping");
            return false;
        }
        if (mapping!.FormatRevision != FormatRevision
            || !string.Equals(mapping.SourceFingerprint, fingerprint, StringComparison.Ordinal)
            || !IsCanonicalHash(mapping.ContentHash))
        {
            Quarantine(mappingPath, "mapping");
            return false;
        }

        var objectPath = GetObjectPath(mapping.ContentHash);
        if (!File.Exists(objectPath) || new FileInfo(objectPath).Length != mapping.Length)
        {
            Quarantine(mappingPath, "mapping");
            return false;
        }
        using var stream = File.OpenRead(objectPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, mapping.ContentHash, StringComparison.Ordinal))
        {
            Quarantine(objectPath, "object");
            Quarantine(mappingPath, "mapping");
            return false;
        }

        File.SetLastWriteTimeUtc(mappingPath, DateTime.UtcNow);
        snapshotObject = new PackageUiSnapshotObject(objectPath, mapping.ContentHash, mapping.Length);
        return true;
    }

    private PackageUiSnapshotObject Create(
        RuntimePackageSource source,
        IReadOnlyList<SnapshotSourceFile> files,
        string fingerprint)
    {
        Directory.CreateDirectory(_objectRoot);
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_temporaryRoot);
        var temporaryPath = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N") + ".snapshot");
        try
        {
            string contentHash;
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hashingOutput = new HashingWriteStream(output))
            {
                using var archivedSourceHash = source.Kind == PackageSourceKind.Dev
                    ? CreateSourceFingerprintHash(source)
                    : null;
                using (var archive = new ZipArchive(hashingOutput, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in files)
                    {
                        if (archivedSourceHash is not null) AppendFileIdentity(archivedSourceHash, file);
                        var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        entry.ExternalAttributes = 0;
                        using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var destination = entry.Open();
                        if (archivedSourceHash is null)
                        {
                            input.CopyTo(destination);
                        }
                        else
                        {
                            CopyAndHash(input, destination, archivedSourceHash, file.Length);
                        }
                    }
                }
                contentHash = hashingOutput.GetHashAndReset();
                if (archivedSourceHash is not null
                    && !string.Equals(
                        Convert.ToHexString(archivedSourceHash.GetHashAndReset()).ToLowerInvariant(),
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Package source '{source.PackageId}' changed while its UI snapshot was being created.");
                }
            }

            var length = new FileInfo(temporaryPath).Length;
            if (length > PackageUiSnapshotStore.MaxUncompressedBytes)
            {
                throw new InvalidDataException($"Package UI snapshot exceeds the {PackageUiSnapshotStore.MaxUncompressedBytes} byte archive limit.");
            }
            var currentFingerprint = ComputeSourceFingerprint(source, EnumerateFiles(source));
            if (!string.Equals(currentFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Package source '{source.PackageId}' changed while its UI snapshot was being created.");
            }

            var objectPath = GetObjectPath(contentHash);
            if (File.Exists(objectPath) && !ObjectMatches(objectPath, contentHash, length))
            {
                Quarantine(objectPath, "object");
            }
            try
            {
                File.Move(temporaryPath, objectPath);
            }
            catch (IOException) when (File.Exists(objectPath))
            {
                TryDeleteFile(temporaryPath);
            }
            var mapping = new SourceMapping(FormatRevision, fingerprint, contentHash, length);
            WriteMapping(GetMappingPath(fingerprint), mapping);
            return new PackageUiSnapshotObject(objectPath, contentHash, length);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static IReadOnlyList<SnapshotSourceFile> EnumerateFiles(RuntimePackageSource source)
    {
        var files = Directory.EnumerateFiles(source.EffectiveSnapshotFolder, "*", SearchOption.AllDirectories)
            .Select(file => new SnapshotSourceFile(
                file,
                Path.GetRelativePath(source.EffectiveSnapshotFolder, file).Replace('\\', '/'),
                new FileInfo(file).Length))
            .Where(item => item.RelativePath == "sunder-package.json"
                           || item.RelativePath.StartsWith("lib/", StringComparison.Ordinal)
                           || item.RelativePath.StartsWith("assets/", StringComparison.Ordinal))
            .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0 || files.Length > PackageUiSnapshotStore.MaxFileCount)
        {
            throw new InvalidDataException($"Package UI snapshot contains an invalid number of files ({files.Length}).");
        }
        if (files.Sum(static file => file.Length) > PackageUiSnapshotStore.MaxUncompressedBytes)
        {
            throw new InvalidDataException($"Package UI snapshot exceeds the {PackageUiSnapshotStore.MaxUncompressedBytes} byte limit.");
        }
        return files;
    }

    private static string ComputeSourceFingerprint(RuntimePackageSource source, IReadOnlyList<SnapshotSourceFile> files)
    {
        if (source.Kind == PackageSourceKind.Installed
            && TryComputeInstalledFingerprint(source, out var installedFingerprint))
        {
            return installedFingerprint;
        }

        using var hash = CreateSourceFingerprintHash(source);
        foreach (var file in files)
        {
            AppendFileIdentity(hash, file);
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool TryComputeInstalledFingerprint(RuntimePackageSource source, out string fingerprint)
    {
        fingerprint = string.Empty;
        var contentIndexPath = Path.Combine(source.SourceFolder, "manifest", "content-index.json");
        var manifestPath = Path.Combine(source.SourceFolder, "manifest", "sunder-package.json");
        if (!File.Exists(contentIndexPath) || !File.Exists(manifestPath)) return false;

        using var hash = CreateSourceFingerprintHash(source);
        foreach (var path in new[] { manifestPath, contentIndexPath })
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            hash.AppendData(SHA256.HashData(stream));
        }
        fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return true;
    }

    private static IncrementalHash CreateSourceFingerprintHash(RuntimePackageSource source)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, $"r{FormatRevision}\0{source.Kind}\0{source.PackageId}\0");
        return hash;
    }

    private static void AppendFileIdentity(IncrementalHash hash, SnapshotSourceFile file)
    {
        Append(hash, file.RelativePath);
        Append(hash, "\0");
        Span<byte> length = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(length, file.Length);
        hash.AppendData(length);
    }

    private static void CopyAndHash(Stream input, Stream destination, IncrementalHash sourceHash, long expectedLength)
    {
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long length = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                length = checked(length + read);
                fileHash.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (length != expectedLength)
        {
            throw new InvalidDataException("Package source file length changed while its UI snapshot was being created.");
        }
        sourceHash.AppendData(fileHash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private string GetObjectPath(string hash) => Path.Combine(_objectRoot, hash + ".snapshot");

    private string GetMappingPath(string fingerprint) => Path.Combine(_sourceRoot, fingerprint + ".json");

    private static bool TryReadMapping(string path, out SourceMapping? mapping)
    {
        mapping = null;
        try
        {
            if (!File.Exists(path)) return false;
            mapping = JsonSerializer.Deserialize<SourceMapping>(File.ReadAllText(path));
            return mapping is not null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteMapping(string path, SourceMapping mapping)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(mapping));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void Quarantine(string path, string kind)
    {
        if (!File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(_quarantineRoot);
            File.Move(path, Path.Combine(_quarantineRoot, $"{kind}-{Path.GetFileName(path)}-{Guid.NewGuid():N}"));
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to quarantine corrupt package UI snapshot cache {Kind}", kind);
        }
    }

    private static bool IsCanonicalHash(string value)
        => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ObjectMatches(string path, string expectedHash, long expectedLength)
    {
        if (new FileInfo(path).Length != expectedLength) return false;
        using var stream = File.OpenRead(path);
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            expectedHash,
            StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record SnapshotSourceFile(string Path, string RelativePath, long Length);

    private sealed record SourceMapping(int FormatRevision, string SourceFingerprint, string ContentHash, long Length);

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            return inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
