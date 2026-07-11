using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides thread-safe asynchronous access to host-protected package secrets.</summary>
/// <remarks>Keys are opaque and case-sensitive. The host owns encryption and persistence; packages must not persist returned plaintext.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.SecretsV1)]
public interface IPackageSecrets
{
    /// <summary>Gets a secret, returning <see langword="null"/> when absent.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomically creates or replaces a secret.</summary>
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a secret when present.</summary>
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}
