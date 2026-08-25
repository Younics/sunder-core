using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Sunder.Package.Format;
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
    internal const long MaxPackageUploadBytes = RuntimeRequestSizeLimits.PackageUploadMaxRequestBodyBytes;
    internal const long MaxStackUploadBytes = RuntimeRequestSizeLimits.StackUploadMaxRequestBodyBytes;
    internal const long MaxMediaUploadBytes = RuntimeRequestSizeLimits.StackMediaUploadMaxRequestBodyBytes;

    private readonly RuntimePackagePaths _paths;
    private readonly RuntimeTransportPolicyOptions _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, UploadEntry> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DownloadEntry> _downloads = new(StringComparer.Ordinal);
    private readonly object _pendingTransfersGate = new();
    private long _pendingTransferBytes;
    private int _pendingTransferCount;
    private readonly object _rpcContentGate = new();
    private readonly Dictionary<string, RpcContentEntry> _rpcContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RpcContentUsage> _rpcContentByPackage = new(StringComparer.Ordinal);
    private readonly Dictionary<RuntimeRpcContentAuthority, RpcContentUsage> _rpcContentByAuthority = [];
    private long _rpcContentBytes;
    private int _rpcContentReferences;

    public RuntimeContentTransferStore(
        RuntimePackagePaths paths,
        RuntimeTransportPolicyOptions? policy = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _policy = policy ?? new RuntimeTransportPolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static void CleanupCrashLeftTransfers(RuntimePackagePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.TransferRootPath)) return;

        foreach (var path in Directory.EnumerateFileSystemEntries(paths.TransferRootPath))
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
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
        PendingTransferReservation? reservation = null;
        try
        {
            reservation = ReservePendingTransfer(contentLength ?? 0);
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

                    if (read > limit - length)
                    {
                        throw new RuntimeUploadLimitException($"Upload exceeds the {limit} byte limit.");
                    }
                    length += read;
                    EnsurePendingTransferReservationBytes(reservation, length);

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

            ResizePendingTransferReservation(reservation, length);
            File.Move(partialPath, finalPath);
            var entry = new UploadEntry(
                kind,
                finalPath,
                contentHash,
                length,
                normalizedFileName,
                normalizedContentType,
                generation,
                _timeProvider.GetUtcNow(),
                reservation);
            if (!_uploads.TryAdd(uploadId, entry))
            {
                throw new IOException("Failed to allocate an upload handle.");
            }

            reservation = null;
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
            if (reservation is not null) RollbackPendingTransferReservation(reservation);
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
        if (consume) ReleasePendingTransferReservation(entry.Reservation);
        if (entry.Kind != kind
            || entry.Generation != generation
            || !File.Exists(entry.FilePath))
        {
            if (consume)
            {
                TryDeleteFile(entry.FilePath);
            }
            else if (_uploads.TryRemove(new KeyValuePair<string, UploadEntry>(uploadId, entry)))
            {
                ReleasePendingTransferReservation(entry.Reservation);
                TryDeleteFile(entry.FilePath);
            }
            return null;
        }

        return new RuntimeUploadLease(uploadId, entry.FilePath, entry.ContentHash, entry.Length, entry.FileName, entry.ContentType);
    }

    public void ReleaseUpload(RuntimeUploadLease lease)
    {
        RemoveUpload(lease.UploadId);
        TryDeleteFile(lease.FilePath);
    }

    public void DiscardUpload(string uploadId) => RemoveUpload(uploadId);

    public ContentDownloadDescriptor RegisterDownload(
        string filePath,
        string contentHash,
        long length,
        string fileName,
        string contentType,
        long generation)
    {
        if (length < 0)
        {
            throw new RuntimeValidationException("Download length cannot be negative.");
        }
        var id = Guid.NewGuid().ToString("N");
        PendingTransferReservation? reservation = ReservePendingTransfer(length);
        try
        {
            var entry = new DownloadEntry(
                filePath,
                contentHash,
                length,
                NormalizeFileName(fileName, "download.bin"),
                NormalizeContentType(contentType),
                generation,
                _timeProvider.GetUtcNow(),
                reservation);
            if (!_downloads.TryAdd(id, entry))
            {
                throw new IOException("Failed to allocate a download handle.");
            }
            reservation = null;
            return new ContentDownloadDescriptor(id, contentHash, length, entry.FileName, entry.ContentType, $"downloads/{id}");
        }
        finally
        {
            if (reservation is not null) RollbackPendingTransferReservation(reservation);
        }
    }

    public RuntimeUploadLease? AcquireDownload(string downloadId, long generation)
    {
        SweepExpired(_timeProvider.GetUtcNow());
        if (!_downloads.TryRemove(downloadId, out var entry))
        {
            return null;
        }
        ReleasePendingTransferReservation(entry.Reservation);
        if (entry.Generation != generation
            || !File.Exists(entry.FilePath))
        {
            TryDeleteFile(entry.FilePath);
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
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > _policy.MaxRpcContentBytes)
        {
            throw RpcContentResourceExhausted(
                "rpc.content.reference-byte-limit",
                $"RPC content exceeds the {_policy.MaxRpcContentBytes} byte per-reference limit.");
        }

        RuntimeRpcContentQuotaReservation? reservation = ReserveRpcContent(
            ownerPackageId,
            authority,
            stream.Length);
        try
        {
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
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
                reservation,
                cancellationToken);
            reservation = null;
            return reference;
        }
        finally
        {
            if (reservation is not null) RollbackRpcContentReservation(reservation);
        }
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
            throw RpcContentResourceExhausted(
                "rpc.content.reference-byte-limit",
                $"RPC content exceeds the {_policy.MaxRpcContentBytes} byte per-reference limit.");
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
        RuntimeRpcContentQuotaReservation? reservation = ReserveRpcContent(
            ownerPackageId,
            authority,
            expectedLength ?? 0);
        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
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
                        throw RpcContentResourceExhausted(
                            "rpc.content.reference-byte-limit",
                            $"RPC content exceeds the {_policy.MaxRpcContentBytes} byte per-reference limit.");
                    }
                    EnsureRpcContentReservationBytes(reservation, length);
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
            ResizeRpcContentReservation(reservation, length);
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
                reservation,
                cancellationToken);
            reservation = null;
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
            if (reservation is not null) RollbackRpcContentReservation(reservation);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
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
                    ReleaseRpcContentReservation(entry.Reservation);
                    invalidEntry = entry;
                }
                else
                {
                    stream.Position = 0;
                    var useNumber = ++entry.UseCount;
                    var exhausted = entry.UseCount >= entry.MaximumUses;
                    if (exhausted)
                    {
                        _rpcContent.Remove(reference.Id);
                        ReleaseRpcContentReservation(entry.Reservation);
                    }
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
                ReleaseRpcContentReservation(entry.Reservation);
                invalidEntry = entry;
            }
            catch (UnauthorizedAccessException)
            {
                stream?.Dispose();
                _rpcContent.Remove(reference.Id);
                ReleaseRpcContentReservation(entry.Reservation);
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
                ReleaseRpcContentReservation(entry.Reservation);
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
                ReleaseRpcContentReservation(entry.Reservation);
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
        foreach (var id in _downloads.Keys)
        {
            RemoveDownload(id);
        }
        RpcContentEntry[] rpcContent;
        lock (_rpcContentGate)
        {
            rpcContent = _rpcContent.Values.ToArray();
            _rpcContent.Clear();
            foreach (var entry in rpcContent) ReleaseRpcContentReservation(entry.Reservation);
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
            if (entry.CreatedAtUtc < cutoff
                && _downloads.TryRemove(new KeyValuePair<string, DownloadEntry>(id, entry)))
            {
                ReleasePendingTransferReservation(entry.Reservation);
                TryDeleteFile(entry.FilePath);
            }
        }
        lock (_rpcContentGate)
        {
            foreach (var pair in _rpcContent
                         .Where(pair => pair.Value.Reference.ExpiresAtUtc <= now)
                         .ToArray())
            {
                _rpcContent.Remove(pair.Key);
                ReleaseRpcContentReservation(pair.Value.Reservation);
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
        RuntimeRpcContentQuotaReservation reservation,
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
                    authority,
                    reservation));
        }
    }

    private RuntimeRpcContentQuotaReservation ReserveRpcContent(
        string ownerPackageId,
        RuntimeRpcContentAuthority? authority,
        long bytes)
    {
        lock (_rpcContentGate)
        {
            if (bytes < 0 || bytes > _policy.MaxRpcContentBytes)
            {
                throw RpcContentResourceExhausted(
                    "rpc.content.reference-byte-limit",
                    "RPC content exceeds its per-reference byte limit.");
            }
            var hasPackageUsage = _rpcContentByPackage.TryGetValue(
                ownerPackageId,
                out var packageUsage);
            packageUsage ??= new RpcContentUsage();
            RpcContentUsage? authorityUsage = null;
            var hasAuthorityUsage = authority is not null
                                    && _rpcContentByAuthority.TryGetValue(authority, out authorityUsage);
            if (authority is not null) authorityUsage ??= new RpcContentUsage();
            EnsureReferenceCapacity(packageUsage, authorityUsage);
            EnsureByteCapacity(packageUsage, authorityUsage, bytes);

            if (!hasPackageUsage) _rpcContentByPackage.Add(ownerPackageId, packageUsage);
            if (authority is not null && !hasAuthorityUsage)
            {
                _rpcContentByAuthority.Add(authority, authorityUsage!);
            }
            _rpcContentReferences++;
            _rpcContentBytes += bytes;
            packageUsage.References++;
            packageUsage.Bytes += bytes;
            if (authorityUsage is not null)
            {
                authorityUsage.References++;
                authorityUsage.Bytes += bytes;
            }
            return new RuntimeRpcContentQuotaReservation(
                ownerPackageId,
                authority,
                bytes);
        }
    }

    private void EnsureRpcContentReservationBytes(
        RuntimeRpcContentQuotaReservation reservation,
        long bytes)
    {
        lock (_rpcContentGate)
        {
            if (!reservation.IsActive || bytes <= reservation.Bytes) return;
            ResizeRpcContentReservationCore(reservation, bytes);
        }
    }

    private void ResizeRpcContentReservation(
        RuntimeRpcContentQuotaReservation reservation,
        long bytes)
    {
        lock (_rpcContentGate)
        {
            if (!reservation.IsActive)
            {
                throw new InvalidOperationException("The RPC content reservation is no longer active.");
            }
            ResizeRpcContentReservationCore(reservation, bytes);
        }
    }

    private void ResizeRpcContentReservationCore(
        RuntimeRpcContentQuotaReservation reservation,
        long bytes)
    {
        if (bytes < 0 || bytes > _policy.MaxRpcContentBytes)
        {
            throw RpcContentResourceExhausted(
                "rpc.content.reference-byte-limit",
                "RPC content exceeds its per-reference byte limit.");
        }
        var delta = bytes - reservation.Bytes;
        if (delta == 0) return;
        var packageUsage = _rpcContentByPackage[reservation.OwnerPackageId];
        var authorityUsage = reservation.Authority is null
            ? null
            : _rpcContentByAuthority[reservation.Authority];
        if (delta > 0) EnsureByteCapacity(packageUsage, authorityUsage, delta);

        _rpcContentBytes += delta;
        packageUsage.Bytes += delta;
        if (authorityUsage is not null) authorityUsage.Bytes += delta;
        reservation.Bytes = bytes;
    }

    private void RollbackRpcContentReservation(RuntimeRpcContentQuotaReservation reservation)
    {
        lock (_rpcContentGate)
        {
            ReleaseRpcContentReservation(reservation);
        }
    }

    private void ReleaseRpcContentReservation(RuntimeRpcContentQuotaReservation reservation)
    {
        if (!reservation.IsActive) return;
        reservation.IsActive = false;
        _rpcContentReferences--;
        _rpcContentBytes -= reservation.Bytes;
        ReleaseUsage(
            _rpcContentByPackage,
            reservation.OwnerPackageId,
            reservation.Bytes);
        if (reservation.Authority is not null)
        {
            ReleaseUsage(
                _rpcContentByAuthority,
                reservation.Authority,
                reservation.Bytes);
        }
    }

    private void EnsureReferenceCapacity(
        RpcContentUsage packageUsage,
        RpcContentUsage? authorityUsage)
    {
        if (_rpcContentReferences >= _policy.MaxRpcContentReferencesPerRuntime)
        {
            throw RpcContentResourceExhausted(
                "rpc.content.runtime-reference-limit",
                "The Runtime active RPC content-reference limit was reached.");
        }
        if (packageUsage.References >= _policy.MaxRpcContentReferencesPerPackage)
        {
            throw RpcContentResourceExhausted(
                "rpc.content.package-reference-limit",
                "The package active RPC content-reference limit was reached.");
        }
        if (authorityUsage?.References >= _policy.MaxRpcContentReferencesPerCall)
        {
            throw RpcContentResourceExhausted(
                "rpc.content.call-reference-limit",
                "The call active RPC content-reference limit was reached.");
        }
    }

    private void EnsureByteCapacity(
        RpcContentUsage packageUsage,
        RpcContentUsage? authorityUsage,
        long additionalBytes)
    {
        if (WouldExceed(_rpcContentBytes, additionalBytes, _policy.MaxRpcContentBytesPerRuntime))
        {
            throw RpcContentResourceExhausted(
                "rpc.content.runtime-byte-limit",
                "The Runtime aggregate RPC content byte limit was reached.");
        }
        if (WouldExceed(packageUsage.Bytes, additionalBytes, _policy.MaxRpcContentBytesPerPackage))
        {
            throw RpcContentResourceExhausted(
                "rpc.content.package-byte-limit",
                "The package aggregate RPC content byte limit was reached.");
        }
        if (authorityUsage is not null
            && WouldExceed(authorityUsage.Bytes, additionalBytes, _policy.MaxRpcContentBytesPerCall))
        {
            throw RpcContentResourceExhausted(
                "rpc.content.call-byte-limit",
                "The call aggregate RPC content byte limit was reached.");
        }
    }

    private static bool WouldExceed(long current, long additional, long limit)
        => additional > limit || current > limit - additional;

    private PendingTransferReservation ReservePendingTransfer(long bytes)
    {
        lock (_pendingTransfersGate)
        {
            if (_pendingTransferCount >= _policy.MaxPendingContentTransfers)
            {
                throw new RuntimeUploadLimitException(
                    $"The Runtime already has {_policy.MaxPendingContentTransfers} pending content transfers.");
            }
            if (bytes < 0
                || WouldExceed(_pendingTransferBytes, bytes, _policy.MaxPendingContentTransferBytes))
            {
                throw new RuntimeUploadLimitException(
                    $"Pending content transfers exceed the {_policy.MaxPendingContentTransferBytes} byte aggregate limit.");
            }

            _pendingTransferCount++;
            _pendingTransferBytes += bytes;
            return new PendingTransferReservation(bytes);
        }
    }

    private void EnsurePendingTransferReservationBytes(
        PendingTransferReservation reservation,
        long bytes)
    {
        lock (_pendingTransfersGate)
        {
            if (!reservation.IsActive || bytes <= reservation.Bytes) return;
            ResizePendingTransferReservationCore(reservation, bytes);
        }
    }

    private void ResizePendingTransferReservation(
        PendingTransferReservation reservation,
        long bytes)
    {
        lock (_pendingTransfersGate)
        {
            if (!reservation.IsActive)
            {
                throw new InvalidOperationException("The pending content transfer reservation is no longer active.");
            }
            ResizePendingTransferReservationCore(reservation, bytes);
        }
    }

    private void ResizePendingTransferReservationCore(
        PendingTransferReservation reservation,
        long bytes)
    {
        var delta = bytes - reservation.Bytes;
        if (delta > 0
            && WouldExceed(_pendingTransferBytes, delta, _policy.MaxPendingContentTransferBytes))
        {
            throw new RuntimeUploadLimitException(
                $"Pending content transfers exceed the {_policy.MaxPendingContentTransferBytes} byte aggregate limit.");
        }

        _pendingTransferBytes += delta;
        reservation.Bytes = bytes;
    }

    private void RollbackPendingTransferReservation(PendingTransferReservation reservation)
        => ReleasePendingTransferReservation(reservation);

    private void ReleasePendingTransferReservation(PendingTransferReservation reservation)
    {
        lock (_pendingTransfersGate)
        {
            if (!reservation.IsActive) return;
            reservation.IsActive = false;
            _pendingTransferCount--;
            _pendingTransferBytes -= reservation.Bytes;
        }
    }

    private static void ReleaseUsage<TKey>(
        Dictionary<TKey, RpcContentUsage> usage,
        TKey key,
        long bytes) where TKey : notnull
    {
        var current = usage[key];
        current.References--;
        current.Bytes -= bytes;
        if (current.References == 0) usage.Remove(key);
    }

    private static SunderRpcException RpcContentResourceExhausted(string code, string message)
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.ResourceExhausted,
            code,
            message);

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
            ReleasePendingTransferReservation(entry.Reservation);
            TryDeleteFile(entry.FilePath);
        }
    }

    private void RemoveDownload(string id)
    {
        if (_downloads.TryRemove(id, out var entry))
        {
            ReleasePendingTransferReservation(entry.Reservation);
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

    private sealed record UploadEntry(
        RuntimeUploadKind Kind,
        string FilePath,
        string ContentHash,
        long Length,
        string FileName,
        string ContentType,
        long Generation,
        DateTimeOffset CreatedAtUtc,
        PendingTransferReservation Reservation);

    private sealed record DownloadEntry(
        string FilePath,
        string ContentHash,
        long Length,
        string FileName,
        string ContentType,
        long Generation,
        DateTimeOffset CreatedAtUtc,
        PendingTransferReservation Reservation);

    private sealed class PendingTransferReservation(long bytes)
    {
        public long Bytes { get; set; } = bytes;
        public bool IsActive { get; set; } = true;
    }

    private sealed class RpcContentEntry(
        SunderRpcContentReference reference,
        string filePath,
        string ownerPackageId,
        string audiencePackageId,
        long generation,
        RuntimeRpcContentEndpoint providerEndpoint,
        int maximumUses,
        bool ownsFile,
        RuntimeRpcContentAuthority? authority,
        RuntimeRpcContentQuotaReservation reservation)
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
        public RuntimeRpcContentQuotaReservation Reservation { get; } = reservation;
        public int UseCount { get; set; }
    }

    private sealed class RpcContentUsage
    {
        public long Bytes { get; set; }
        public int References { get; set; }
    }

    private sealed class RuntimeRpcContentQuotaReservation(
        string ownerPackageId,
        RuntimeRpcContentAuthority? authority,
        long bytes)
    {
        public string OwnerPackageId { get; } = ownerPackageId;
        public RuntimeRpcContentAuthority? Authority { get; } = authority;
        public long Bytes { get; set; } = bytes;
        public bool IsActive { get; set; } = true;
    }
}
