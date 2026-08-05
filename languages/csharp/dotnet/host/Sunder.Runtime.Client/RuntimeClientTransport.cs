using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimeClientTransport : IDisposable
{
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly RuntimeProtocolNegotiationCache _negotiations = new();
    private readonly HttpClient _httpClient;
    private int _disposed;

    public RuntimeClientTransport(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
    {
        _getConnectionInfo = getConnectionInfo ?? throw new ArgumentNullException(nameof(getConnectionInfo));
        Policy = policy ?? new RuntimeClientPolicyOptions();
        AuthenticatedHandler = new RuntimeAuthenticatedHttpMessageHandler(
            getConnectionInfo,
            innerHandler ?? new HttpClientHandler { AllowAutoRedirect = false },
            Policy,
            _negotiations);
        _httpClient = new HttpClient(AuthenticatedHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Responses = new RuntimeHttpResponseReader(Policy);
    }

    public RuntimeClientTransport(Uri runtimeUrl, RuntimeClientPolicyOptions? policy = null)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl), policy: policy)
    {
    }

    internal RuntimeAuthenticatedHttpMessageHandler AuthenticatedHandler { get; }

    public RuntimeClientPolicyOptions Policy { get; }

    internal RuntimeHttpResponseReader Responses { get; }

    public RuntimeConnectionInfo? GetConnectionInfo() => _getConnectionInfo();

    public Uri CreateRequestUri(string relativePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var connection = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), $"api/v1/{relativePath}");
    }

    public Task<RuntimeHandshakeResponse> NegotiateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return AuthenticatedHandler.NegotiateAsync(cancellationToken);
    }

    public Task<RuntimeHandshakeResponse> ProbeHandshakeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return AuthenticatedHandler.ProbeHandshakeAsync(cancellationToken);
    }

    public Task<RuntimeHandshakeResponse> RefreshHandshakeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return AuthenticatedHandler.RefreshNegotiationAsync(cancellationToken);
    }

    public async Task ShutdownWithoutProtocolNegotiationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateRequestUri("system/shutdown"));
        request.Options.Set(RuntimeAuthenticatedHttpMessageHandler.SkipProtocolNegotiationKey, true);
        using var response = await SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        await Responses.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _httpClient.SendAsync(request, completionOption, cancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(
        Uri requestUri,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _httpClient.GetAsync(requestUri, completionOption, cancellationToken);
    }

    internal HttpClient HttpClient => _httpClient;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _httpClient.Dispose();
        _negotiations.Dispose();
    }
}
