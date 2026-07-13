using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public async Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<PackageConfigurationSchemaDescriptor>>("packages/settings/schemas", cancellationToken) ?? [];

    public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken cancellationToken = default)
        => GetJsonAsync<PackageSettingsValuesResponse>($"packages/{Uri.EscapeDataString(packageId)}/settings/values", cancellationToken);

    public async Task SavePackageSettingsValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/settings/values"),
            new UpdatePackageSettingsRequest(values),
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default)
        => GetJsonAsync<PackageAuthStatusResponse>($"packages/{Uri.EscapeDataString(packageId)}/auth/status", cancellationToken);

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/auth/start"),
            content: null,
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageAuthSessionStartResponse>(response, cancellationToken);
    }

    public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken cancellationToken = default)
        => GetJsonAsync<PackageAuthSessionStatusResponse>(
            $"packages/{Uri.EscapeDataString(packageId)}/auth/sessions/{Uri.EscapeDataString(authSessionId)}",
            cancellationToken);

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/auth/disconnect"),
            content: null,
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageAuthStatusResponse>(response, cancellationToken);
    }

    public async Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message, CancellationToken cancellationToken = default)
    {
        var status = await GetPackageSessionStatusAsync(packageId, cancellationToken);
        if (status is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/fault"),
            new ReportPackageFaultRequest(origin, message, status.GenerationId),
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
    }
}
