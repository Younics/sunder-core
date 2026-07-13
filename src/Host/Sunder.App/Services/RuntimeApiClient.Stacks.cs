using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public async Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<RuntimeStackExportDiscoveryResponse>("stacks/export/items", cancellationToken)
           ?? new RuntimeStackExportDiscoveryResponse([], [], ["Runtime returned an empty Stack export discovery response."]);

    public async Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri("stacks/export"), request, cancellationToken);
        try
        {
            return await ReadJsonAsync<RuntimeStackExportResponse>(response, cancellationToken)
                   ?? new RuntimeStackExportResponse(false, null, [], [response.ReasonPhrase ?? "Stack export failed."]);
        }
        catch (JsonException)
        {
            return new RuntimeStackExportResponse(false, null, [], [response.ReasonPhrase ?? "Stack export failed."]);
        }
    }

    public async Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri("stacks/import/preview"), request, cancellationToken);
        try
        {
            return await ReadJsonAsync<RuntimeStackImportPreviewResponse>(response, cancellationToken)
                   ?? FailedPreview(response);
        }
        catch (JsonException)
        {
            return FailedPreview(response);
        }
    }

    public async Task<RuntimeStackImportResponse> ImportStackAsync(RuntimeStackImportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri("stacks/import/apply"), request, cancellationToken);
        try
        {
            return await ReadJsonAsync<RuntimeStackImportResponse>(response, cancellationToken)
                   ?? FailedImport(response);
        }
        catch (JsonException)
        {
            return FailedImport(response);
        }
    }

    private static RuntimeStackImportPreviewResponse FailedPreview(HttpResponseMessage response)
        => new(false, null, null, [], [], [], [], [response.ReasonPhrase ?? "Stack import preview failed."]);

    private static RuntimeStackImportResponse FailedImport(HttpResponseMessage response)
        => new(RuntimeStackImportOutcome.Failed, [], new Dictionary<string, string>(), [], [], [response.ReasonPhrase ?? "Stack import failed."]);
}
