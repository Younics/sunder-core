using System.Net.Http.Headers;
using System.Net;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimeAuthenticatedHttpMessageHandler : DelegatingHandler
{
    private readonly Func<RuntimeConnectionInfo?> _getConnection;
    private readonly RuntimeClientPolicyOptions _policy;
    private readonly SemaphoreSlim _negotiationGate = new(1, 1);
    private RuntimeConnectionInfo? _negotiatedConnection;
    private RuntimeHandshakeResponse? _handshake;

    public RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _getConnection = getConnection ?? throw new ArgumentNullException(nameof(getConnection));
        _policy = policy ?? new RuntimeClientPolicyOptions();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var connection = _getConnection()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        if (request.RequestUri is null || !connection.CanSendTo(request.RequestUri))
        {
            throw new InvalidOperationException("Refusing to send Runtime credentials to a URL other than the authenticated Runtime URL.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
        if (IsVersionedRequest(connection, request.RequestUri))
        {
            await NegotiateAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var timeout = IsStreamRequest(request) ? _policy.StreamLifetimeTimeout : _policy.RequestTimeout;
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            response.Content = new DeadlineHttpContent(response.Content, deadline);
            return response;
        }
        catch
        {
            deadline.Dispose();
            throw;
        }
    }

    public Task<RuntimeHandshakeResponse> NegotiateAsync(CancellationToken cancellationToken = default)
    {
        var connection = _getConnection()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return NegotiateAsync(connection, cancellationToken);
    }

    private async Task<RuntimeHandshakeResponse> NegotiateAsync(
        RuntimeConnectionInfo connection,
        CancellationToken cancellationToken)
    {
        if (connection == _negotiatedConnection && _handshake is not null)
        {
            return _handshake;
        }

        await _negotiationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection == _negotiatedConnection && _handshake is not null)
            {
                return _handshake;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_policy.RequestTimeout);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), "api/handshake"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
            using var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var handshakePolicy = _policy with { MaxJsonResponseBytes = _policy.MaxHandshakeResponseBytes };
            var handshake = await new RuntimeHttpResponseReader(handshakePolicy)
                .ReadRequiredJsonAsync<RuntimeHandshakeResponse>(response, timeout.Token)
                .ConfigureAwait(false);
            if (RuntimeProtocolCompatibility.GetIncompatibility(handshake) is { } incompatibility)
            {
                throw new RuntimeProtocolException(incompatibility);
            }
            _negotiatedConnection = connection;
            _handshake = handshake;
            return handshake;
        }
        finally
        {
            _negotiationGate.Release();
        }
    }

    private static bool IsVersionedRequest(RuntimeConnectionInfo connection, Uri requestUri)
    {
        var root = RuntimeConnectionInfo.Normalize(connection.RuntimeUrl).AbsolutePath;
        return requestUri.AbsolutePath.StartsWith($"{root}api/v1/", StringComparison.Ordinal);
    }

    private static bool IsStreamRequest(HttpRequestMessage request)
        => request.Headers.Accept.Any(value => string.Equals(value.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
           || request.RequestUri?.AbsolutePath.Contains("/streams/", StringComparison.Ordinal) == true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _negotiationGate.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class DeadlineHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _deadline;

        public DeadlineHttpContent(HttpContent inner, CancellationTokenSource deadline)
        {
            _inner = inner;
            _deadline = deadline;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
            => _inner.CopyToAsync(stream, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => _inner.ReadAsStreamAsync();

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _deadline.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
