using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Shared;

namespace Sunder.App.Services;

public sealed class RegistryApiClient : IRegistryApiClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public RegistryApiClient(Uri registryUrl, HttpClient? httpClient = null)
    {
        RegistryUrl = RegistryUrlHelper.Normalize(registryUrl);
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = RegistryUrl,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _disposeHttpClient = httpClient is null;
    }

    public Uri RegistryUrl { get; }

    public async Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/packages?skip={skip}&take={take}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&query={Uri.EscapeDataString(query.Trim())}";
        }

        return await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryPackageSummary>>(CreateRequestUri(path), cancellationToken) ?? [];
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => GetFromJsonOrNullAsync<RegistryPackageDetails>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}"),
            cancellationToken);

    public async Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/stacks?skip={skip}&take={take}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&query={Uri.EscapeDataString(query.Trim())}";
        }

        return await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryStackSummary>>(CreateRequestUri(path), cancellationToken) ?? [];
    }

    public Task<RegistryStackDetails?> GetStackAsync(
        string stackId,
        CancellationToken cancellationToken = default)
        => GetFromJsonOrNullAsync<RegistryStackDetails>(
            CreateRequestUri($"api/stacks/{Uri.EscapeDataString(stackId)}"),
            cancellationToken);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
        => GetFromJsonOrNullAsync<RegistryPackageVersionDetails>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}"),
            cancellationToken);

    public async Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
        RegistryResolveUpdatesRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri("api/packages/resolve-updates"), request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RegistryResolveUpdatesResponse>(cancellationToken: cancellationToken)
            ?? new RegistryResolveUpdatesResponse([]);
    }

    public async Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
        RegistryResolveInstallPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri("api/packages/resolve-install-plan"), request, cancellationToken);
        RegistryResolveInstallPlanResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RegistryResolveInstallPlanResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
        }

        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryResolveInstallPlanResponse(true, [], [], [], [])
            : new RegistryResolveInstallPlanResponse(false, [], [], [response.ReasonPhrase ?? "Install plan resolution failed."], []);
    }

    public async Task<RegistryCurrentUserResponse?> GetCurrentUserAsync(
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateRequestUri("api/cli-auth/me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RegistryCurrentUserResponse>(cancellationToken: cancellationToken);
    }

    public async Task<RegistryCliTokenResponse> ExchangeCliTokenAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("api/cli-auth/token"),
            new RegistryCliTokenRequest(code, codeVerifier),
            cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<RegistryCliTokenResponse>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryCliTokenResponse(false, null, null, null, ["Registry did not return an auth token."])
            : new RegistryCliTokenResponse(false, null, null, null, [response.ReasonPhrase ?? "Registry token exchange failed."]);
    }

    public async Task DownloadArtifactAsync(
        RegistryPackageArtifact artifact,
        string packageId,
        string version,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var response = await _httpClient.GetAsync(CreateRequestUri(artifact.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var fileInfo = new FileInfo(destinationPath);
        if (artifact.Size > 0 && fileInfo.Length != artifact.Size)
        {
            throw new InvalidOperationException(
                $"Downloaded package '{packageId}' {version} size mismatch. Expected {artifact.Size} bytes, got {fileInfo.Length} bytes.");
        }

        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            await using var stream = File.OpenRead(destinationPath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Downloaded package '{packageId}' {version} SHA-256 mismatch. Expected {artifact.Sha256}, got {actualHash}.");
            }
        }
    }

    public async Task DownloadStackAsync(
        RegistryStackArtifact artifact,
        string stackId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var response = await _httpClient.GetAsync(CreateRequestUri(artifact.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var fileInfo = new FileInfo(destinationPath);
        if (artifact.Size > 0 && fileInfo.Length != artifact.Size)
        {
            throw new InvalidOperationException(
                $"Downloaded Stack '{stackId}' size mismatch. Expected {artifact.Size} bytes, got {fileInfo.Length} bytes.");
        }

        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            await using var stream = File.OpenRead(destinationPath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Downloaded Stack '{stackId}' SHA-256 mismatch. Expected {artifact.Sha256}, got {actualHash}.");
            }
        }
    }

    public async Task<RegistryPublishStackResponse> PublishStackAsync(
        string stackPath,
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateRequestUri("api/stacks/publish"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        await using var stackStream = File.OpenRead(stackPath);
        using var form = new MultipartFormDataContent();
        using var stackContent = new StreamContent(stackStream);
        stackContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(stackContent, "stack", Path.GetFileName(stackPath));
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<RegistryPublishStackResponse>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryPublishStackResponse(false, null, null, [], ["Registry did not return a Stack publish response."])
            : new RegistryPublishStackResponse(false, null, null, [], [response.ReasonPhrase ?? "Registry Stack publish failed."])
            {
                Forbidden = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            };
    }

    public async Task<RegistryStackManagementOperationResponse> DeleteStackAsync(
        string stackId,
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            CreateRequestUri($"api/stacks/{Uri.EscapeDataString(stackId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<RegistryStackManagementOperationResponse>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryStackManagementOperationResponse(true, $"Deleted Stack '{stackId}'.", [])
            : new RegistryStackManagementOperationResponse(false, null, [response.ReasonPhrase ?? "Registry Stack deletion failed."])
            {
                Forbidden = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            };
    }

    private Uri CreateRequestUri(string path)
        => Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri
            : new Uri(RegistryUrl, path);

    private async Task<T?> GetFromJsonOrNullAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
