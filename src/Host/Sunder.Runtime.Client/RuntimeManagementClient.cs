using System.Net.Http.Json;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient : IDisposable
{
    private readonly RuntimeClientTransport _transport;
    private readonly bool _ownsTransport;
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly HttpClient _httpClient;
    private readonly RuntimeHttpResponseReader _responses;

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(CreateUri(path), token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<T>(response, token).ConfigureAwait(false);
    }

    private async Task<T> GetRequiredAsync<T>(string path, string requiredFeature, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateUri(path));
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { requiredFeature });
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
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

}
