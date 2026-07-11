using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe asynchronous access to package-owned files.</summary>
/// <remarks>Paths are case-sensitive relative capability paths; rooted paths and traversal are rejected. The host owns persistence and callers own returned byte arrays.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageFileStore
{
    /// <summary>Reads a file, returning <see langword="null"/> when it does not exist.</summary>
    Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Atomically writes a file, creating parent directories as needed.</summary>
    Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a file when present.</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}
