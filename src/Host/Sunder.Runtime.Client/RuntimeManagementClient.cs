using System.Net.Http.Json;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient : IDisposable
{
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly HttpClient _httpClient;
    private readonly RuntimeHttpResponseReader _responses;

    public RuntimeManagementClient(Uri runtimeUrl)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl))
    {
    }

    public RuntimeManagementClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
    {
        var clientPolicy = policy ?? new RuntimeClientPolicyOptions();
        _getConnectionInfo = getConnectionInfo ?? throw new ArgumentNullException(nameof(getConnectionInfo));
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(getConnectionInfo, innerHandler, clientPolicy))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _responses = new RuntimeHttpResponseReader(clientPolicy);
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<T>(response, token).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken token)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateUri(path), request, token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<TResponse>(response, token).ConfigureAwait(false);
    }

    private Uri CreateUri(string path)
    {
        var connection = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), $"api/v1/{path}");
    }

    public void Dispose() => _httpClient.Dispose();

}
