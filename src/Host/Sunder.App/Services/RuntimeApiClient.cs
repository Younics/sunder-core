using System.Net.Http.Json;
using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class RuntimeApiClient : IRuntimeApiClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<Uri> _getRuntimeBaseUri;

    public RuntimeApiClient(Uri runtimeBaseUri)
        : this(() => RuntimeUrlHelper.Normalize(runtimeBaseUri))
    {
    }

    public RuntimeApiClient(RuntimeConnectionState runtimeConnectionState)
        : this(() => runtimeConnectionState.RuntimeUrl)
    {
    }

    public RuntimeApiClient(Func<Uri> getRuntimeBaseUri)
    {
        _httpClient = new HttpClient();
        _getRuntimeBaseUri = getRuntimeBaseUri;
    }

    public async Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<SystemStatusResponse>(CreateRequestUri("api/system"), cancellationToken);

    public async Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(CreateRequestUri("health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<ActivePackageDescriptor>>(CreateRequestUri("api/packages/active"), cancellationToken) ?? [];

    public async Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<SessionPackageDescriptor>>(CreateRequestUri("api/packages/session"), cancellationToken) ?? [];

    public async Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<PackageSourceDescriptor>>(CreateRequestUri("api/packages/sources/active"), cancellationToken) ?? [];

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<InstalledPackageDescriptor>>(CreateRequestUri("api/packages/installed"), cancellationToken) ?? [];

    public Uri CreatePackageAssetUri(string packageId, string assetPath)
        => CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/assets/{EscapeRelativePath(assetPath)}");

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.PostAsJsonAsync(
                CreateRequestUri("api/packages/install/local"),
                new PackageInstallFromPathRequest(packagePath),
                cancellationToken),
            cancellationToken);

    public async Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.PostAsJsonAsync(
                CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/upgrade/local"),
                new PackageUpgradeFromPathRequest(packagePath, allowDowngrade, reinstall),
                cancellationToken),
            cancellationToken);

    public async Task<PackageOperationResult> EnableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.PostAsync(
                CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/enable"),
                content: null,
                cancellationToken),
            cancellationToken);

    public async Task<PackageOperationResult> DisableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.PostAsync(
                CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/disable"),
                content: null,
                cancellationToken),
            cancellationToken);

    public async Task<PackageOperationResult> UninstallPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await SendPackageOperationAsync(
            () => _httpClient.DeleteAsync(
                CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}"),
                cancellationToken),
            cancellationToken);

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
        return await response.Content.ReadFromJsonAsync<DevPackageLoadResult>(cancellationToken: cancellationToken)
            ?? new DevPackageLoadResult([], [], ["Runtime returned an empty dev-package load response."]);
    }

    public async Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<PackageConfigurationSchemaDescriptor>>(
            CreateRequestUri("api/packages/configuration/schemas"),
            cancellationToken
        ) ?? [];

    public async Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<PackageConfigurationValuesResponse>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/config/values"),
            cancellationToken
        );

    public async Task SavePackageConfigurationValuesAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<PackageAuthStatusResponse>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/status"),
            cancellationToken
        );

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/start"),
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageAuthSessionStartResponse>(cancellationToken: cancellationToken);
    }

    public async Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
        string packageId,
        string authSessionId,
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<PackageAuthSessionStatusResponse>(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/sessions/{Uri.EscapeDataString(authSessionId)}"),
            cancellationToken
        );

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/auth/disconnect"),
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageAuthStatusResponse>(cancellationToken: cancellationToken);
    }

    public async Task ReportPackageFaultAsync(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri($"api/packages/{Uri.EscapeDataString(packageId)}/fault"),
            new ReportPackageFaultRequest(origin, message),
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(CreateRequestUri("api/system/shutdown"), content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Uri CreateRequestUri(string relativePath)
        => new(RuntimeUrlHelper.Normalize(_getRuntimeBaseUri()), relativePath);

    private static string EscapeRelativePath(string path)
        => string.Join(
            '/',
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));

    private async Task<PackageOperationResult> SendPackageOperationAsync(
        Func<Task<HttpResponseMessage>> sendAsync,
        CancellationToken cancellationToken)
    {
        using var response = await sendAsync();
        var result = await response.Content.ReadFromJsonAsync<PackageOperationResult>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new PackageOperationResult(true, null, RequiresAppRestart: true, [], [])
            : new PackageOperationResult(false, response.ReasonPhrase, RequiresAppRestart: false, [], [response.ReasonPhrase ?? "Package operation failed."]);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
