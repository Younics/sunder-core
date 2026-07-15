using System.Net.Http.Headers;
using System.Net;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimeAuthenticatedHttpMessageHandler : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<IReadOnlyList<string>> RequiredFeaturesKey =
        new("Sunder.Runtime.RequiredFeatures");
    private readonly Func<RuntimeConnectionInfo?> _getConnection;
    private readonly RuntimeClientPolicyOptions _policy;
    private readonly RuntimeProtocolNegotiationCache _negotiations;
    private readonly bool _ownsNegotiations;

    public RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
        : this(
            getConnection,
            innerHandler,
            policy ?? new RuntimeClientPolicyOptions(),
            new RuntimeProtocolNegotiationCache(),
            ownsNegotiations: true)
    {
    }

    internal RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler,
        RuntimeClientPolicyOptions policy,
        RuntimeProtocolNegotiationCache negotiations)
        : this(getConnection, innerHandler, policy, negotiations, ownsNegotiations: false)
    {
    }

    private RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler,
        RuntimeClientPolicyOptions policy,
        RuntimeProtocolNegotiationCache negotiations,
        bool ownsNegotiations)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _getConnection = getConnection ?? throw new ArgumentNullException(nameof(getConnection));
        _policy = policy;
        _negotiations = negotiations;
        _ownsNegotiations = ownsNegotiations;
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
            var handshake = await NegotiateAsync(connection, cancellationToken).ConfigureAwait(false);
            if (request.Options.TryGetValue(RequiredFeaturesKey, out var requiredFeatures)
                && RuntimeProtocolCompatibility.GetIncompatibility(handshake, requiredFeatures.ToArray()) is { } incompatibility)
            {
                throw new RuntimeProtocolException(incompatibility);
            }
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
        return await _negotiations.GetOrAddAsync(
            connection,
            async negotiationToken =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(negotiationToken);
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
                return handshake;
            },
            cancellationToken).ConfigureAwait(false);
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
            if (_ownsNegotiations)
            {
                _negotiations.Dispose();
            }
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
            => CopyToWithDeadlineAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
            => CopyToWithDeadlineAsync(stream, cancellationToken);

        protected override async Task<Stream> CreateContentReadStreamAsync()
            => new DeadlineReadStream(
                await _inner.ReadAsStreamAsync(_deadline.Token).ConfigureAwait(false),
                _deadline.Token);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
            return new DeadlineReadStream(
                await _inner.ReadAsStreamAsync(linked.Token).ConfigureAwait(false),
                _deadline.Token);
        }

        private async Task CopyToWithDeadlineAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
            await _inner.CopyToAsync(stream, linked.Token).ConfigureAwait(false);
        }

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

    private sealed class DeadlineReadStream(Stream inner, CancellationToken deadlineToken) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
        {
            deadlineToken.ThrowIfCancellationRequested();
            return inner.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            deadlineToken.ThrowIfCancellationRequested();
            return inner.Read(buffer);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
            return await inner.ReadAsync(buffer.AsMemory(offset, count), linked.Token).ConfigureAwait(false);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class RuntimeProtocolNegotiationCache : IDisposable
{
    private const int MaximumEntries = 8;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<RuntimeConnectionIdentity, RuntimeHandshakeResponse> _handshakes = [];

    public async Task<RuntimeHandshakeResponse> GetOrAddAsync(
        RuntimeConnectionInfo connection,
        Func<CancellationToken, Task<RuntimeHandshakeResponse>> negotiateAsync,
        CancellationToken cancellationToken)
    {
        var identity = RuntimeConnectionIdentity.Create(connection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_handshakes.TryGetValue(identity, out var handshake))
            {
                return handshake;
            }

            handshake = await negotiateAsync(cancellationToken).ConfigureAwait(false);
            if (_handshakes.Count >= MaximumEntries)
            {
                _handshakes.Clear();
            }
            _handshakes[identity] = handshake;
            return handshake;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private readonly record struct RuntimeConnectionIdentity(string RuntimeUrl, string BearerToken)
    {
        public static RuntimeConnectionIdentity Create(RuntimeConnectionInfo connection)
            => new(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl).AbsoluteUri, connection.BearerToken);
    }
}
