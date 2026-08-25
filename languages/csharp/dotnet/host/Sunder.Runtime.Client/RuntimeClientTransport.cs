using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimeClientTransport : IDisposable
{
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly RuntimeProtocolNegotiationCache _negotiations = new();
    private readonly RuntimeClientResourceProfileStore _resourceProfiles = new();
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
            _negotiations,
            _resourceProfiles);
        _httpClient = new HttpClient(AuthenticatedHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = RuntimeClientResourceLimits.MaximumProtocolEnvelopeBytes,
        };
        Responses = new RuntimeHttpResponseReader(Policy, GetResourceLimits);
    }

    public RuntimeClientTransport(Uri runtimeUrl, RuntimeClientPolicyOptions? policy = null)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl), policy: policy)
    {
    }

    internal RuntimeAuthenticatedHttpMessageHandler AuthenticatedHandler { get; }

    public RuntimeClientPolicyOptions Policy { get; }

    internal RuntimeHttpResponseReader Responses { get; }

    public RuntimeClientResourceProfile? CurrentResourceProfile
    {
        get
        {
            var connection = _getConnectionInfo();
            return connection is null ? null : _resourceProfiles.Get(connection);
        }
    }

    public RuntimeClientResourceLimits ResourceLimits
        => new(CurrentResourceProfile);

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
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _httpClient.SendAsync(request, completionOption, cancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(
        Uri requestUri,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _httpClient.GetAsync(requestUri, completionOption, cancellationToken);
    }

    internal Task<HttpResponseMessage> PostAsync(
        Uri requestUri,
        HttpContent? content,
        CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, requestUri, content, cancellationToken);

    internal Task<HttpResponseMessage> PutAsync(
        Uri requestUri,
        HttpContent? content,
        CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Put, requestUri, content, cancellationToken);

    internal Task<HttpResponseMessage> DeleteAsync(
        Uri requestUri,
        CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, requestUri, content: null, cancellationToken);

    internal Task<HttpResponseMessage> PostAsJsonAsync<TValue>(
        Uri requestUri,
        TValue value,
        CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, requestUri, JsonContent.Create(value), cancellationToken);

    internal Task<HttpResponseMessage> PutAsJsonAsync<TValue>(
        Uri requestUri,
        TValue value,
        CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Put, requestUri, JsonContent.Create(value), cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri requestUri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri) { Content = content };
        return await SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private RuntimeClientResourceLimits GetResourceLimits() => ResourceLimits;

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
