using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal interface IRegistryClient : IDisposable
{
    Uri RegistryUrl { get; }
    Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token);
    Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token);
    Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token);
    Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token);
    Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token);
    Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token);
    Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token);
    Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token);
    Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken token);
}

internal sealed class RegistryClient : IRegistryClient
{
    private const string ApiRoot = "api/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public RegistryClient(Uri registryUrl, HttpMessageHandler? handler = null)
    {
        RegistryUrl = registryUrl;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = registryUrl;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Uri RegistryUrl { get; }

    public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token)
    {
        var path = $"{ApiRoot}/packages?skip={skip}&take={take}" + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&query={Uri.EscapeDataString(query.Trim())}");
        return GetRequiredAsync<IReadOnlyList<RegistryPackageSummary>>(path, token);
    }

    public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token)
    {
        var path = $"{ApiRoot}/stacks?skip={skip}&take={take}" + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&query={Uri.EscapeDataString(query.Trim())}");
        return GetRequiredAsync<IReadOnlyList<RegistryStackSummary>>(path, token);
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token)
        => GetOrNullAsync<RegistryPackageDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}", token);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token)
        => GetOrNullAsync<RegistryPackageVersionDetails>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(version)}", token);

    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token)
        => GetOrNullAsync<RegistryStackDetails>($"{ApiRoot}/stacks/{Uri.EscapeDataString(stackId)}", token);

    public Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token)
        => GetOrNullAsync<RegistryPackageDistTagsResponse>($"{ApiRoot}/packages/{Uri.EscapeDataString(packageId)}/dist-tags", token);

    public Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token)
        => PostAsync<RegistryPublishLocalPackageRequest, RegistryPublishPackageResponse>(
            $"{ApiRoot}/dev/packages/publish/local", new(packagePath, setLatest), token, acceptErrorPayload: true);

    public Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token)
        => PostAsync<RegistryPublishLocalStackRequest, RegistryPublishStackResponse>(
            $"{ApiRoot}/dev/stacks/publish/local", new(stackPath), token, acceptErrorPayload: true);

    public async Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await _httpClient.GetAsync(CreateUri(artifact.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            }

            var length = new FileInfo(temporaryPath).Length;
            if (artifact.Size > 0 && length != artifact.Size) throw new InvalidDataException($"Downloaded Stack '{stackId}' size mismatch.");
            if (!string.IsNullOrWhiteSpace(artifact.Sha256))
            {
                await using var stream = File.OpenRead(temporaryPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Downloaded Stack '{stackId}' SHA-256 mismatch.");
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), token).ConfigureAwait(false);
        return await ReadRequiredAsync<T>(response, token).ConfigureAwait(false);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        return await ReadRequiredAsync<T>(response, token).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken token, bool acceptErrorPayload)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateUri(path), request, token).ConfigureAwait(false);
        return await ReadRequiredAsync<TResponse>(response, token, acceptErrorPayload).ConfigureAwait(false);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken token, bool acceptErrorPayload = false)
    {
        if (response.IsSuccessStatusCode || acceptErrorPayload)
        {
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, token).ConfigureAwait(false);
                if (value is not null) return value;
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
            }
        }
        throw await CreateExceptionAsync(response, token).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw await CreateExceptionAsync(response, token).ConfigureAwait(false);
    }

    private static async Task<CliHttpException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken token)
    {
        Problem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<Problem>(JsonOptions, token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }
        return new CliHttpException(
            response.StatusCode,
            problem?.Title ?? $"Registry request failed with HTTP {(int)response.StatusCode}",
            problem?.Detail,
            problem?.Code);
    }

    private Uri CreateUri(string path) => Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute : new Uri(RegistryUrl, path);
    public void Dispose() => _httpClient.Dispose();
    private sealed record Problem(string? Title, string? Detail, string? Code);
}
