using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Shared;

namespace Sunder.Cli;

internal sealed class RegistryClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public RegistryClient(Uri registryUrl, TimeSpan timeout)
    {
        RegistryUrl = registryUrl;
        _timeout = timeout;
        _httpClient = new HttpClient { BaseAddress = registryUrl, Timeout = timeout };
    }

    public Uri RegistryUrl { get; }

    public async Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var path = $"api/packages?skip={skip}&take={take}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&query={Uri.EscapeDataString(query)}";
        }

        return await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryPackageSummary>>(path, cancellationToken) ?? [];
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken)
        => GetFromJsonOrNullAsync<RegistryPackageDetails>($"api/packages/{Uri.EscapeDataString(packageId)}", cancellationToken);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken)
        => GetFromJsonOrNullAsync<RegistryPackageVersionDetails>(
            $"api/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}",
            cancellationToken);

    public Task<RegistryPackageResolveResponse?> ResolveAsync(
        string packageId,
        string tag,
        CancellationToken cancellationToken)
        => GetFromJsonOrNullAsync<RegistryPackageResolveResponse>(
            $"api/packages/{Uri.EscapeDataString(packageId)}/resolve?tag={Uri.EscapeDataString(tag)}",
            cancellationToken);

    public async Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
        RegistryResolveUpdatesRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/packages/resolve-updates", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RegistryResolveUpdatesResponse>(cancellationToken: cancellationToken)
            ?? new RegistryResolveUpdatesResponse([]);
    }

    public async Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
        RegistryResolveInstallPlanRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/packages/resolve-install-plan", request, cancellationToken);
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

    public async Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(
        string packagePath,
        bool setLatest,
        CancellationToken cancellationToken)
    {
        return await RunPublishRequestAsync(async () =>
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "api/dev/packages/publish/local",
                new RegistryPublishLocalPackageRequest(packagePath, setLatest),
                cancellationToken);

            return await ReadPublishResponseAsync(
                response,
                "Development publish endpoint was not found. Start the registry in Development or use the authenticated publish endpoint.",
                cancellationToken);
        }, cancellationToken);
    }

    public async Task<RegistryPublishPackageResponse> PublishPackageAsync(
        string packagePath,
        bool setLatest,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        return await RunPublishRequestAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/packages/publish");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var content = new MultipartFormDataContent();
            await using var packageStream = File.OpenRead(packagePath);
            using var packageContent = new StreamContent(packageStream);
            packageContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(packageContent, "package", Path.GetFileName(packagePath));
            content.Add(new StringContent(setLatest ? "true" : "false"), "setLatest");
            request.Content = content;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return await ReadPublishResponseAsync(
                response,
                "Authenticated publish endpoint was not found on this registry.",
                cancellationToken);
        }, cancellationToken);
    }

    public async Task<RegistryPackageManagementOperationResponse> SetVersionYankedAsync(
        string packageId,
        string version,
        bool isYanked,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedJsonRequest(
            HttpMethod.Put,
            $"api/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}/yank",
            new RegistrySetPackageVersionYankRequest(isYanked),
            bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadManagementResponseAsync(response, cancellationToken);
    }

    public async Task<RegistryPackageManagementOperationResponse> SetVersionDeprecationAsync(
        string packageId,
        string version,
        string? message,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedJsonRequest(
            HttpMethod.Put,
            $"api/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}/deprecation",
            new RegistryDeprecatePackageVersionRequest(message),
            bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadManagementResponseAsync(response, cancellationToken);
    }

    public Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken cancellationToken)
        => GetFromJsonOrNullAsync<RegistryPackageDistTagsResponse>(
            $"api/packages/{Uri.EscapeDataString(packageId)}/dist-tags",
            cancellationToken);

    public async Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(
        string packageId,
        string tag,
        string version,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedJsonRequest(
            HttpMethod.Put,
            $"api/packages/{Uri.EscapeDataString(packageId)}/dist-tags/{Uri.EscapeDataString(tag)}",
            new RegistrySetPackageDistTagRequest(version),
            bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadManagementResponseAsync(response, cancellationToken);
    }

    public async Task<RegistryPackageManagementOperationResponse> DeleteDistTagAsync(
        string packageId,
        string tag,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/packages/{Uri.EscapeDataString(packageId)}/dist-tags/{Uri.EscapeDataString(tag)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadManagementResponseAsync(response, cancellationToken);
    }

    public async Task<RegistryCliTokenResponse> ExchangeCliTokenAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/cli-auth/token",
            new RegistryCliTokenRequest(code, codeVerifier),
            cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<RegistryCliTokenResponse>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryCliTokenResponse(false, null, null, null, ["Registry did not return a CLI token."])
            : new RegistryCliTokenResponse(false, null, null, null, [response.ReasonPhrase ?? "CLI token exchange failed."]);
    }

    public async Task<RegistryCurrentUserResponse?> GetCurrentUserAsync(string bearerToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/cli-auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RegistryCurrentUserResponse>(cancellationToken: cancellationToken);
    }

    public async Task DownloadArtifactAsync(
        RegistryPackageArtifact artifact,
        string packageId,
        string version,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var response = await _httpClient.GetAsync(artifact.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

    private async Task<T?> GetFromJsonOrNullAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private static HttpRequestMessage CreateAuthorizedJsonRequest<T>(
        HttpMethod method,
        string path,
        T body,
        string bearerToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<RegistryPublishPackageResponse> ReadPublishResponseAsync(
        HttpResponseMessage response,
        string notFoundMessage,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new RegistryPublishPackageResponse(false, null, null, null, [], ["Authentication failed. Set SUNDER_REGISTRY_TOKEN or pass --token."]);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new RegistryPublishPackageResponse(false, null, null, null, [], ["Authenticated user is not allowed to publish packages."]);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new RegistryPublishPackageResponse(false, null, null, null, [], [notFoundMessage]);
        }

        RegistryPublishPackageResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RegistryPublishPackageResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
        }

        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryPublishPackageResponse(true, null, null, "Package published.", [], [])
            : new RegistryPublishPackageResponse(false, null, null, null, [], [response.ReasonPhrase ?? "Package publish failed."]);
    }

    private async Task<RegistryPublishPackageResponse> RunPublishRequestAsync(
        Func<Task<RegistryPublishPackageResponse>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliTimeoutException("Registry publish", _timeout, ex);
        }
    }

    private static async Task<RegistryPackageManagementOperationResponse> ReadManagementResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new RegistryPackageManagementOperationResponse(false, null, ["Authentication failed. Set SUNDER_REGISTRY_TOKEN or pass --token."]);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new RegistryPackageManagementOperationResponse(false, null, ["Authenticated user is not allowed to manage this package."])
            {
                Forbidden = true,
            };
        }

        RegistryPackageManagementOperationResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RegistryPackageManagementOperationResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
        }

        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new RegistryPackageManagementOperationResponse(true, "Package management operation completed.", [])
            : new RegistryPackageManagementOperationResponse(false, null, [response.ReasonPhrase ?? "Package management operation failed."]);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
