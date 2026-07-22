using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe asynchronous access to host-protected package secrets.</summary>
/// <remarks>
/// Keys are case-sensitive portable ASCII tokens and values are bounded UTF-8 strings as defined by
/// <see cref="PackageStorageValidation"/>. The host owns encryption and persistence; packages must not persist
/// returned plaintext. Mutations are atomic and cancellation reported before commit leaves the previous secret
/// unchanged.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.SecretsV1)]
public interface IPackageSecrets
{
    /// <summary>Gets a secret, returning <see langword="null"/> when absent.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid.</exception>
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically creates or replaces a secret.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="value"/> is invalid.</exception>
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a secret when present.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid.</exception>
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}
