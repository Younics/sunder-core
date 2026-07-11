using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides a thread-safe snapshot-backed view of host-owned package configuration.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ConfigurationValuesV1)]
public interface IPackageConfiguration
{
    /// <summary>Gets a case-sensitive configuration key, returning <see langword="null"/> when absent.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);
}
