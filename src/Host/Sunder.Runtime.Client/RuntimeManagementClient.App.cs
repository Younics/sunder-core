using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsRuntimeHealthyAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(CreateUri("health"), token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task ShutdownAsync(CancellationToken token = default)
    {
        using var response = await _httpClient.PostAsync(CreateUri("system/shutdown"), null, token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<ActivePackageDescriptor>>("packages/active", token);

    public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<SessionPackageDescriptor>>("packages/session", token);

    public async Task<PackageSessionStatus?> GetPackageSessionStatusAsync(
        string packageId,
        CancellationToken token = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateUri($"packages/session/{Uri.EscapeDataString(packageId)}/status"),
            token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        return await _responses.ReadRequiredJsonAsync<PackageSessionStatus>(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeHandshakeResponse> GetRuntimeHandshakeAsync(CancellationToken token = default)
        => _transport.RefreshHandshakeAsync(token);

    public Task<DevPackageOwnerLeaseResponse> ReplaceDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerMutationRequest request,
        CancellationToken token = default)
        => SendDevPackageOwnerAsync<DevPackageOwnerMutationRequest, DevPackageOwnerLeaseResponse>(
            HttpMethod.Put,
            $"dev-package-owners/{Uri.EscapeDataString(ownerId)}",
            request,
            token);

    public Task<DevPackageOwnerLeaseResponse> HeartbeatDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerHeartbeatRequest request,
        CancellationToken token = default)
        => SendDevPackageOwnerAsync<DevPackageOwnerHeartbeatRequest, DevPackageOwnerLeaseResponse>(
            HttpMethod.Post,
            $"dev-package-owners/{Uri.EscapeDataString(ownerId)}/heartbeat",
            request,
            token);

    public async Task ReleaseDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerReleaseRequest request,
        CancellationToken token = default)
    {
        using var message = CreateDevPackageOwnerRequest(
            HttpMethod.Post,
            $"dev-package-owners/{Uri.EscapeDataString(ownerId)}/release",
            request);
        using var response = await _httpClient.SendAsync(message, token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(
        long afterSequenceId = 0,
        CancellationToken token = default)
        => GetRequiredAsync<RuntimeEventSnapshot>(
            $"runtime-events/snapshot?after={Math.Max(0, afterSequenceId)}",
            token);

    public IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(
        long afterSequenceId,
        CancellationToken token = default)
        => ReadSseAsync<RuntimeEventDescriptor>("runtime-events/stream", afterSequenceId, token);

    public Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(
        long afterSequenceId = 0,
        int limit = 500,
        CancellationToken token = default)
        => GetRequiredAsync<PackageLogSnapshot>(
            $"package-logs/snapshot?after={Math.Max(0, afterSequenceId)}&limit={Math.Clamp(limit, 1, 1000)}",
            token);

    public IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(
        long afterSequenceId,
        CancellationToken token = default)
        => ReadSseAsync<PackageLogEntryDescriptor>("package-logs/stream", afterSequenceId, token);

    public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<PackageUiSnapshotDescriptor>>("packages/ui-snapshots", token);

    public async Task DownloadPackageUiSnapshotAsync(
        PackageUiSnapshotDescriptor snapshot,
        Stream destination,
        long maxBytes,
        CancellationToken token = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateUri(snapshot.SnapshotUri),
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
        await CopyBoundedAsync(response.Content, destination, maxBytes, token).ConfigureAwait(false);
    }

    public Uri CreatePackageAssetUri(string packageId, string assetPath)
        => CreateUri($"packages/{Uri.EscapeDataString(packageId)}/assets/{EscapeRelativePath(assetPath)}");

    public async Task DownloadContentAsync(
        ContentDownloadDescriptor download,
        string destinationPath,
        CancellationToken token = default)
    {
        if (download.Length < 0)
        {
            throw new InvalidDataException("Runtime download declared an invalid length.");
        }

        using var response = await _httpClient.GetAsync(
            CreateUri(download.DownloadUri),
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await VerifiedFileTransfer.PublishAsync(
            source,
            destinationPath,
            Math.Max(1, download.Length),
            download.Length,
            download.ContentHash,
            "Runtime download",
            token).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<PackageSettingsSchemaDescriptor>>("packages/settings/schemas", token);

    public async Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(
        string packageId,
        CancellationToken token = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateUri($"packages/{Uri.EscapeDataString(packageId)}/settings/values"),
            token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        return await _responses.ReadRequiredJsonAsync<PackageSettingsValuesResponse>(response, token).ConfigureAwait(false);
    }

    public async Task SavePackageSettingsValuesAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken token = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            CreateUri($"packages/{Uri.EscapeDataString(packageId)}/settings/values"),
            new UpdatePackageSettingsRequest(values),
            token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public Task<PackageAuthStatusResponse> GetPackageAuthStatusAsync(string packageId, CancellationToken token = default)
        => GetRequiredAsync<PackageAuthStatusResponse>($"packages/{Uri.EscapeDataString(packageId)}/auth/status", token);

    public async Task<PackageAuthSessionStartResponse> StartPackageAuthAsync(string packageId, CancellationToken token = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateUri($"packages/{Uri.EscapeDataString(packageId)}/auth/start"),
            null,
            token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<PackageAuthSessionStartResponse>(response, token).ConfigureAwait(false);
    }

    public Task<PackageAuthSessionStatusResponse> GetPackageAuthSessionStatusAsync(
        string packageId,
        string authSessionId,
        CancellationToken token = default)
        => GetRequiredAsync<PackageAuthSessionStatusResponse>(
            $"packages/{Uri.EscapeDataString(packageId)}/auth/sessions/{Uri.EscapeDataString(authSessionId)}",
            token);

    public async Task<PackageAuthStatusResponse> DisconnectPackageAuthAsync(string packageId, CancellationToken token = default)
    {
        using var response = await _httpClient.PostAsync(
            CreateUri($"packages/{Uri.EscapeDataString(packageId)}/auth/disconnect"),
            null,
            token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<PackageAuthStatusResponse>(response, token).ConfigureAwait(false);
    }

    public async Task ReportPackageFaultAsync(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        CancellationToken token = default)
    {
        var status = await GetPackageSessionStatusAsync(packageId, token).ConfigureAwait(false);
        if (status is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            CreateUri($"packages/{Uri.EscapeDataString(packageId)}/fault"),
            new ReportPackageFaultRequest(origin, message, status.GenerationId),
            token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeStackExportDiscoveryResponse>("stacks/export/items", token);

    public Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken token = default)
        => PostAsync<RuntimeStackExportRequest, RuntimeStackExportResponse>("stacks/export", request, token);

    public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(
        RuntimeStackImportPreviewRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeStackImportPreviewRequest, RuntimeStackImportPreviewResponse>("stacks/import/preview", request, token);

    public Task<RuntimeStackImportResponse> ImportStackAsync(RuntimeStackImportRequest request, CancellationToken token = default)
        => PostAsync<RuntimeStackImportRequest, RuntimeStackImportResponse>("stacks/import/apply", request, token);

    private async IAsyncEnumerable<T> ReadSseAsync<T>(
        string endpoint,
        long afterSequenceId,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CreateUri($"{endpoint}?after={Math.Max(0, afterSequenceId)}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (afterSequenceId > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Last-Event-ID",
                afterSequenceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: false);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return JsonSerializer.Deserialize<T>(data.ToString(), StreamJsonOptions)
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
            if (data.Length + value.Length > _transport.Policy.MaxStreamEventBytes)
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

    private static string EscapeRelativePath(string path)
        => string.Join(
            '/',
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));

    private async Task<TResponse> SendDevPackageOwnerAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        CancellationToken token)
    {
        using var request = CreateDevPackageOwnerRequest(method, path, payload);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<TResponse>(response, token).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateDevPackageOwnerRequest<TRequest>(
        HttpMethod method,
        string path,
        TRequest payload)
    {
        var request = new HttpRequestMessage(method, CreateUri(path))
        {
            Content = JsonContent.Create(payload),
        };
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { RuntimeProtocolFeatures.DevPackageOwnerLeasesV1 });
        return request;
    }

    private static async Task CopyBoundedAsync(
        HttpContent content,
        Stream destination,
        long maxBytes,
        CancellationToken token)
    {
        if (content.Headers.ContentLength is > 0 and var contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException($"Runtime response payload exceeds the {maxBytes} byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Runtime response payload exceeds the {maxBytes} byte limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }
}
