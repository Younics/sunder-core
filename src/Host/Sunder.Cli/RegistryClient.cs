using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal interface IRegistryConnection : IDisposable
{
    Uri RegistryUrl { get; }
}

internal interface IRegistryBrowseClient : IRegistryConnection
{
    Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token);
    Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token);
    Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token);
    Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token);
    Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token);
    Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token);
    Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken token);
}

internal interface IRegistryManageClient : IRegistryConnection
{
    Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token);
    Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token);
}

internal interface IRegistryClient : IRegistryBrowseClient, IRegistryManageClient;

internal sealed partial class RegistryClient : IRegistryClient
{
    private const string ApiRoot = "api/v1";
    private const long MaxJsonResponseBytes = 4L * 1024 * 1024;
    private const long MaxErrorResponseBytes = 64L * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public RegistryClient(Uri registryUrl, HttpMessageHandler? handler = null)
    {
        RegistryUrl = CliHttpUrl.Require(registryUrl, "Registry");
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = registryUrl;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Uri RegistryUrl { get; }

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
                var value = await CliHttpContentReader.ReadJsonAsync<T>(
                    response.Content, MaxJsonResponseBytes, JsonOptions, token).ConfigureAwait(false);
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
            problem = await CliHttpContentReader.ReadJsonAsync<Problem>(
                response.Content, MaxErrorResponseBytes, JsonOptions, token).ConfigureAwait(false);
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

    private Uri CreateUri(string path) => CliHttpUrl.Require(Uri.TryCreate(path, UriKind.Relative, out _) ? new Uri(RegistryUrl, path) : new Uri(path, UriKind.Absolute), "Registry request");
    public void Dispose() => _httpClient.Dispose();
    private sealed record Problem(string? Title, string? Detail, string? Code);
}
