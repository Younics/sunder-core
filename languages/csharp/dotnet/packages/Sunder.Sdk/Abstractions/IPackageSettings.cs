using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides validated access to the package's schema-declared, non-secret settings.</summary>
/// <remarks>
/// Settings are persisted independently from <see cref="IPackageStorageContext.State"/>. Secret schema fields must be
/// accessed through <see cref="IPackageSecrets"/> instead. Field keys and string values use the portable bounds in
/// <see cref="PackageStorageValidation"/>.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.SettingsV1)]
public interface IPackageSettings
{
    /// <summary>Gets a setting's stored value, or its schema default when no value is stored.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid or not a declared non-secret field.</exception>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Gets only the stored value, returning <see langword="null"/> when the schema default is effective.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid or not a declared non-secret field.</exception>
    Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Validates and stores a schema-declared, non-secret setting.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="value"/> violates the storage or settings schema contract.</exception>
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a stored value so the schema default, if any, becomes effective.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is invalid, undeclared, secret, or identifies a required field without a default.</exception>
    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);
}
