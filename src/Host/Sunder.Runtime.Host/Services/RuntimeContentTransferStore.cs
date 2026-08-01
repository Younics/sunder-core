using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal enum RuntimeUploadKind
{
    Package,
    Stack,
    StackMedia,
}

internal sealed record RuntimeUploadLease(
    string UploadId,
    string FilePath,
    string ContentHash,
    long Length,
    string FileName,
    string ContentType);

internal sealed record RuntimeRpcContentLease(
    SunderRpcContentReference Reference,
    string FilePath,
    string OwnerPackageId,
    string AudiencePackageId,
    long Generation,
    int UseNumber,
    bool DeleteOnRelease);

internal sealed class RuntimeContentTransferStore : IDisposable
{
    internal const long MaxPackageUploadBytes = 512L * 1024 * 1024;
    internal const long MaxStackUploadBytes = 256L * 1024 * 1024;
    internal const long MaxMediaUploadBytes = 64L * 1024 * 1024;

    private readonly RuntimePackagePaths _paths;
    private readonly RuntimeTransportPolicyOptions _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, UploadEntry> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DownloadEntry> _downloads = new(StringComparer.Ordinal);
    private readonly object _rpcContentGate = new();
    private readonly Dictionary<string, RpcContentEntry> _rpcContent = new(StringComparer.Ordinal);

