using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimePackageCallbackClient : IDisposable
{
    private readonly RuntimeClientTransport _transport;
    private readonly bool _ownsTransport;
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
        _responses = transport.Responses;
    }

    public async Task<PackageCallbackSessionResponse> StartAsync(
        string packageId,
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.PostAsJsonAsync(
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
        using var response = await _transport.GetAsync(
            CreateUri(packageId, $"callbacks/sessions/{Uri.EscapeDataString(callbackSessionId)}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<PackageCallbackSessionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CancelAsync(
        string packageId,
        string callbackSessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.DeleteAsync(
            CreateUri(packageId, $"callbacks/sessions/{Uri.EscapeDataString(callbackSessionId)}"),
            cancellationToken).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<bool>(response, cancellationToken).ConfigureAwait(false);
    }

    private Uri CreateUri(string packageId, string route)
        => _transport.CreateRequestUri($"packages/{Uri.EscapeDataString(packageId)}/{route}");

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
