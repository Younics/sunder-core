using System.Net;
using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimePackageDataClient : IDisposable
{
    private readonly RuntimeClientTransport _transport;
    private readonly bool _ownsTransport;
    private readonly RuntimeHttpResponseReader _responses;

    public RuntimePackageDataClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
        : this(new RuntimeClientTransport(getConnectionInfo, innerHandler, policy), ownsTransport: true)
    {
    }

    public RuntimePackageDataClient(RuntimeClientTransport transport)
        : this(transport, ownsTransport: false)
    {
    }

    private RuntimePackageDataClient(RuntimeClientTransport transport, bool ownsTransport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _responses = transport.Responses;
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

    public async Task<PackageSettingValueResponse?> GetSettingAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.GetAsync(
            CreatePackageUri(packageId, $"settings/{Uri.EscapeDataString(key)}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await _responses.ReadJsonAsync<PackageSettingValueResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSettingAsync(
        string packageId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.PutAsJsonAsync(
            CreatePackageUri(packageId, $"settings/{Uri.EscapeDataString(key)}"),
            new SetPackageSettingValueRequest(value),
            cancellationToken).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSettingAsync(
        string packageId,
        string key,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.DeleteAsync(
            CreatePackageUri(packageId, $"settings/{Uri.EscapeDataString(key)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

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
        using var response = await _transport.GetAsync(
            CreateUri(packageId, $"files/{EscapeRelativePath(relativePath)}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await _responses.ReadBinaryAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(
        string packageId,
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        using var requestContent = new ReadOnlyMemoryContent(contents);
        using var response = await _transport.PutAsync(
            CreateUri(packageId, $"files/{EscapeRelativePath(relativePath)}"),
            requestContent,
            cancellationToken).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(
        string packageId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.DeleteAsync(
            CreateUri(packageId, $"files/{EscapeRelativePath(relativePath)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PackageDataValueResponse?> GetValueAsync(
        string packageId,
        string area,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await _transport.GetAsync(
            CreateUri(packageId, $"{area}/{Uri.EscapeDataString(key)}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await _responses.ReadJsonAsync<PackageDataValueResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetValueAsync(
        string packageId,
        string area,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        using var response = await _transport.PutAsJsonAsync(
            CreateUri(packageId, $"{area}/{Uri.EscapeDataString(key)}"),
            new SetPackageDataValueRequest(value),
            cancellationToken).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteValueAsync(
        string packageId,
        string area,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await _transport.DeleteAsync(
            CreateUri(packageId, $"{area}/{Uri.EscapeDataString(key)}"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> ListKeysAsync(
        string packageId,
        string area,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var suffix = string.IsNullOrEmpty(prefix) ? string.Empty : $"?prefix={Uri.EscapeDataString(prefix)}";
        using var response = await _transport.GetAsync(
                CreateUri(packageId, $"{area}{suffix}"),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await _responses.ReadJsonAsync<PackageDataKeysResponse>(response, cancellationToken).ConfigureAwait(false))?.Keys ?? [];
    }

    private static string EscapeRelativePath(string relativePath)
        => string.Join('/', relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    private Uri CreateUri(string packageId, string relativePath)
        => CreatePackageUri(packageId, $"data/{relativePath}");

    private Uri CreatePackageUri(string packageId, string relativePath)
        => _transport.CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/{relativePath}");

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
