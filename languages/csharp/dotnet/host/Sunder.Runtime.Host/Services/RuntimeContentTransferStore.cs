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

internal readonly record struct RuntimeRpcContentEndpoint(
    string EndpointReference,
    Guid ActivationId);

internal sealed class RuntimeRpcContentAuthority(string id)
{
    private int _revoked;

    public string Id { get; } = id;
    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;

    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
}

internal sealed record RuntimeRpcContentLease(
    SunderRpcContentReference Reference,
    Stream Content,
    string OwnerPackageId,
    string AudiencePackageId,
    long Generation,
    RuntimeRpcContentEndpoint ProviderEndpoint,
    int UseNumber,
    string FilePath,
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
        RuntimeRpcContentEndpoint providerEndpoint,
        DateTimeOffset expiresAtUtc,
        SunderRpcContentRepeatability repeatability,
        int maximumUses,
        CancellationToken cancellationToken = default,
        RuntimeRpcContentAuthority? authority = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("RPC content does not exist.", fullPath);
        ValidateRpcContentRegistration(
            ownerPackageId,
            audiencePackageId,
            generation,
            providerEndpoint,
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
        AddRpcContent(
            reference,
            fullPath,
            ownerPackageId,
            audiencePackageId,
            generation,
            providerEndpoint,
            maximumUses,
            ownsFile: false,
            authority,
            cancellationToken);
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
        RuntimeRpcContentEndpoint providerEndpoint,
        DateTimeOffset expiresAtUtc,
        SunderRpcContentRepeatability repeatability,
        int maximumUses,
        CancellationToken cancellationToken = default,
        RuntimeRpcContentAuthority? authority = null)
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
            providerEndpoint,
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
            AddRpcContent(
                reference,
                finalPath,
                ownerPackageId,
                audiencePackageId,
                generation,
                providerEndpoint,
                maximumUses,
                ownsFile: true,
                authority,
                cancellationToken);
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
        long generation,
        RuntimeRpcContentEndpoint providerEndpoint,
        RuntimeRpcContentAuthority? authority = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RpcContentEntry? invalidEntry = null;
        RuntimeRpcContentLease? lease = null;
        lock (_rpcContentGate)
        {
            if (!_rpcContent.TryGetValue(reference.Id, out var entry)
                || entry.Reference != reference
                || !string.Equals(entry.OwnerPackageId, ownerPackageId, StringComparison.Ordinal)
                || !string.Equals(entry.AudiencePackageId, audiencePackageId, StringComparison.Ordinal)
                || entry.Generation != generation
                || entry.ProviderEndpoint != providerEndpoint
                || !ReferenceEquals(entry.Authority, authority)
                || authority?.IsRevoked == true
                || entry.Reference.ExpiresAtUtc <= _timeProvider.GetUtcNow()
                || entry.UseCount >= entry.MaximumUses)
            {
                return null;
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    entry.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != reference.Length
                    || !string.Equals(
                        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                        reference.Sha256,
                        StringComparison.Ordinal))
                {
                    stream.Dispose();
                    stream = null;
                    _rpcContent.Remove(reference.Id);
                    invalidEntry = entry;
                }
                else
                {
                    stream.Position = 0;
                    var useNumber = ++entry.UseCount;
                    var exhausted = entry.UseCount >= entry.MaximumUses;
                    if (exhausted) _rpcContent.Remove(reference.Id);
                    lease = new RuntimeRpcContentLease(
                        reference,
                        stream,
                        entry.OwnerPackageId,
                        entry.AudiencePackageId,
                        entry.Generation,
                        entry.ProviderEndpoint,
                        useNumber,
                        entry.FilePath,
                        entry.OwnsFile && exhausted);
                    stream = null;
                }
            }
            catch (IOException)
            {
                stream?.Dispose();
                _rpcContent.Remove(reference.Id);
                invalidEntry = entry;
            }
            catch (UnauthorizedAccessException)
            {
                stream?.Dispose();
                _rpcContent.Remove(reference.Id);
                invalidEntry = entry;
            }
        }
        if (invalidEntry?.OwnsFile == true) TryDeleteFile(invalidEntry.FilePath);
        return lease;
    }

    public bool TryGetRpcContentEndpoint(
        SunderRpcContentReference reference,
        string audiencePackageId,
        long generation,
        RuntimeRpcContentAuthority authority,
        out RuntimeRpcContentEndpoint providerEndpoint)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(authority);
        lock (_rpcContentGate)
        {
            if (_rpcContent.TryGetValue(reference.Id, out var entry)
                && entry.Reference == reference
                && string.Equals(entry.AudiencePackageId, audiencePackageId, StringComparison.Ordinal)
                && entry.Generation == generation
                && ReferenceEquals(entry.Authority, authority)
                && !authority.IsRevoked
                && entry.Reference.ExpiresAtUtc > _timeProvider.GetUtcNow()
                && entry.UseCount < entry.MaximumUses)
            {
                providerEndpoint = entry.ProviderEndpoint;
                return true;
            }
        }
        providerEndpoint = default;
        return false;
    }

    public RuntimeRpcContentLease? AcquireRpcContentForAudience(
        SunderRpcContentReference reference,
        string audiencePackageId,
        long generation,
        RuntimeRpcContentEndpoint providerEndpoint,
        RuntimeRpcContentAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(authority);
        RpcContentEntry entry;
        lock (_rpcContentGate)
        {
            if (!_rpcContent.TryGetValue(reference.Id, out entry!)
                || entry.Reference != reference
                || !string.Equals(entry.AudiencePackageId, audiencePackageId, StringComparison.Ordinal)
                || entry.Generation != generation
                || entry.ProviderEndpoint != providerEndpoint
                || !ReferenceEquals(entry.Authority, authority)
                || authority.IsRevoked)
            {
                return null;
            }
        }
        return AcquireRpcContent(
            reference,
            entry.OwnerPackageId,
            audiencePackageId,
            generation,
            providerEndpoint,
            authority);
    }

    public void ReleaseRpcContent(RuntimeRpcContentLease lease)
    {
        lease.Content.Dispose();
        if (lease.DeleteOnRelease) TryDeleteFile(lease.FilePath);
    }

    public void DiscardRpcContent(
        SunderRpcContentReference reference,
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        RuntimeRpcContentEndpoint providerEndpoint,
        RuntimeRpcContentAuthority? authority = null)
    {
        RpcContentEntry? removed = null;
        lock (_rpcContentGate)
        {
            if (_rpcContent.TryGetValue(reference.Id, out var entry)
                && entry.Reference == reference
                && string.Equals(entry.OwnerPackageId, ownerPackageId, StringComparison.Ordinal)
                && string.Equals(entry.AudiencePackageId, audiencePackageId, StringComparison.Ordinal)
                && entry.Generation == generation
                && entry.ProviderEndpoint == providerEndpoint
                && ReferenceEquals(entry.Authority, authority))
            {
                _rpcContent.Remove(reference.Id);
                removed = entry;
            }
        }
        if (removed?.OwnsFile == true) TryDeleteFile(removed.FilePath);
    }

    public void DiscardRpcContentAuthority(RuntimeRpcContentAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.Revoke();
        RpcContentEntry[] removed;
        lock (_rpcContentGate)
        {
            removed = _rpcContent.Values
                .Where(entry => ReferenceEquals(entry.Authority, authority))
                .ToArray();
            foreach (var entry in removed)
            {
                _rpcContent.Remove(entry.Reference.Id);
            }
        }
        foreach (var entry in removed)
        {
            if (entry.OwnsFile) TryDeleteFile(entry.FilePath);
        }
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
        RuntimeRpcContentEndpoint providerEndpoint,
        int maximumUses,
        bool ownsFile,
        RuntimeRpcContentAuthority? authority,
        CancellationToken cancellationToken)
    {
        lock (_rpcContentGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (authority?.IsRevoked == true)
            {
                throw new OperationCanceledException(
                    "The RPC content authority was revoked during registration.",
                    cancellationToken);
            }
            _rpcContent.Add(
                reference.Id,
                new RpcContentEntry(
                    reference,
                    filePath,
                    ownerPackageId,
                    audiencePackageId,
                    generation,
                    providerEndpoint,
                    maximumUses,
                    ownsFile,
                    authority));
        }
    }

    private void ValidateRpcContentRegistration(
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        RuntimeRpcContentEndpoint providerEndpoint,
        DateTimeOffset expiresAtUtc,
        SunderRpcContentRepeatability repeatability,
        int maximumUses)
    {
        if (generation < 0
            || string.IsNullOrWhiteSpace(ownerPackageId)
            || string.IsNullOrWhiteSpace(audiencePackageId)
            || string.IsNullOrWhiteSpace(providerEndpoint.EndpointReference)
            || providerEndpoint.ActivationId == Guid.Empty
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
        RuntimeRpcContentEndpoint providerEndpoint,
        int maximumUses,
        bool ownsFile,
        RuntimeRpcContentAuthority? authority)
    {
        public SunderRpcContentReference Reference { get; } = reference;
        public string FilePath { get; } = filePath;
        public string OwnerPackageId { get; } = ownerPackageId;
        public string AudiencePackageId { get; } = audiencePackageId;
        public long Generation { get; } = generation;
        public RuntimeRpcContentEndpoint ProviderEndpoint { get; } = providerEndpoint;
        public int MaximumUses { get; } = maximumUses;
        public bool OwnsFile { get; } = ownsFile;
        public RuntimeRpcContentAuthority? Authority { get; } = authority;
        public int UseCount { get; set; }
    }
}
