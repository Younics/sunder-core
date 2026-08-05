using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackageUiSnapshotObject(string Path, string ContentHash, long Length);

internal sealed class PackageUiSnapshotObjectCache
{
    internal const int FormatRevision = 2;
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

    public PackageUiSnapshotObject GetOrCreate(RuntimePackageTargetSource targetSource)
    {
        var source = targetSource.Source;
        var started = Stopwatch.GetTimestamp();
        var files = EnumerateFiles(targetSource);
        var fingerprint = ComputeSourceFingerprint(targetSource, files);
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

            var created = Create(targetSource, files, fingerprint);
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
        RuntimePackageTargetSource targetSource,
        IReadOnlyList<SnapshotSourceFile> files,
        string fingerprint)
    {
        var source = targetSource.Source;
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
                using (var archive = new ZipArchive(hashingOutput, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in files)
                    {
                        var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        entry.ExternalAttributes = 0;
                        using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var destination = entry.Open();
                        CopyAndValidate(input, destination, file);
                    }
                }
                contentHash = hashingOutput.GetHashAndReset();
            }

            var length = new FileInfo(temporaryPath).Length;
            if (length > PackageUiSnapshotStore.MaxUncompressedBytes)
            {
                throw new InvalidDataException($"Package UI snapshot exceeds the {PackageUiSnapshotStore.MaxUncompressedBytes} byte archive limit.");
            }
            var currentFingerprint = ComputeSourceFingerprint(targetSource, EnumerateFiles(targetSource));
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

    private static IReadOnlyList<SnapshotSourceFile> EnumerateFiles(RuntimePackageTargetSource targetSource)
    {
        var source = targetSource.Source;
        var manifest = source.Manifest
            ?? throw new InvalidDataException($"Package source '{source.PackageId}' is missing its strict manifest.");
        var contentIndex = source.ContentIndex
            ?? throw new InvalidDataException($"Package source '{source.PackageId}' is missing its strict content index.");
        var indexed = (contentIndex.Files ?? throw new InvalidDataException("Package content index is missing files."))
            .ToDictionary(
                static entry => entry?.Path ?? throw new InvalidDataException("Package content index contains a null entry."),
                static entry => entry!,
                StringComparer.Ordinal);
        var files = new Dictionary<string, SnapshotSourceFile>(StringComparer.Ordinal);
        Add(SunderPackageFormat.ManifestPath, SunderPackageFormat.ManifestPath);
        foreach (var projection in SunderPackageTargetResolver
                     .CreateProjectionPlan(manifest, contentIndex, targetSource.TargetKey)
                     .Files)
        {
            Add(projection.PhysicalPath.ToString(), projection.LogicalPath.ToString());
        }
        var ordered = files.Values.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Length > PackageUiSnapshotStore.MaxFileCount)
        {
            throw new InvalidDataException($"Package UI snapshot contains an invalid number of files ({ordered.Length}).");
        }
        if (ordered.Sum(static file => file.Length) > PackageUiSnapshotStore.MaxUncompressedBytes)
        {
            throw new InvalidDataException($"Package UI snapshot exceeds the {PackageUiSnapshotStore.MaxUncompressedBytes} byte limit.");
        }
        return ordered;

        void Add(string physicalPath, string snapshotPath)
        {
            if (!indexed.TryGetValue(physicalPath, out var entry))
            {
                throw new InvalidDataException($"Package UI snapshot references unindexed file '{physicalPath}'.");
            }
            if (!files.TryAdd(snapshotPath, new SnapshotSourceFile(
                    ArchiveRelativePath.Parse(
                        physicalPath,
                        SunderPackageFormat.MaxArchivePathLength,
                        SunderPackageFormat.MaxArchivePathDepth)
                        .ToPlatformPath(source.EffectiveSnapshotFolder),
                    snapshotPath,
                    entry.Size,
                    entry.Sha256!)))
            {
                throw new InvalidDataException(
                    $"Package target '{targetSource.TargetKey}' snapshot projection collides at '{snapshotPath}'.");
            }
        }
    }

    private static string ComputeSourceFingerprint(RuntimePackageTargetSource targetSource, IReadOnlyList<SnapshotSourceFile> files)
    {
        using var hash = CreateSourceFingerprintHash(targetSource);
        foreach (var file in files)
        {
            AppendFileIdentity(hash, file);
            Append(hash, file.Sha256);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IncrementalHash CreateSourceFingerprintHash(RuntimePackageTargetSource targetSource)
    {
        var source = targetSource.Source;
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(
            hash,
            $"r{FormatRevision}\0{source.Kind}\0{source.PackageId}\0{source.ContentIdentity}\0{targetSource.TargetKey.Role}\0{targetSource.TargetKey.Rid}\0{targetSource.Target.Kind}\0{targetSource.Target.EntryPoint}\0{targetSource.Target.TargetFramework}\0{targetSource.Target.SdkVersion}\0");
        foreach (var capability in targetSource.Target.RequiredHostCapabilities ?? [])
        {
            Append(hash, capability ?? string.Empty);
            Append(hash, "\0");
        }
        foreach (var view in targetSource.Target.Views ?? [])
        {
            if (view is null)
            {
                Append(hash, "<null>\0");
                continue;
            }
            Append(hash, view.ViewId ?? string.Empty);
            Append(hash, "\0");
            Append(hash, view.DisplayName ?? string.Empty);
            Append(hash, "\0");
            Append(hash, view.Route ?? string.Empty);
            Append(hash, "\0");
            Append(hash, view.Icon ?? string.Empty);
            Append(hash, "\0");
            Append(hash, view.DefaultPlacement ?? string.Empty);
            Append(hash, view.ShowInHotbar?.ToString() ?? string.Empty);
            Append(hash, "\0");
        }
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

    private static void CopyAndValidate(Stream input, Stream destination, SnapshotSourceFile file)
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
        var actualHash = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
        if (length != file.Length || !string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Package source file '{file.RelativePath}' changed or failed hash validation while its UI snapshot was being created.");
        }
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

    private sealed record SnapshotSourceFile(string Path, string RelativePath, long Length, string Sha256);

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
