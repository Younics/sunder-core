using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe asynchronous package-owned key/value state.</summary>
/// <remarks>
/// Keys are case-sensitive portable ASCII tokens and values are bounded UTF-8 strings as defined by
/// <see cref="PackageStorageValidation"/>. Lists are stable ordinally sorted snapshots. Mutations are atomic;
/// cancellation reported before commit leaves the previous value unchanged, while a committed mutation succeeds
/// even if cancellation is requested concurrently after the commit.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageKeyValueStore
{
    /// <summary>Gets a value, returning <see langword="null"/> when the key is absent.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid.</exception>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically creates or replaces a value.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="value"/> is invalid.</exception>
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a key exists.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid.</exception>
    Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes a value when present.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid.</exception>
    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists keys that start with the optional case-sensitive prefix.</summary>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is neither <see langword="null"/>, empty, nor a valid key prefix.</exception>
    Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
