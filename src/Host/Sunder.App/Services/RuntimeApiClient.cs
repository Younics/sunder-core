using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient : IRuntimeApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxSseEventCharacters = 256 * 1024;
    private const long MaxOperationResponseBytes = 4L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly RuntimeHttpResponseReader _responses;

    public RuntimeApiClient(Uri runtimeBaseUri)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeBaseUri)) { }

    public RuntimeApiClient(RuntimeConnectionState runtimeConnectionState)
        : this(() => runtimeConnectionState.ConnectionInfo) { }

    internal RuntimeApiClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null)
    {
        var policy = new RuntimeClientPolicyOptions();
        _getConnectionInfo = getConnectionInfo ?? throw new ArgumentNullException(nameof(getConnectionInfo));
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(_getConnectionInfo, innerHandler, policy))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _responses = new RuntimeHttpResponseReader(policy);
    }

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        CancellationToken cancellationToken = default
    )
    {
        var upload = await UploadPackageAsync(packagePath, cancellationToken);
        var stage = await StagePackageStoreChangesAsync(
            new PackageStoreStageRequest([new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: upload.UploadId)]),
            cancellationToken);
        return stage.StageId is null ? stage.OperationResult : await CommitPackageStoreStageAsync(stage.StageId, cancellationToken);
    }

    public async Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default
    )
    {
        var upload = await UploadPackageAsync(packagePath, cancellationToken);
        var stage = await StagePackageStoreChangesAsync(
            new PackageStoreStageRequest([new PackageStoreMutationRequest(
                PackageStoreMutationKind.Upgrade,
                packageId,
                upload.UploadId,
                allowDowngrade,
                reinstall)]),
            cancellationToken);
        return stage.StageId is null ? stage.OperationResult : await CommitPackageStoreStageAsync(stage.StageId, cancellationToken);
    }

    public async Task<PackageOperationResult> EnableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await SendPackageOperationAsync(
            () =>
                _httpClient.PostAsync(
                    CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/enable"),
                    content: null,
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<PackageOperationResult> DisableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await SendPackageOperationAsync(
            () =>
                _httpClient.PostAsync(
                    CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/disable"),
                    content: null,
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<PackageOperationResult> UninstallPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await SendPackageOperationAsync(
            () =>
                _httpClient.DeleteAsync(
                    CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}"),
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
        PackageStoreStageRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("packages/store/stage"),
            request,
            cancellationToken);

        var result = await ReadPackageStoreStageResultAsync(response, cancellationToken);
        if (result is not null)
        {
            return response.IsSuccessStatusCode || !result.Success
                ? result
                : PackageStoreStageResult.Failed(CreatePackageOperationFailureResult(response).Message ?? "Package store stage failed.");
        }

        return response.IsSuccessStatusCode
            ? PackageStoreStageResult.Failed("Runtime returned an empty package-store stage response.")
            : PackageStoreStageResult.Failed(CreatePackageOperationFailureResult(response).Message ?? "Package store stage failed.");
    }

    public async Task<PackageOperationResult> CommitPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.PostAsync(
                CreateRequestUri($"packages/store/stage/{Uri.EscapeDataString(stageId)}/commit"),
                content: null,
                cancellationToken),
            cancellationToken);

    public async Task DiscardPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            CreateRequestUri($"packages/store/stage/{Uri.EscapeDataString(stageId)}"),
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(
        PackageLifecycleLoadRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("packages/session/load-batch"),
            request,
            cancellationToken
        );

        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageLifecycleOperationResult>(response, cancellationToken)
            ?? PackageLifecycleOperationResult.Failed("Runtime returned an empty package lifecycle load response.");
    }

    public async Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.PostAsJsonAsync(
                CreateRequestUri("packages/session/reload-installed"),
                new InstalledPackageSessionReloadRequest(impactedPackageIds),
                cancellationToken),
            cancellationToken);

    public async Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(
        PackageLifecycleStageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("packages/session/stage"),
            request,
            cancellationToken
        );

        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageLifecycleStageResult>(response, cancellationToken)
            ?? PackageLifecycleStageResult.Failed("Runtime returned an empty package lifecycle stage response.");
    }

    public async Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(
        string stageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"packages/session/stage/{Uri.EscapeDataString(stageId)}/commit"),
            content: null,
            cancellationToken);

        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageLifecycleOperationResult>(response, cancellationToken)
            ?? PackageLifecycleOperationResult.Failed("Runtime returned an empty package lifecycle stage commit response.");
    }

    public async Task DiscardPackageLifecycleStageAsync(
        string stageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.DeleteAsync(
            CreateRequestUri($"packages/session/stage/{Uri.EscapeDataString(stageId)}"),
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken);
    }

    private async IAsyncEnumerable<T> ReadSseAsync<T>(
        string endpoint,
        long afterSequenceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CreateRequestUri($"{endpoint}?after={Math.Max(0, afterSequenceId)}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (afterSequenceId > 0)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", afterSequenceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: false);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return JsonSerializer.Deserialize<T>(data.ToString(), JsonOptions)
                                 ?? throw new InvalidDataException("Runtime stream returned an empty event.");
                    data.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line.AsSpan(5).TrimStart();
            if (data.Length + value.Length > MaxSseEventCharacters)
            {
                throw new InvalidDataException("Runtime stream event exceeded the client parser limit.");
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(value);
        }
    }

    private async Task<T?> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri(relativePath),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    private Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        => _responses.ReadJsonAsync<T>(response, cancellationToken);

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return await _responses.ReadRequiredJsonAsync<T>(response, cancellationToken);
    }

    private async Task<ContentUploadDescriptor> UploadFileAsync(
        string filePath,
        string endpoint,
        string contentType,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        stream.Position = 0;
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = stream.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = Path.GetFileName(filePath) };
        content.Headers.Add("X-Content-SHA256", hash);
        using var response = await _httpClient.PostAsync(CreateRequestUri(endpoint), content, cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<ContentUploadDescriptor>(response, cancellationToken)
               ?? throw new InvalidDataException("Runtime returned an empty upload response.");
    }

    private Uri CreateRequestUri(string relativePath)
    {
        var connectionInfo = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(RuntimeUrlHelper.Normalize(connectionInfo.RuntimeUrl), $"api/v1/{relativePath}");
    }

    private static string EscapeRelativePath(string path) =>
        string.Join(
            '/',
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString)
        );

    private async Task<PackageOperationResult> SendPackageOperationAsync(
        Func<Task<HttpResponseMessage>> sendAsync,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var response = await sendAsync();
            var result = await ReadPackageOperationResultAsync(response, cancellationToken);
            if (result is not null)
            {
                return response.IsSuccessStatusCode || !result.Success
                    ? result
                    : CreatePackageOperationFailureResult(response);
            }

            return response.IsSuccessStatusCode
                ? CreatePackageOperationSuccessResult()
                : CreatePackageOperationFailureResult(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return CreatePackageOperationFailureResult("Runtime package operation timed out.", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CreatePackageOperationFailureResult("Runtime package operation request failed.", ex.Message);
        }
    }

    private static async Task<PackageOperationResult?> ReadPackageOperationResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await BoundedHttpContentReader.ReadStringAsync(
            response.Content,
            MaxOperationResponseBytes,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PackageOperationResult>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            AppSessionLog.WriteError("Failed to parse a runtime package operation response.", ex);
            return response.IsSuccessStatusCode
                ? CreatePackageOperationFailureResult("Runtime returned an invalid package operation response.", ex.Message)
                : CreatePackageOperationFailureResult(response, content);
        }
    }

    private static async Task<PackageStoreStageResult?> ReadPackageStoreStageResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await BoundedHttpContentReader.ReadStringAsync(
            response.Content,
            MaxOperationResponseBytes,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PackageStoreStageResult>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            AppSessionLog.WriteError("Failed to parse a runtime package-store stage response.", ex);
            return PackageStoreStageResult.Failed(response.IsSuccessStatusCode
                ? "Runtime returned an invalid package-store stage response."
                : CreatePackageOperationFailureResult(response, content).Message ?? "Package store stage failed.");
        }
    }

    private static PackageOperationResult CreatePackageOperationSuccessResult()
        => new(true, null, RuntimeSessionApplied: true, RequiresAppRestart: false, [], []);

    private static PackageOperationResult CreatePackageOperationFailureResult(HttpResponseMessage response, string? responseBody = null)
    {
        var statusText = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? ((int)response.StatusCode).ToString()
            : $"{(int)response.StatusCode} {response.ReasonPhrase}";
        var message = $"Runtime package operation failed with HTTP {statusText}.";
        var body = NormalizeResponseBody(responseBody);
        return string.IsNullOrWhiteSpace(body)
            ? CreatePackageOperationFailureResult(message)
            : CreatePackageOperationFailureResult($"{message} {body}");
    }

    private static PackageOperationResult CreatePackageOperationFailureResult(string message, string? detail = null)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Package operation failed." : message.Trim();
        var normalizedDetail = NormalizeResponseBody(detail);
        IReadOnlyList<string> errors = string.IsNullOrWhiteSpace(normalizedDetail)
            || string.Equals(normalizedDetail, normalizedMessage, StringComparison.Ordinal)
                ? [normalizedMessage]
                : [normalizedMessage, normalizedDetail];

        return new PackageOperationResult(false, normalizedMessage, RuntimeSessionApplied: false, RequiresAppRestart: false, [], errors);
    }

    private static string? NormalizeResponseBody(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var trimmed = responseBody.Trim();
        return trimmed.Length <= 500 ? trimmed : $"{trimmed[..500]}...";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
