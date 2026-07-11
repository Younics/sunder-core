using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

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

internal sealed class RuntimeContentTransferStore(RuntimePackagePaths paths) : IDisposable
{
    internal const long MaxPackageUploadBytes = 512L * 1024 * 1024;
    internal const long MaxStackUploadBytes = 256L * 1024 * 1024;
    internal const long MaxMediaUploadBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan HandleLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, UploadEntry> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DownloadEntry> _downloads = new(StringComparer.Ordinal);

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

        Directory.CreateDirectory(paths.TransferRootPath);
        var uploadId = Guid.NewGuid().ToString("N");
        var partialPath = Path.Combine(paths.TransferRootPath, uploadId + ".partial");
        var extension = kind switch
        {
            RuntimeUploadKind.Package => ".sunderpkg",
            RuntimeUploadKind.Stack => ".sunderstack",
            _ => ".media",
        };
        var finalPath = Path.Combine(paths.TransferRootPath, uploadId + extension);
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
            var entry = new UploadEntry(kind, finalPath, contentHash, length, normalizedFileName, normalizedContentType, generation, DateTimeOffset.UtcNow);
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
        CleanupExpired();
        if (!_uploads.TryGetValue(uploadId, out var entry)
            || entry.Kind != kind
            || entry.Generation != generation
            || !File.Exists(entry.FilePath))
        {
            RemoveUpload(uploadId);
            return null;
        }

        if (consume)
        {
            _uploads.TryRemove(uploadId, out _);
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
        var entry = new DownloadEntry(filePath, contentHash, length, NormalizeFileName(fileName, "download.bin"), NormalizeContentType(contentType), generation, DateTimeOffset.UtcNow);
        _downloads[id] = entry;
        return new ContentDownloadDescriptor(id, contentHash, length, entry.FileName, entry.ContentType, $"downloads/{id}");
    }

    public RuntimeUploadLease? AcquireDownload(string downloadId, long generation)
    {
        CleanupExpired();
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
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - HandleLifetime;
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
}
