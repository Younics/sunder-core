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

public sealed class RuntimeApiClient : IRuntimeApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxSseEventCharacters = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;

    public RuntimeApiClient(Uri runtimeBaseUri)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeBaseUri)) { }

    public RuntimeApiClient(RuntimeConnectionState runtimeConnectionState)
        : this(() => runtimeConnectionState.ConnectionInfo) { }

    internal RuntimeApiClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null)
    {
        _getConnectionInfo = getConnectionInfo ?? throw new ArgumentNullException(nameof(getConnectionInfo));
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(_getConnectionInfo, innerHandler));
    }

    public async Task<SystemStatusResponse?> GetSystemStatusAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<SystemStatusResponse>(
            CreateRequestUri("system"),
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
            CreateRequestUri("packages/active"),
            cancellationToken
        ) ?? [];

    public async Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<SessionPackageDescriptor>>(
            CreateRequestUri("packages/session"),
            cancellationToken
        ) ?? [];

    public async Task<DevPackageWatchStatus> SetDevPackageWatchIntentAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("dev-packages/watch"),
            new DevPackageWatchIntentRequest(enabled),
            cancellationToken);
        return await ReadRequiredAsync<DevPackageWatchStatus>(response, cancellationToken);
    }

    public async Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(
        long afterSequenceId = 0,
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<RuntimeEventSnapshot>(
               CreateRequestUri($"runtime-events/snapshot?after={Math.Max(0, afterSequenceId)}"),
               cancellationToken)
           ?? throw new InvalidDataException("Runtime returned an empty event snapshot.");

    public IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(
        long afterSequenceId,
        CancellationToken cancellationToken = default)
        => ReadSseAsync<RuntimeEventDescriptor>("runtime-events/stream", afterSequenceId, cancellationToken);

    public async Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(
        long afterSequenceId = 0,
        int limit = 500,
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<PackageLogSnapshot>(
               CreateRequestUri($"package-logs/snapshot?after={Math.Max(0, afterSequenceId)}&limit={Math.Clamp(limit, 1, 1000)}"),
               cancellationToken)
           ?? throw new InvalidDataException("Runtime returned an empty package-log snapshot.");

    public IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(
        long afterSequenceId,
        CancellationToken cancellationToken = default)
        => ReadSseAsync<PackageLogEntryDescriptor>("package-logs/stream", afterSequenceId, cancellationToken);

    public async Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<PackageUiSnapshotDescriptor>>(
            CreateRequestUri("packages/ui-snapshots"),
            cancellationToken
        ) ?? [];

    public async Task DownloadPackageUiSnapshotAsync(
        PackageUiSnapshotDescriptor snapshot,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri(snapshot.SnapshotUri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > AppPackageSourcePreparer.MaxSnapshotBytes)
        {
            throw new InvalidDataException("Runtime package UI snapshot exceeds the App stream limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, cancellationToken);
    }

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<InstalledPackageDescriptor>>(
            CreateRequestUri("packages/installed"),
            cancellationToken
        ) ?? [];

    public async Task<PackageSessionStatus?> GetPackageSessionStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri($"packages/session/{Uri.EscapeDataString(packageId)}/status"),
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
            CreateRequestUri("packages/session/load"),
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
            CreateRequestUri($"packages/session/{Uri.EscapeDataString(packageId)}/unload"),
            new PackageSessionUnloadRequest(sourceKind),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageSessionOperationResult>(cancellationToken: cancellationToken)
               ?? PackageSessionOperationResult.Failed("Runtime returned an empty package-session unload response.");
    }

    public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
        CreateRequestUri(
            $"packages/{Uri.EscapeDataString(packageId)}/assets/{EscapeRelativePath(assetPath)}"
        );

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        CancellationToken cancellationToken = default
    ) =>
        await InstallPackageFromPathAsync(packagePath, applyRuntimeSession: true, cancellationToken);

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        bool applyRuntimeSession,
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
    ) =>
        await UpgradePackageFromPathAsync(packageId, packagePath, allowDowngrade, reinstall, applyRuntimeSession: true, cancellationToken);

    public async Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade,
        bool reinstall,
        bool applyRuntimeSession,
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

    public async Task<ContentUploadDescriptor> UploadPackageAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
        => await UploadFileAsync(packagePath, "uploads/packages", "application/vnd.sunder.package", cancellationToken);

    public async Task<ContentUploadDescriptor> UploadStackAsync(
        string stackPath,
        CancellationToken cancellationToken = default)
        => await UploadFileAsync(stackPath, "uploads/stacks", "application/vnd.sunder.stack", cancellationToken);

    public async Task<ContentUploadDescriptor> UploadStackMediaAsync(
        string mediaPath,
        string contentType,
        CancellationToken cancellationToken = default)
        => await UploadFileAsync(mediaPath, "uploads/stack-media", contentType, cancellationToken);

    public async Task DownloadContentAsync(
        ContentDownloadDescriptor download,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(CreateRequestUri(download.DownloadUri), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length > download.Length)
            {
                throw new InvalidDataException("Runtime download exceeded its declared length.");
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (length != download.Length || !string.Equals(actualHash, download.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            destination.Close();
            File.Delete(destinationPath);
            throw new InvalidDataException("Runtime download hash or length verification failed.");
        }
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

        response.EnsureSuccessStatusCode();
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

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageLifecycleOperationResult>(
                cancellationToken: cancellationToken
            )
            ?? PackageLifecycleOperationResult.Failed("Runtime returned an empty package lifecycle load response.");
    }

    public async Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<RuntimeStackExportDiscoveryResponse>(
               CreateRequestUri("stacks/export/items"),
               cancellationToken)
           ?? new RuntimeStackExportDiscoveryResponse([], [], ["Runtime returned an empty Stack export discovery response."]);

    public async Task<RuntimeStackExportResponse> ExportStackAsync(
        RuntimeStackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("stacks/export"),
            request,
            cancellationToken);

        RuntimeStackExportResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RuntimeStackExportResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Fall through to a generic protocol-level failure.
        }

        return result ?? new RuntimeStackExportResponse(
            false,
            null,
            [],
            [response.ReasonPhrase ?? "Stack export failed."]);
    }

    public async Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(
        RuntimeStackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("stacks/import/preview"),
            request,
            cancellationToken);

        RuntimeStackImportPreviewResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RuntimeStackImportPreviewResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Fall through to a generic protocol-level failure.
        }

        return result ?? new RuntimeStackImportPreviewResponse(
            false,
            [],
            [],
            [],
            [],
            [response.ReasonPhrase ?? "Stack import preview failed."]);
    }

    public async Task<RuntimeStackImportResponse> ImportStackAsync(
        RuntimeStackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("stacks/import/apply"),
            request,
            cancellationToken);

        RuntimeStackImportResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<RuntimeStackImportResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Fall through to a generic protocol-level failure.
        }

        return result ?? new RuntimeStackImportResponse(
            false,
            [],
            request.IdRemaps,
            [],
            [response.ReasonPhrase ?? "Stack import failed."]);
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

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageLifecycleStageResult>(
                cancellationToken: cancellationToken
            )
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

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PackageLifecycleOperationResult>(
                cancellationToken: cancellationToken
            )
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

        response.EnsureSuccessStatusCode();
    }

    public async Task<
        IReadOnlyList<PackageConfigurationSchemaDescriptor>
    > GetConfigurationSchemasAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<IReadOnlyList<PackageConfigurationSchemaDescriptor>>(
            CreateRequestUri("packages/configuration/schemas"),
            cancellationToken
        ) ?? [];

    public async Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default
    ) =>
        await _httpClient.GetFromJsonAsync<PackageConfigurationValuesResponse>(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/config/values"),
            cancellationToken
        );

    public async Task SavePackageConfigurationValuesAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PutAsJsonAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/config/values"),
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
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/auth/status"),
            cancellationToken
        );

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/auth/start"),
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
                $"packages/{Uri.EscapeDataString(packageId)}/auth/sessions/{Uri.EscapeDataString(authSessionId)}"
            ),
            cancellationToken
        );

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default
    )
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/auth/disconnect"),
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
        var status = await GetPackageSessionStatusAsync(packageId, cancellationToken);
        if (status is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/fault"),
            new ReportPackageFaultRequest(origin, message, status.GenerationId),
            cancellationToken
        );

        response.EnsureSuccessStatusCode();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateRequestUri("system/shutdown"),
            content: null,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
    }

    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryAuthStartRequest, RuntimeRegistryAuthStartResponse>("registry/auth/start", request, cancellationToken);

    public async Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(CreateRequestUri($"registry/auth/sessions/{Uri.EscapeDataString(sessionId)}"), cancellationToken);
        return response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? null
            : await ReadRequiredAsync<RuntimeRegistryAuthSessionStatus>(response, cancellationToken);
    }

    public async Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string registryOrigin, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(CreateRequestUri($"registry/auth/status?origin={Uri.EscapeDataString(registryOrigin)}"), cancellationToken);
        return await ReadRequiredAsync<RuntimeRegistryAuthStatus>(response, cancellationToken);
    }

    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string registryOrigin, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryOriginRequest, RuntimeRegistryAuthStatus>("registry/auth/logout", new(registryOrigin), cancellationToken);

    public Task<RegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPackageBatchRequest, RegistryResolveInstallPlanResponse>("registry/packages/plan", request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPackageRequest, RuntimeRegistryPackageChangeResult>("registry/packages/install", request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPackageBatchRequest, RuntimeRegistryPackageChangeResult>("registry/packages/apply", request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryUpdateRequest, RuntimeRegistryPackageChangeResult>("registry/packages/update", request, cancellationToken);

    public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryStarRequest, RegistryPackageStarResponse>("registry/packages/star", request, cancellationToken);

    public Task<RegistryStackStarResponse> SetRegistryStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryStarRequest, RegistryStackStarResponse>("registry/stacks/star", request, cancellationToken);

    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPublishRequest, RegistryPublishStackResponse>("registry/stacks/publish", request, cancellationToken);

    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryDeleteStackRequest, RegistryStackManagementOperationResponse>("registry/stacks/delete", request, cancellationToken);

    private async Task<TResponse> PostRegistryAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri(path), request, cancellationToken);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken);
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
        response.EnsureSuccessStatusCode();
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

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
               ?? throw new InvalidDataException($"Runtime returned an empty {typeof(T).Name} response.");
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
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContentUploadDescriptor>(cancellationToken: cancellationToken)
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

    private static async Task<PackageStoreStageResult?> ReadPackageStoreStageResultAsync(
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
