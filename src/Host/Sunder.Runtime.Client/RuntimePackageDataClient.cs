using System.Net;
using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimePackageDataClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;

    public RuntimePackageDataClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(getConnectionInfo);
        _getConnectionInfo = getConnectionInfo;
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(getConnectionInfo, innerHandler));
    }

    public Task<PackageDataValueResponse?> GetStateAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
        => GetValueAsync(packageId, "state", key, cancellationToken);

    public Task SetStateAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
        => SetValueAsync(packageId, "state", key, value, cancellationToken);

    public Task DeleteStateAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
        => DeleteValueAsync(packageId, "state", key, cancellationToken);

    public Task<IReadOnlyList<string>> ListStateKeysAsync(
        string packageId,
        string? prefix = null,
        CancellationToken cancellationToken = default)
        => ListKeysAsync(packageId, "state", prefix, cancellationToken);

    public Task<PackageDataValueResponse?> GetConfigurationAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
        => GetValueAsync(packageId, "configuration", key, cancellationToken);

    public Task<PackageDataValueResponse?> GetSecretAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
        => GetValueAsync(packageId, "secrets", key, cancellationToken);

    public Task SetSecretAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
        => SetValueAsync(packageId, "secrets", key, value, cancellationToken);

    public Task DeleteSecretAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
        => DeleteValueAsync(packageId, "secrets", key, cancellationToken);

    public async Task<byte[]?> ReadFileAsync(
        string packageId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateUri(packageId, $"files/{EscapeRelativePath(relativePath)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(
        string packageId,
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        using var requestContent = new ReadOnlyMemoryContent(contents);
        using var response = await _httpClient.PutAsync(
            CreateUri(packageId, $"files/{EscapeRelativePath(relativePath)}"),
            requestContent,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteFileAsync(
        string packageId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            CreateUri(packageId, $"files/{EscapeRelativePath(relativePath)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<PackageDataValueResponse?> GetValueAsync(
        string packageId,
        string area,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            CreateUri(packageId, $"{area}/{Uri.EscapeDataString(key)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageDataValueResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SetValueAsync(
        string packageId,
        string area,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            CreateUri(packageId, $"{area}/{Uri.EscapeDataString(key)}"),
            new SetPackageDataValueRequest(value),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task DeleteValueAsync(
        string packageId,
        string area,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(
            CreateUri(packageId, $"{area}/{Uri.EscapeDataString(key)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<IReadOnlyList<string>> ListKeysAsync(
        string packageId,
        string area,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var suffix = string.IsNullOrEmpty(prefix) ? string.Empty : $"?prefix={Uri.EscapeDataString(prefix)}";
        using var response = await _httpClient.GetAsync(CreateUri(packageId, $"{area}{suffix}"), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PackageDataKeysResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false))?.Keys ?? [];
    }

    private static string EscapeRelativePath(string relativePath)
        => string.Join('/', relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    private Uri CreateUri(string packageId, string relativePath)
    {
        var connection = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(
            RuntimeConnectionInfo.Normalize(connection.RuntimeUrl),
            $"api/v1/packages/{Uri.EscapeDataString(packageId)}/data/{relativePath}");
    }

    public void Dispose() => _httpClient.Dispose();
}
