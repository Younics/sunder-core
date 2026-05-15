using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Shared;

namespace Sunder.App.Services;

public sealed class RegistryApiClient : IRegistryApiClient
{
    private readonly HttpClient _httpClient;

    public RegistryApiClient(Uri registryUrl)
    {
        RegistryUrl = RegistryUrlHelper.Normalize(registryUrl);
        _httpClient = new HttpClient
        {
            BaseAddress = RegistryUrl,
            Timeout = TimeSpan.FromSeconds(30),
        };
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

        return await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryPackageSummary>>(path, cancellationToken) ?? [];
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => GetFromJsonOrNullAsync<RegistryPackageDetails>(
            $"api/packages/{Uri.EscapeDataString(packageId)}",
            cancellationToken);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
        => GetFromJsonOrNullAsync<RegistryPackageVersionDetails>(
            $"api/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}",
            cancellationToken);

    public async Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
        RegistryResolveUpdatesRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/packages/resolve-updates", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RegistryResolveUpdatesResponse>(cancellationToken: cancellationToken)
            ?? new RegistryResolveUpdatesResponse([]);
    }

    public async Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
        RegistryResolveInstallPlanRequest request,
        CancellationToken cancellationToken = default)
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

    public async Task DownloadArtifactAsync(
        RegistryPackageArtifact artifact,
        string packageId,
        string version,
        string destinationPath,
        CancellationToken cancellationToken = default)
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
