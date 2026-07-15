using System.Net;
using System.Net.Http.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

public sealed class RegistryApiClient : IRegistryApiClient
{
    private const string ApiRoot = "api/v1";
    private const long MaxJsonResponseBytes = 4L * 1024 * 1024;
    private const long MaxStackDownloadBytes = 256L * 1024 * 1024;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public RegistryApiClient(Uri registryUrl, HttpClient? httpClient = null)
    {
        RegistryUrl = RegistryUrlHelper.Normalize(registryUrl);
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = RegistryUrl,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _disposeHttpClient = httpClient is null;
    }

    public Uri RegistryUrl { get; }

    public async Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
    {
        var path = $"{ApiRoot}/packages?skip={skip}&take={take}&sort={FormatSort(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return await GetJsonAsync<IReadOnlyList<RegistryPackageSummary>>(path, cancellationToken) ?? [];
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryPackageDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}", cancellationToken);

    public async Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
    {
        var path = $"{ApiRoot}/stacks?skip={skip}&take={take}&sort={FormatSort(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return await GetJsonAsync<IReadOnlyList<RegistryStackSummary>>(path, cancellationToken) ?? [];
    }

    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryStackDetails>($"{ApiRoot}/stacks/{Uri.EscapeDataString(stackId)}", cancellationToken);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryPackageVersionDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}", cancellationToken);

    public Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default)
        => DownloadAsync(artifact.DownloadUrl, artifact.Size, artifact.Sha256, $"Stack '{stackId}'", destinationPath, cancellationToken);

    private async Task DownloadAsync(string url, long? size, string hash, string label, string destinationPath, CancellationToken cancellationToken)
    {
        if (size < 0 || size > MaxStackDownloadBytes)
        {
            throw new InvalidDataException($"Downloaded {label} exceeds the Stack download limit.");
        }

        var downloadUri = CreateDownloadUri(url);
        using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!HttpMediaUriValidator.HasSameOrigin(downloadUri, response.RequestMessage?.RequestUri))
        {
            throw new InvalidDataException($"Downloaded {label} redirected to an untrusted origin.");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxStackDownloadBytes
            || size is { } expectedSize
            && response.Content.Headers.ContentLength is { } contentLength
            && contentLength != expectedSize)
        {
            throw new InvalidDataException($"Downloaded {label} has an invalid content length.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await VerifiedFileTransfer.PublishAsync(
            source,
            destinationPath,
            MaxStackDownloadBytes,
            size,
            hash,
            $"Downloaded {label}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await BoundedHttpContentReader.ReadJsonAsync<T>(response.Content, MaxJsonResponseBytes, JsonOptions, cancellationToken);
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await BoundedHttpContentReader.ReadJsonAsync<T>(response.Content, MaxJsonResponseBytes, JsonOptions, cancellationToken);
    }

    private Uri CreateUri(string path) => Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute : new Uri(RegistryUrl, path);

    private Uri CreateDownloadUri(string path)
    {
        var uri = CreateUri(path);
        if (!HttpMediaUriValidator.IsValid(uri)
            || !HttpMediaUriValidator.HasSameOrigin(RegistryUrl, uri))
        {
            throw new InvalidDataException("Registry download URL must remain on the trusted Registry origin.");
        }

        return uri;
    }
    private static string FormatSort(RegistrySearchSort sort) => sort switch { RegistrySearchSort.Downloads => "downloads", RegistrySearchSort.Stars => "stars", _ => "updated" };

    public void Dispose()
    {
        if (_disposeHttpClient) _httpClient.Dispose();
    }
}
