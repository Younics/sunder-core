using System.Net.Http.Json;
using System.Net.Sockets;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class RuntimeHealthProbe : IDisposable
{
    private readonly RuntimeConnectionState _connectionState;
    private readonly HttpClient _httpClient;

    public RuntimeHealthProbe(RuntimeConnectionState connectionState)
    {
        _connectionState = connectionState;
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(() => _connectionState.ConnectionInfo))
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    public async Task<SystemStatusResponse?> TryGetRuntimeStatusAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SystemStatusResponse>(
                new Uri(runtimeUrl, "api/v1/system"),
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
                new Uri(runtimeUrl, "api/v1/system/shutdown"),
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
        if (_connectionState.ConnectionInfo is null)
        {
            return await CanConnectAsync(runtimeUrl, cancellationToken);
        }

        try
        {
            using var response = await _httpClient.GetAsync(new Uri(runtimeUrl, "api/v1/health"), cancellationToken);
            return true;
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

    private static async Task<bool> CanConnectAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(runtimeUrl.Host, runtimeUrl.Port, cancellationToken);
            return true;
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

    public void Dispose() => _httpClient.Dispose();
}