    public RuntimeContentTransferStore(
        RuntimePackagePaths paths,
        RuntimeTransportPolicyOptions? policy = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _policy = policy ?? new RuntimeTransportPolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ContentUploadDescriptor> CreateUploadAsync(
        RuntimeUploadKind kind,
        Stream source,
        long? contentLength,
        string? expectedHash,
        string? fileName,
        string? contentType,
        long generation,
        CancellationToken cancellationToken)
    {
        var limit = kind switch
        {
            RuntimeUploadKind.Package => MaxPackageUploadBytes,
            RuntimeUploadKind.Stack => MaxStackUploadBytes,
            RuntimeUploadKind.StackMedia => MaxMediaUploadBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (contentLength is < 0 || contentLength > limit)
        {
            throw new RuntimeUploadLimitException($"Upload exceeds the {limit} byte limit.");
        }

        Directory.CreateDirectory(_paths.TransferRootPath);
        var uploadId = Guid.NewGuid().ToString("N");
        var partialPath = Path.Combine(_paths.TransferRootPath, uploadId + ".partial");
        var extension = kind switch
        {
            RuntimeUploadKind.Package => ".sunderpkg",
            RuntimeUploadKind.Stack => ".sunderstack",
            _ => ".media",
        };
        var finalPath = Path.Combine(_paths.TransferRootPath, uploadId + extension);
        var normalizedFileName = NormalizeFileName(fileName, kind == RuntimeUploadKind.Package ? "package.sunderpkg" : "content.bin");
        var normalizedContentType = NormalizeContentType(contentType);
        long length = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                    if (length > limit)
                    {
                        throw new RuntimeUploadLimitException($"Upload exceeds the {limit} byte limit.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            var contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(expectedHash)
                && !string.Equals(contentHash, NormalizeHash(expectedHash), StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeValidationException("Uploaded content hash does not match X-Content-SHA256.");
            }

            File.Move(partialPath, finalPath);
            var entry = new UploadEntry(kind, finalPath, contentHash, length, normalizedFileName, normalizedContentType, generation, _timeProvider.GetUtcNow());
            if (!_uploads.TryAdd(uploadId, entry))
            {
                throw new IOException("Failed to allocate an upload handle.");
            }

            return new ContentUploadDescriptor(uploadId, contentHash, length, normalizedFileName, normalizedContentType);
        }
        catch
        {
            TryDeleteFile(partialPath);
            TryDeleteFile(finalPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public RuntimeUploadLease? AcquireUpload(string uploadId, RuntimeUploadKind kind, long generation, bool consume)
    {
        SweepExpired(_timeProvider.GetUtcNow());
        UploadEntry? entry;
        var acquired = consume
            ? _uploads.TryRemove(uploadId, out entry)
            : _uploads.TryGetValue(uploadId, out entry);
        if (!acquired || entry is null)
        {
            return null;
        }
        if (entry.Kind != kind
            || entry.Generation != generation
            || !File.Exists(entry.FilePath))
        {
            if (!consume)
            {
                _uploads.TryRemove(new KeyValuePair<string, UploadEntry>(uploadId, entry));
            }
            TryDeleteFile(entry.FilePath);
            return null;
        }

        return new RuntimeUploadLease(uploadId, entry.FilePath, entry.ContentHash, entry.Length, entry.FileName, entry.ContentType);
    }

    public void ReleaseUpload(RuntimeUploadLease lease) => TryDeleteFile(lease.FilePath);

    public void DiscardUpload(string uploadId) => RemoveUpload(uploadId);

    public ContentDownloadDescriptor RegisterDownload(
        string filePath,
        string contentHash,
        long length,
        string fileName,
        string contentType,
        long generation)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new DownloadEntry(filePath, contentHash, length, NormalizeFileName(fileName, "download.bin"), NormalizeContentType(contentType), generation, _timeProvider.GetUtcNow());
        _downloads[id] = entry;
        return new ContentDownloadDescriptor(id, contentHash, length, entry.FileName, entry.ContentType, $"downloads/{id}");
    }

    public RuntimeUploadLease? AcquireDownload(string downloadId, long generation)
    {
        SweepExpired(_timeProvider.GetUtcNow());
        if (!_downloads.TryRemove(downloadId, out var entry)
            || entry.Generation != generation
            || !File.Exists(entry.FilePath))
        {
            if (entry is not null)
            {
                TryDeleteFile(entry.FilePath);
            }
            return null;
        }

        return new RuntimeUploadLease(downloadId, entry.FilePath, entry.ContentHash, entry.Length, entry.FileName, entry.ContentType);
    }

    public async Task<SunderRpcContentReference> RegisterRpcContentAsync(
        string filePath,
        string mediaType,
        string fileName,
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        DateTimeOffset expiresAtUtc,
        SunderRpcContentRepeatability repeatability,
        int maximumUses,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("RPC content does not exist.", fullPath);
        ValidateRpcContentRegistration(
            ownerPackageId,
            audiencePackageId,
            generation,
            expiresAtUtc,
            repeatability,
            maximumUses);
        if (new FileInfo(fullPath).Length > _policy.MaxRpcContentBytes)
        {
            throw new RuntimeUploadLimitException(
                $"RPC content exceeds the {_policy.MaxRpcContentBytes} byte limit.");
        }
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        var reference = new SunderRpcContentReference(
            "rpc-content-" + Guid.NewGuid().ToString("N"),
            stream.Length,
            hash,
            NormalizeContentType(mediaType),
            NormalizeFileName(fileName, "content.bin"),
            expiresAtUtc,
            repeatability);
        AddRpcContent(reference, fullPath, ownerPackageId, audiencePackageId, generation, maximumUses, ownsFile: false);
        return reference;
    }

    public async Task<SunderRpcContentReference> RegisterRpcContentAsync(
        Stream source,
        long? expectedLength,
        string mediaType,
        string fileName,
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        DateTimeOffset expiresAtUtc,
        SunderRpcContentRepeatability repeatability,
        int maximumUses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("RPC content must provide a readable stream.", nameof(source));
        }
        if (expectedLength is < 0 || expectedLength > _policy.MaxRpcContentBytes)
        {
            throw new RuntimeUploadLimitException(
                $"RPC content exceeds the {_policy.MaxRpcContentBytes} byte limit.");
        }
        ValidateRpcContentRegistration(
            ownerPackageId,
            audiencePackageId,
            generation,
            expiresAtUtc,
            repeatability,
            maximumUses);

        Directory.CreateDirectory(_paths.TransferRootPath);
        var id = "rpc-content-" + Guid.NewGuid().ToString("N");
        var partialPath = Path.Combine(_paths.TransferRootPath, id + ".partial");
        var finalPath = Path.Combine(_paths.TransferRootPath, id + ".rpccontent");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    length = checked(length + read);
                    if (length > _policy.MaxRpcContentBytes)
                    {
                        throw new RuntimeUploadLimitException(
                            $"RPC content exceeds the {_policy.MaxRpcContentBytes} byte limit.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (expectedLength is not null && expectedLength != length)
            {
                throw new RuntimeValidationException(
                    "RPC content length does not match its declared length.");
            }
            File.Move(partialPath, finalPath);
            var reference = new SunderRpcContentReference(
                id,
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                NormalizeContentType(mediaType),
                NormalizeFileName(fileName, "content.bin"),
                expiresAtUtc,
                repeatability);
            AddRpcContent(reference, finalPath, ownerPackageId, audiencePackageId, generation, maximumUses, ownsFile: true);
            return reference;
        }
        catch
        {
            TryDeleteFile(partialPath);
            TryDeleteFile(finalPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public RuntimeRpcContentLease? AcquireRpcContent(
        SunderRpcContentReference reference,
        string ownerPackageId,
        string audiencePackageId,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RpcContentEntry entry;
        int useNumber;
        bool exhausted;
        lock (_rpcContentGate)
        {
            if (!_rpcContent.TryGetValue(reference.Id, out entry!)
                || entry.Reference != reference
                || !string.Equals(entry.OwnerPackageId, ownerPackageId, StringComparison.Ordinal)
                || !string.Equals(entry.AudiencePackageId, audiencePackageId, StringComparison.Ordinal)
                || entry.Generation != generation
                || entry.Reference.ExpiresAtUtc <= _timeProvider.GetUtcNow()
                || entry.UseCount >= entry.MaximumUses)
            {
                return null;
            }
            useNumber = ++entry.UseCount;
            exhausted = entry.UseCount >= entry.MaximumUses;
            if (exhausted) _rpcContent.Remove(reference.Id);
        }

        var file = new FileInfo(entry.FilePath);
        if (!file.Exists || file.Length != reference.Length)
        {
            RemoveInvalidRpcContent(entry);
            return null;
        }
        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, reference.Sha256, StringComparison.Ordinal))
        {
            RemoveInvalidRpcContent(entry);
            return null;
        }
        return new RuntimeRpcContentLease(
            reference,
            entry.FilePath,
            entry.OwnerPackageId,
            entry.AudiencePackageId,
            entry.Generation,
            useNumber,
            entry.OwnsFile && exhausted);
    }

    public void ReleaseRpcContent(RuntimeRpcContentLease lease)
    {
        if (lease.DeleteOnRelease) TryDeleteFile(lease.FilePath);
    }

    public void DiscardRpcContent(
        SunderRpcContentReference reference,
        string ownerPackageId,
        string audiencePackageId,
        long generation)
    {
        RpcContentEntry? removed = null;
        lock (_rpcContentGate)
        {
            if (_rpcContent.TryGetValue(reference.Id, out var entry)
                && entry.Reference == reference
                && string.Equals(entry.OwnerPackageId, ownerPackageId, StringComparison.Ordinal)
                && string.Equals(entry.AudiencePackageId, audiencePackageId, StringComparison.Ordinal)
                && entry.Generation == generation)
            {
                _rpcContent.Remove(reference.Id);
                removed = entry;
            }
        }
        if (removed?.OwnsFile == true) TryDeleteFile(removed.FilePath);
    }

    public void Dispose()
    {
        foreach (var id in _uploads.Keys)
        {
            RemoveUpload(id);
        }
        foreach (var (_, entry) in _downloads)
        {
            TryDeleteFile(entry.FilePath);
        }
        _downloads.Clear();
        RpcContentEntry[] rpcContent;
        lock (_rpcContentGate)
        {
            rpcContent = _rpcContent.Values.ToArray();
            _rpcContent.Clear();
        }
        foreach (var entry in rpcContent)
        {
            if (entry.OwnsFile) TryDeleteFile(entry.FilePath);
        }
    }

    internal void SweepExpired(DateTimeOffset now)
    {
        var cutoff = now - _policy.ContentTransferLifetime;
        foreach (var (id, entry) in _uploads)
        {
            if (entry.CreatedAtUtc < cutoff)
            {
                RemoveUpload(id);
            }
        }
        foreach (var (id, entry) in _downloads)
        {
            if (entry.CreatedAtUtc < cutoff && _downloads.TryRemove(id, out var removed))
            {
                TryDeleteFile(removed.FilePath);
            }
        }
        lock (_rpcContentGate)
        {
            foreach (var pair in _rpcContent
                         .Where(pair => pair.Value.Reference.ExpiresAtUtc <= now)
                         .ToArray())
            {
                _rpcContent.Remove(pair.Key);
                if (pair.Value.OwnsFile) TryDeleteFile(pair.Value.FilePath);
            }
        }
    }

    private void AddRpcContent(
        SunderRpcContentReference reference,
        string filePath,
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        int maximumUses,
        bool ownsFile)
    {
        lock (_rpcContentGate)
        {
            _rpcContent.Add(
                reference.Id,
                new RpcContentEntry(
                    reference,
                    filePath,
                    ownerPackageId,
                    audiencePackageId,
                    generation,
                    maximumUses,
                    ownsFile));
        }
    }

    private void RemoveInvalidRpcContent(RpcContentEntry entry)
    {
        lock (_rpcContentGate)
        {
            _rpcContent.Remove(entry.Reference.Id);
        }
        if (entry.OwnsFile) TryDeleteFile(entry.FilePath);
    }

    private void ValidateRpcContentRegistration(
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        DateTimeOffset expiresAtUtc,
        SunderRpcContentRepeatability repeatability,
        int maximumUses)
    {
        if (generation < 0
            || string.IsNullOrWhiteSpace(ownerPackageId)
            || string.IsNullOrWhiteSpace(audiencePackageId)
            || maximumUses < 1
            || maximumUses > _policy.MaxRpcContentUses
            || repeatability == SunderRpcContentRepeatability.SingleUse && maximumUses != 1
            || expiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new ArgumentException("The RPC content ownership, generation, expiry, or use limit is invalid.");
        }
    }

    private void RemoveUpload(string id)
    {
        if (_uploads.TryRemove(id, out var entry))
        {
            TryDeleteFile(entry.FilePath);
        }
    }

    private static string NormalizeFileName(string? value, string fallback)
    {
        var fileName = Path.GetFileName(value?.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ? fallback : fileName;
    }

    private static string NormalizeContentType(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)
            ? "application/octet-stream"
            : value.Trim();

    private static string NormalizeHash(string value)
        => value.Trim().Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record UploadEntry(RuntimeUploadKind Kind, string FilePath, string ContentHash, long Length, string FileName, string ContentType, long Generation, DateTimeOffset CreatedAtUtc);
    private sealed record DownloadEntry(string FilePath, string ContentHash, long Length, string FileName, string ContentType, long Generation, DateTimeOffset CreatedAtUtc);

    private sealed class RpcContentEntry(
        SunderRpcContentReference reference,
        string filePath,
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        int maximumUses,
        bool ownsFile)
    {
        public SunderRpcContentReference Reference { get; } = reference;
        public string FilePath { get; } = filePath;
        public string OwnerPackageId { get; } = ownerPackageId;
        public string AudiencePackageId { get; } = audiencePackageId;
        public long Generation { get; } = generation;
        public int MaximumUses { get; } = maximumUses;
        public bool OwnsFile { get; } = ownsFile;
        public int UseCount { get; set; }
    }
}
