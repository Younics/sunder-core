using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal interface IRegistryConnection : IDisposable
{
    Uri RegistryApiUrl { get; }
}

internal interface IRegistryBrowseClient : IRegistryConnection
{
    Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token);
    Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token);
    Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token);
    Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token);
    Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token);
    Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token);
    Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, bool force, CancellationToken token);
}

internal interface IRegistryManageClient : IRegistryConnection
{
    Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token);
    Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token);
    Task<RegistryPublishPackageResponse> PublishPackageAsync(
        string packagePath,
        bool setLatest,
        RegistryPublishCredential credential,
        CancellationToken token);
    Task<RegistryPublishStackResponse> PublishStackAsync(
        string stackPath,
        RegistryPublishCredential credential,
        CancellationToken token);
}

internal interface IRegistryClient : IRegistryBrowseClient, IRegistryManageClient;

internal sealed partial class RegistryClient : IRegistryClient
{
    private const string ApiRoot = "api/v1";
    private const long MaxJsonResponseBytes = 4L * 1024 * 1024;
    private const long MaxErrorResponseBytes = 64L * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public RegistryClient(Uri registryApiUrl, HttpMessageHandler? handler = null)
    {
        RegistryApiUrl = CliHttpUrl.RequireBase(registryApiUrl, "Registry");
        _httpClient = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _httpClient.BaseAddress = RegistryApiUrl;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Uri RegistryApiUrl { get; }

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
        if (!response.IsSuccessStatusCode
            && (!acceptErrorPayload || IsProblemDetails(response) || IsCanonicalHttpFailure(response.StatusCode)))
        {
            throw await CreateExceptionAsync(response, token).ConfigureAwait(false);
        }

        try
        {
            var value = await CliHttpContentReader.ReadJsonAsync<T>(
                response.Content, MaxJsonResponseBytes, JsonOptions, token).ConfigureAwait(false);
            if (value is null || HasMalformedRequiredCollections(value))
            {
                throw new InvalidDataException(
                    $"Registry returned a malformed response for HTTP {(int)response.StatusCode}.");
            }
            return value;
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, token).ConfigureAwait(false);
        }
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
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
        }
        var correlationId = problem?.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId)
            && response.Headers.TryGetValues("X-Correlation-ID", out var values))
        {
            correlationId = values.FirstOrDefault();
        }
        return new CliHttpException(
            response.StatusCode,
            problem?.Title ?? $"Registry request failed with HTTP {(int)response.StatusCode}",
            problem?.Detail,
            problem?.Code,
            correlationId);
    }

    private static bool IsCanonicalHttpFailure(HttpStatusCode statusCode)
        => (int)statusCode is 401 or 403 or 413 or 429;

    private static bool IsProblemDetails(HttpResponseMessage response)
        => string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "application/problem+json",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasMalformedRequiredCollections(object value)
        => value switch
        {
            IReadOnlyList<RegistryPackageSummary> packages => packages.Any(package => package is null),
            IReadOnlyList<RegistryStackSummary> stacks => stacks.Any(stack => stack is null),
            RegistryPackageDetails package => package.Versions is null || package.Versions.Any(version => version is null),
            RegistryPackageVersionDetails version => version.DependsOn is null
                                                     || version.DependsOn.Any(item => item is null)
                                                     || version.Targets is null
                                                     || version.Targets.Any(item => item is null)
                                                     || version.Projections is null
                                                     || version.Projections.Any(item => item is null)
                                                     || version.ContractBundles is null
                                                     || version.ContractBundles.Any(item => item is null)
                                                     || version.UsesContracts is null
                                                     || version.UsesContracts.Any(item => item is null)
                                                     || version.Providers is null
                                                     || version.Providers.Any(item => item is null)
                                                     || version.TrustedArtifactOrigins is null,
            RegistryStackDetails stack => stack.Packages is null
                                          || stack.Packages.Any(item => item is null)
                                          || stack.Fragments is null
                                          || stack.Fragments.Any(item => item is null)
                                          || stack.RequiredInputs is null
                                          || stack.RequiredInputs.Any(item => item is null),
            RegistryPackageDistTagsResponse tags => tags.DistTags is null || tags.DistTags.Any(tag => tag is null),
            RegistryPublishPackageResponse packagePublish => packagePublish.Warnings is null || packagePublish.Errors is null,
            RegistryPublishStackResponse stackPublish => stackPublish.Warnings is null || stackPublish.Errors is null,
            _ => false,
        };

    private Uri CreateUri(string path) => RegistryTransportSecurity.CreateUri(RegistryApiUrl, path);
    public void Dispose() => _httpClient.Dispose();
    private sealed record Problem(string? Title, string? Detail, string? Code, string? CorrelationId);
}
