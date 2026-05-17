using System.Net.Http.Json;
using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class RuntimeHealthProbe
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<SystemStatusResponse?> TryGetRuntimeStatusAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SystemStatusResponse>(
                new Uri(runtimeUrl, "api/system"),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task ShutdownRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await _httpClient.PostAsync(
                new Uri(runtimeUrl, "api/system/shutdown"),
                content: null,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Startup verifies that the old host actually stopped before launching the bundled host.
        }
    }

    public async Task<bool> IsRuntimeHealthyAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(runtimeUrl, "health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
