using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimePackageCallbackClient : IDisposable
{
    private readonly RuntimeClientTransport _transport;
    private readonly bool _ownsTransport;
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly HttpClient _httpClient;
    private readonly RuntimeHttpResponseReader _responses;

    public RuntimePackageCallbackClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
        : this(new RuntimeClientTransport(getConnectionInfo, innerHandler, policy), ownsTransport: true)
    {
    }

    public RuntimePackageCallbackClient(RuntimeClientTransport transport)
        : this(transport, ownsTransport: false)
    {
    }

    private RuntimePackageCallbackClient(RuntimeClientTransport transport, bool ownsTransport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _getConnectionInfo = transport.GetConnectionInfo;
        _httpClient = transport.HttpClient;
        _responses = transport.Responses;
    }

    public async Task<PackageCallbackSessionResponse> StartAsync(
        string packageId,
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            CreateUri(packageId, $"callbacks/{Uri.EscapeDataString(callbackHandlerId)}/start"),
            new PackageCallbackSessionStartRequest(parameters),
            cancellationToken).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<PackageCallbackSessionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PackageCallbackSessionResponse> GetStatusAsync(
        string packageId,
        string callbackSessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            CreateUri(packageId, $"callbacks/sessions/{Uri.EscapeDataString(callbackSessionId)}"),
            cancellationToken).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<PackageCallbackSessionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private Uri CreateUri(string packageId, string route)
    {
        var connection = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(
            RuntimeConnectionInfo.Normalize(connection.RuntimeUrl),
            $"api/v1/packages/{Uri.EscapeDataString(packageId)}/{route}");
    }

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
