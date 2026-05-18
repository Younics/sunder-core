using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class RuntimeApiClient : IRuntimeApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Func<Uri> _getRuntimeBaseUri;
    private readonly bool _disposeHttpClient;

    public RuntimeApiClient(Uri runtimeBaseUri)
        : this(() => RuntimeUrlHelper.Normalize(runtimeBaseUri)) { }

    public RuntimeApiClient(RuntimeConnectionState runtimeConnectionState)
        : this(() => runtimeConnectionState.RuntimeUrl) { }

    public RuntimeApiClient(Func<Uri> getRuntimeBaseUri, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _disposeHttpClient = httpClient is null;
        _getRuntimeBaseUri = getRuntimeBaseUri ?? throw new ArgumentNullException(nameof(getRuntimeBaseUri));
    }

    public async Task<SystemStatusResponse?> GetSystemStatusAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<SystemStatusResponse>(
            CreateRequestUri("api/system"),
            cancellationToken
        );

    public async Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                CreateRequestUri("health"),
                cancellationToken
            );
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<ActivePackageDescriptor>>(
            CreateRequestUri("api/packages/active"),
            cancellationToken
        ) ?? [];

    public async Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<SessionPackageDescriptor>>(
            CreateRequestUri("api/packages/session"),
            cancellationToken
        ) ?? [];

    public async Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<PackageSourceDescriptor>>(
            CreateRequestUri("api/packages/sources/active"),
            cancellationToken
        ) ?? [];

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<InstalledPackageDescriptor>>(
            CreateRequestUri("api/packages/installed"),
            cancellationToken
        ) ?? [];

    public async Task<PackageSessionStatus?> GetPackageSessionStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri($"api/packages/session/{Uri.EscapeDataString(packageId)}/status"),
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageSessionStatus>(cancellationToken: cancellationToken);
    }

    public async Task<PackageSessionOperationResult> LoadPackageSessionAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("api/packages/session/load"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageSessionOperationResult>(cancellationToken: cancellationToken)
               ?? PackageSessionOperationResult.Failed("Runtime returned an empty package-session load response.");
    }

    public async Task<PackageSessionOperationResult> UnloadPackageSessionAsync(
        string packageId,
        PackageSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri($"api/packages/session/{Uri.EscapeDataString(packageId)}/unload"),
            new PackageSessionUnloadRequest(sourceKind),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageSessionOperationResult>(cancellationToken: cancellationToken)
               ?? PackageSessionOperationResult.Failed("Runtime returned an empty package-session unload response.");
    }

    public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
        CreateRequestUri(
            $"api/packages/{Uri.EscapeDataString(packageId)}/assets/{EscapeRelativePath(assetPath)}"
        );

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        CancellationToken cancellationToken = default
    ) =>
        await SendPackageOperationAsync(
            () =>
                _httpClient.PostAsJsonAsync(
                    CreateRequestUri("api/packages/install/local"),
                    new PackageInstallFromPathRequest(packagePath),
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default
    ) =>
        await SendPackageOperationAsync(
            () =>
                _httpClient.PostAsJsonAsync(
                    CreateRequestUri(
                        $"api/packages/{Uri.EscapeDataString(packageId)}/upgrade/local"
                    ),
                    new PackageUpgradeFromPathRequest(packagePath, allowDowngrade, reinstall),
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<PackageOperationResult> EnableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await SendPackageOperationAsync(
            () =>
                _httpClient.PostAsync(
                    CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/enable"),
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
                    CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/disable"),
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
                    CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}"),
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<DevPackageLoadResult> LoadDevPackagesAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("api/dev/packages/load"),
            new DevPackageLoadRequest(folders),
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DevPackageLoadResult>(
                cancellationToken: cancellationToken
            )
            ?? new DevPackageLoadResult(
                [],
                [],
                ["Runtime returned an empty dev-package load response."]
            );
    }

    public async Task<DevPackageStageResult> StageDevPackagesAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("api/dev/packages/stage"),
            new DevPackageLoadRequest(folders),
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DevPackageStageResult>(
                cancellationToken: cancellationToken
            )
            ?? new DevPackageStageResult(
                null,
                [],
                [],
                [],
                ["Runtime returned an empty dev-package stage response."]
            );
    }

    public async Task<DevPackageLoadResult> CommitDevPackageStageAsync(
        string stageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"api/dev/packages/stage/{Uri.EscapeDataString(stageId)}/commit"),
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DevPackageLoadResult>(
                cancellationToken: cancellationToken
            )
            ?? new DevPackageLoadResult(
                [],
                [],
                ["Runtime returned an empty dev-package stage commit response."]
            );
    }

    public async Task DiscardDevPackageStageAsync(
        string stageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.DeleteAsync(
            CreateRequestUri($"api/dev/packages/stage/{Uri.EscapeDataString(stageId)}"),
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<
        IReadOnlyList<PackageConfigurationSchemaDescriptor>
    > GetConfigurationSchemasAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<PackageConfigurationSchemaDescriptor>>(
            CreateRequestUri("api/packages/configuration/schemas"),
            cancellationToken
        ) ?? [];

    public async Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<PackageConfigurationValuesResponse>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/config/values"),
            cancellationToken
        );

    public async Task SavePackageConfigurationValuesAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PutAsJsonAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/config/values"),
            new UpdatePackageConfigurationValuesRequest(values),
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
    }

    public async Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<PackageAuthStatusResponse>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/status"),
            cancellationToken
        );

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/start"),
            content: null,
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageAuthSessionStartResponse>(
            cancellationToken: cancellationToken
        );
    }

    public async Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
        string packageId,
        string authSessionId,
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<PackageAuthSessionStatusResponse>(
            CreateRequestUri(
                $"api/packages/{Uri.EscapeDataString(packageId)}/auth/sessions/{Uri.EscapeDataString(authSessionId)}"
            ),
            cancellationToken
        );

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/disconnect"),
            content: null,
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageAuthStatusResponse>(
            cancellationToken: cancellationToken
        );
    }

    public async Task ReportPackageFaultAsync(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/fault"),
            new ReportPackageFaultRequest(origin, message),
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri("api/system/shutdown"),
            content: null,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
    }

    private Uri CreateRequestUri(string relativePath) =>
        new(RuntimeUrlHelper.Normalize(_getRuntimeBaseUri()), relativePath);

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
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
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

    private static PackageOperationResult CreatePackageOperationSuccessResult()
        => new(true, null, RequiresAppRestart: true, [], []);

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

        return new PackageOperationResult(false, normalizedMessage, RequiresAppRestart: false, [], errors);
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
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
