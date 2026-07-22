using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken cancellationToken = default)
        => _management.GetPackageSettingsSchemasAsync(cancellationToken);

    public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken cancellationToken = default)
        => _management.GetPackageSettingsValuesAsync(packageId, cancellationToken);

    public Task SavePackageSettingsValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
        => _management.SavePackageSettingsValuesAsync(packageId, values, cancellationToken);

    public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default)
        => GetPackageAuthStatusCoreAsync(packageId, cancellationToken);

    private async Task<PackageAuthStatusResponse?> GetPackageAuthStatusCoreAsync(string packageId, CancellationToken cancellationToken)
        => await _management.GetPackageAuthStatusAsync(packageId, cancellationToken).ConfigureAwait(false);

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
        => await _management.StartPackageAuthAsync(packageId, cancellationToken).ConfigureAwait(false);

    public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken cancellationToken = default)
        => GetPackageAuthSessionStatusCoreAsync(packageId, authSessionId, cancellationToken);

    private async Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusCoreAsync(
        string packageId,
        string authSessionId,
        CancellationToken cancellationToken)
        => await _management.GetPackageAuthSessionStatusAsync(packageId, authSessionId, cancellationToken).ConfigureAwait(false);

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
        => await _management.DisconnectPackageAuthAsync(packageId, cancellationToken).ConfigureAwait(false);
}
