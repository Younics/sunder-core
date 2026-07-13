using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides validated access to the package's schema-declared, non-secret settings.</summary>
/// <remarks>
/// Settings are persisted independently from <see cref="IPackageStorageContext.State"/>. Secret schema fields must be
/// accessed through <see cref="IPackageSecrets"/> instead.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.SettingsV1)]
public interface IPackageSettings
{
    /// <summary>Gets a setting's stored value, or its schema default when no value is stored.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Gets only the stored value, returning <see langword="null"/> when the schema default is effective.</summary>
    Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Validates and stores a schema-declared, non-secret setting.</summary>
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a stored value so the schema default, if any, becomes effective.</summary>
    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);
}
