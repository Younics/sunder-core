using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe asynchronous package-owned key/value state.</summary>
/// <remarks>Keys are opaque, case-sensitive tokens. Lists are stable ordinally sorted snapshots.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageKeyValueStore
{
    /// <summary>Gets a value, returning <see langword="null"/> when the key is absent.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically creates or replaces a value.</summary>
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a key exists.</summary>
    Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes a value when present.</summary>
    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists keys that start with the optional case-sensitive prefix.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
