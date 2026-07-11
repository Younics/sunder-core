using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimeManagementClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly HttpClient _httpClient;

    public RuntimeManagementClient(Uri runtimeUrl)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl))
    {
    }

    public RuntimeManagementClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null)
    {
        _getConnectionInfo = getConnectionInfo ?? throw new ArgumentNullException(nameof(getConnectionInfo));
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(getConnectionInfo, innerHandler))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token = default)
        => GetRequiredAsync<SystemStatusResponse>("system", token);

    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token = default)
        => PostAsync<object, RuntimeResetChallengeResponse>("system/reset/prepare", new { }, token);

    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token = default)
        => PostAsync<RuntimeResetConfirmRequest, RuntimeResetDrainResponse>(
            "system/reset/drain",
            new RuntimeResetConfirmRequest(challenge),
            token);

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token = default)
        => await GetRequiredAsync<IReadOnlyList<InstalledPackageDescriptor>>("packages/installed", token).ConfigureAwait(false);

    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryAuthStartRequest, RuntimeRegistryAuthStartResponse>("registry/auth/start", request, token);

    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token = default)
        => GetRequiredAsync<RuntimeRegistryAuthStatus>($"registry/auth/status?origin={Uri.EscapeDataString(origin)}", token);

    public async Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token = default)
    {
        using var response = await _httpClient.GetAsync(CreateUri($"registry/auth/sessions/{Uri.EscapeDataString(sessionId)}"), token).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await ReadRequiredAsync<RuntimeRegistryAuthSessionStatus>(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token = default)
        => PostAsync<RuntimeRegistryOriginRequest, RuntimeRegistryAuthStatus>("registry/auth/logout", new(origin), token);

    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryPackageRequest, RuntimeRegistryPackageChangeResult>("registry/packages/install", request, token);

    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryUpdateRequest, RuntimeRegistryPackageChangeResult>("registry/packages/update", request, token);

    public async Task<PackageOperationResult> ApplyLocalPackageAsync(
        string packagePath,
        string packageId,
        bool allowDowngrade,
        bool reinstall,
        CancellationToken token = default)
    {
        var installed = await GetInstalledPackagesAsync(token).ConfigureAwait(false);
        var kind = installed.Any(item => string.Equals(item.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            ? PackageStoreMutationKind.Upgrade
            : PackageStoreMutationKind.Install;
        var upload = await UploadAsync(packagePath, "uploads/packages", "application/vnd.sunder.package", token).ConfigureAwait(false);
        using var stageResponse = await _httpClient.PostAsJsonAsync(
            CreateUri("packages/store/stage"),
            new PackageStoreStageRequest([new PackageStoreMutationRequest(kind, kind == PackageStoreMutationKind.Upgrade ? packageId : null, upload.UploadId, allowDowngrade, reinstall)]),
            token).ConfigureAwait(false);
        var stage = await ReadRequiredAsync<PackageStoreStageResult>(stageResponse, token, acceptErrorPayload: true).ConfigureAwait(false);
        if (!stage.Success || stage.StageId is null)
        {
            return stage.OperationResult;
        }

        using var commitResponse = await _httpClient.PostAsync(
            CreateUri($"packages/store/stage/{Uri.EscapeDataString(stage.StageId)}/commit"),
            null,
            token).ConfigureAwait(false);
        return await ReadRequiredAsync<PackageOperationResult>(commitResponse, token, acceptErrorPayload: true).ConfigureAwait(false);
    }

    public async Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(
        string origin,
        string packagePath,
        bool setLatest,
        CancellationToken token = default)
    {
        var upload = await UploadAsync(packagePath, "uploads/packages", "application/vnd.sunder.package", token).ConfigureAwait(false);
        return await PostAsync<RuntimeRegistryPublishRequest, RegistryPublishPackageResponse>(
            "registry/packages/publish", new(origin, upload.UploadId, setLatest), token).ConfigureAwait(false);
    }

    public async Task<RegistryPublishStackResponse> PublishRegistryStackAsync(
        string origin,
        string stackPath,
        CancellationToken token = default)
    {
        var upload = await UploadAsync(stackPath, "uploads/stacks", "application/vnd.sunder.stack", token).ConfigureAwait(false);
        return await PostAsync<RuntimeRegistryPublishRequest, RegistryPublishStackResponse>(
            "registry/stacks/publish", new(origin, upload.UploadId), token).ConfigureAwait(false);
    }

    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryYankRequest, RegistryPackageManagementOperationResponse>("registry/packages/yank", request, token);

    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryDeprecateRequest, RegistryPackageManagementOperationResponse>("registry/packages/deprecate", request, token);

    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryDistTagRequest, RegistryPackageManagementOperationResponse>("registry/packages/dist-tag", request, token);

    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryDeleteStackRequest, RegistryStackManagementOperationResponse>("registry/stacks/delete", request, token);

    private async Task<ContentUploadDescriptor> UploadAsync(string filePath, string endpoint, string contentType, CancellationToken token)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        stream.Position = 0;
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = stream.Length;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = Path.GetFileName(filePath) };
        content.Headers.Add("X-Content-SHA256", hash);
        using var response = await _httpClient.PostAsync(CreateUri(endpoint), content, token).ConfigureAwait(false);
        return await ReadRequiredAsync<ContentUploadDescriptor>(response, token).ConfigureAwait(false);
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), token).ConfigureAwait(false);
        return await ReadRequiredAsync<T>(response, token).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken token)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateUri(path), request, token).ConfigureAwait(false);
        return await ReadRequiredAsync<TResponse>(response, token).ConfigureAwait(false);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken token,
        bool acceptErrorPayload = false)
    {
        if (response.IsSuccessStatusCode || acceptErrorPayload)
        {
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, token).ConfigureAwait(false);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
            }
        }

        throw await CreateExceptionAsync(response, token).ConfigureAwait(false);
    }

    private static async Task<RuntimeClientException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken token)
    {
        RuntimeProblemDetails? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<RuntimeProblemDetails>(JsonOptions, token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }

        var title = problem?.Title ?? $"Runtime request failed with HTTP {(int)response.StatusCode}";
        return new RuntimeClientException(response.StatusCode, title, problem?.Detail, problem?.Code);
    }

    private Uri CreateUri(string path)
    {
        var connection = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), $"api/v1/{path}");
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record RuntimeProblemDetails(string? Title, string? Detail, string? Code);
}
