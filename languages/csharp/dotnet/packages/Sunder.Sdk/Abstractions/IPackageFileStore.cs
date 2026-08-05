using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe asynchronous access to package-owned files.</summary>
/// <remarks>
/// Paths use the portable grammar and bounds in <see cref="PackageStorageValidation"/>. They are always contained
/// beneath the package file-store root and must not traverse symbolic links or reparse points. Name casing follows
/// the host filesystem; packages must not rely on names that differ only by case. The host owns persistence, callers
/// own returned byte arrays, and callers must dispose streams returned by <see cref="OpenReadAsync"/>.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageFileStore
{
    /// <summary>Reads a file, returning <see langword="null"/> when it does not exist.</summary>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a valid portable relative path.</exception>
    /// <exception cref="InvalidDataException">The stored file exceeds <see cref="PackageStorageValidation.MaximumFileBytes"/>.</exception>
    /// <exception cref="IOException">The host could not read the file.</exception>
    /// <exception cref="UnauthorizedAccessException">The host denied access to the file.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Opens a caller-owned read-only stream, returning <see langword="null"/> when the file does not exist.</summary>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a valid portable relative path.</exception>
    /// <exception cref="InvalidDataException">The stored file exceeds <see cref="PackageStorageValidation.MaximumFileBytes"/>.</exception>
    /// <exception cref="IOException">The host could not read the file.</exception>
    /// <exception cref="UnauthorizedAccessException">The host denied access to the file.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    async ValueTask<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateRelativePath(relativePath);
        var contents = await ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (contents is not null && !PackageStorageValidation.IsValidFileLength(contents.LongLength))
        {
            throw new InvalidDataException(
                $"The package file exceeds the {PackageStorageValidation.MaximumFileBytes} byte limit.");
        }
        return contents is null ? null : new MemoryStream(contents, writable: false);
    }

    /// <summary>Atomically creates or replaces a file, creating parent directories as needed.</summary>
    /// <remarks>
    /// Cancellation before the atomic replacement leaves any previous file unchanged. Once replacement commits, the
    /// operation completes successfully even if cancellation is requested concurrently after that commit.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is invalid or <paramref name="contents"/> exceeds the package file limit.</exception>
    /// <exception cref="IOException">The host could not commit the file.</exception>
    /// <exception cref="UnauthorizedAccessException">The host denied access to the file.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before replacement committed.</exception>
    Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically writes the remaining content from a caller-owned readable stream.</summary>
    /// <remarks>
    /// The method advances but never disposes <paramref name="contents"/>. Cancellation or a file-limit failure before
    /// atomic replacement leaves any previous file unchanged. Once replacement commits, the operation completes
    /// successfully even if cancellation is requested concurrently after that commit.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is invalid, the stream is unreadable, or its remaining content exceeds the package file limit.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="contents"/> was disposed.</exception>
    /// <exception cref="IOException">The host could not read or commit the file.</exception>
    /// <exception cref="UnauthorizedAccessException">The host denied access to the file.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before replacement committed.</exception>
    async Task WriteAsync(
        string relativePath,
        Stream contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ValidateRelativePath(relativePath);
        if (!contents.CanRead)
        {
            throw new ArgumentException("Package file content streams must be readable.", nameof(contents));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var bytesRead = await contents.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }
            if (buffer.Length + bytesRead > PackageStorageValidation.MaximumFileBytes)
            {
                throw new ArgumentException(
                    $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                    nameof(contents));
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        await WriteAsync(relativePath, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a file when present.</summary>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a valid portable relative path.</exception>
    /// <exception cref="IOException">The host could not delete the file.</exception>
    /// <exception cref="UnauthorizedAccessException">The host denied access to the file.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before deletion.</exception>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);

    private static void ValidateRelativePath(string relativePath)
    {
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "Package file paths must be relative, portable, non-empty, free of traversal, and within the declared length limits.",
                nameof(relativePath));
        }
    }
}
