using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Sunder.Registry.Contracts;

namespace Sunder.App.Services;

public sealed class RegistryApiClient : IRegistryApiClient
{
    private const string ApiRoot = "api/v1";
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public RegistryApiClient(Uri registryUrl, HttpClient? httpClient = null)
    {
        RegistryUrl = RegistryUrlHelper.Normalize(registryUrl);
        _httpClient = httpClient ?? new HttpClient { BaseAddress = RegistryUrl, Timeout = TimeSpan.FromSeconds(30) };
        _disposeHttpClient = httpClient is null;
    }

    public Uri RegistryUrl { get; }

    public async Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
    {
        var path = $"{ApiRoot}/packages?skip={skip}&take={take}&sort={FormatSort(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryPackageSummary>>(CreateUri(path), cancellationToken) ?? [];
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryPackageDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}", cancellationToken);

    public async Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
    {
        var path = $"{ApiRoot}/stacks?skip={skip}&take={take}&sort={FormatSort(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<RegistryStackSummary>>(CreateUri(path), cancellationToken) ?? [];
    }

    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryStackDetails>($"{ApiRoot}/stacks/{Uri.EscapeDataString(stackId)}", cancellationToken);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryPackageVersionDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}", cancellationToken);

    public Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default)
        => DownloadAsync(artifact.DownloadUrl, artifact.Size, artifact.Sha256, $"Stack '{stackId}'", destinationPath, cancellationToken);

    private async Task DownloadAsync(string url, long size, string hash, string label, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var response = await _httpClient.GetAsync(CreateUri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var actualSize = new FileInfo(destinationPath).Length;
        if (size > 0 && actualSize != size) throw new InvalidDataException($"Downloaded {label} size mismatch.");
        if (!string.IsNullOrWhiteSpace(hash))
        {
            await using var stream = File.OpenRead(destinationPath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Downloaded {label} SHA-256 mismatch.");
        }
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private Uri CreateUri(string path) => Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute : new Uri(RegistryUrl, path);
    private static string FormatSort(RegistrySearchSort sort) => sort switch { RegistrySearchSort.Downloads => "downloads", RegistrySearchSort.Stars => "stars", _ => "updated" };

    public void Dispose()
    {
        if (_disposeHttpClient) _httpClient.Dispose();
    }
}
