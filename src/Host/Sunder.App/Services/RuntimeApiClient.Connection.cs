using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync<SystemStatusResponse>("system", cancellationToken);

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
        => await GetJsonAsync<IReadOnlyList<ActivePackageDescriptor>>("packages/active", cancellationToken) ?? [];

    public async Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<SessionPackageDescriptor>>("packages/session", cancellationToken) ?? [];

    public async Task<DevPackageWatchStatus> SetDevPackageWatchIntentAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri("dev-packages/watch"),
            new DevPackageWatchIntentRequest(enabled),
            cancellationToken);
        return await ReadRequiredAsync<DevPackageWatchStatus>(response, cancellationToken);
    }

    public async Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(long afterSequenceId = 0, CancellationToken cancellationToken = default)
        => await GetJsonAsync<RuntimeEventSnapshot>($"runtime-events/snapshot?after={Math.Max(0, afterSequenceId)}", cancellationToken)
           ?? throw new InvalidDataException("Runtime returned an empty event snapshot.");

    public IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(long afterSequenceId, CancellationToken cancellationToken = default)
        => ReadSseAsync<RuntimeEventDescriptor>("runtime-events/stream", afterSequenceId, cancellationToken);

    public async Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(long afterSequenceId = 0, int limit = 500, CancellationToken cancellationToken = default)
        => await GetJsonAsync<PackageLogSnapshot>(
               $"package-logs/snapshot?after={Math.Max(0, afterSequenceId)}&limit={Math.Clamp(limit, 1, 1000)}",
               cancellationToken)
           ?? throw new InvalidDataException("Runtime returned an empty package-log snapshot.");

    public IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(long afterSequenceId, CancellationToken cancellationToken = default)
        => ReadSseAsync<PackageLogEntryDescriptor>("package-logs/stream", afterSequenceId, cancellationToken);

    public async Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<PackageUiSnapshotDescriptor>>("packages/ui-snapshots", cancellationToken) ?? [];

    public async Task DownloadPackageUiSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri(snapshot.SnapshotUri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentLength is > AppPackageSourcePreparer.MaxSnapshotBytes)
        {
            throw new InvalidDataException("Runtime package UI snapshot exceeds the App stream limit.");
        }

        await BoundedHttpContentReader.CopyToAsync(
            response.Content,
            destination,
            AppPackageSourcePreparer.MaxSnapshotBytes,
            cancellationToken);
    }

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<InstalledPackageDescriptor>>("packages/installed", cancellationToken) ?? [];

    public async Task<PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateRequestUri($"packages/session/{Uri.EscapeDataString(packageId)}/status"),
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageSessionStatus>(response, cancellationToken);
    }

    public async Task<PackageSessionOperationResult> LoadPackageSessionAsync(PackageSessionLoadRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri("packages/session/load"), request, cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageSessionOperationResult>(response, cancellationToken)
               ?? PackageSessionOperationResult.Failed("Runtime returned an empty package-session load response.");
    }

    public async Task<PackageSessionOperationResult> UnloadPackageSessionAsync(string packageId, PackageSourceKind sourceKind, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateRequestUri($"packages/session/{Uri.EscapeDataString(packageId)}/unload"),
            new PackageSessionUnloadRequest(sourceKind),
            cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<PackageSessionOperationResult>(response, cancellationToken)
               ?? PackageSessionOperationResult.Failed("Runtime returned an empty package-session unload response.");
    }

    public Uri CreatePackageAssetUri(string packageId, string assetPath)
        => CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/assets/{EscapeRelativePath(assetPath)}");

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(CreateRequestUri("system/shutdown"), content: null, cancellationToken);
        await _responses.EnsureSuccessAsync(response, cancellationToken);
    }
}
