using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimeAuthenticatedHttpMessageHandler : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<IReadOnlyList<string>> RequiredFeaturesKey =
        new("Sunder.Runtime.RequiredFeatures");
    internal static readonly HttpRequestOptionsKey<bool> SkipProtocolNegotiationKey =
        new("Sunder.Runtime.SkipProtocolNegotiation");
    internal static readonly HttpRequestOptionsKey<JsonElement> RpcValueKey =
        new("Sunder.Runtime.RpcValue");
    private readonly Func<RuntimeConnectionInfo?> _getConnection;
    private readonly RuntimeClientPolicyOptions _policy;
    private readonly RuntimeProtocolNegotiationCache _negotiations;
    private readonly RuntimeClientResourceProfileStore _resourceProfiles;
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
            new RuntimeClientResourceProfileStore(),
            ownsNegotiations: true)
    {
    }

    internal RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler,
        RuntimeClientPolicyOptions policy,
        RuntimeProtocolNegotiationCache negotiations,
        RuntimeClientResourceProfileStore resourceProfiles)
        : this(
            getConnection,
            innerHandler,
            policy,
            negotiations,
            resourceProfiles,
            ownsNegotiations: false)
    {
    }

    private RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler,
        RuntimeClientPolicyOptions policy,
        RuntimeProtocolNegotiationCache negotiations,
        RuntimeClientResourceProfileStore resourceProfiles,
        bool ownsNegotiations)
        : base(innerHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        _getConnection = getConnection ?? throw new ArgumentNullException(nameof(getConnection));
        _policy = policy;
        _negotiations = negotiations;
        _resourceProfiles = resourceProfiles;
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
        var skipProtocolNegotiation = request.Options.TryGetValue(SkipProtocolNegotiationKey, out var skip)
                                      && skip;
        if (!skipProtocolNegotiation && IsVersionedRequest(connection, request.RequestUri))
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
            var limits = new RuntimeClientResourceLimits(_resourceProfiles.Get(connection));
            if (request.Options.TryGetValue(RpcValueKey, out var rpcValue))
            {
                limits.EnsureRpcValue(rpcValue, "Runtime RPC request value");
            }
            await PreflightJsonContentAsync(request, limits, deadline.Token).ConfigureAwait(false);
            var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            EnsureResponseOrigin(connection, request, response);
            response.Content = new DeadlineHttpContent(
                response.Content,
                deadline,
                cancellationToken,
                timeout);
            return response;
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            deadline.Dispose();
            throw new TimeoutException($"Runtime request exceeded the {timeout} timeout.", exception);
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

    public async Task<RuntimeHandshakeResponse> RefreshNegotiationAsync(CancellationToken cancellationToken = default)
    {
        var connection = _getConnection()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        _resourceProfiles.Remove(connection);
        try
        {
            return await _negotiations.RefreshAsync(
                connection,
                negotiationToken => RequestHandshakeAsync(
                    connection,
                    requireCompatibility: true,
                    negotiationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _resourceProfiles.Remove(connection);
            throw;
        }
    }

    internal Task<RuntimeHandshakeResponse> ProbeHandshakeAsync(CancellationToken cancellationToken = default)
    {
        var connection = _getConnection()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return RequestHandshakeAsync(connection, requireCompatibility: false, cancellationToken);
    }

    private async Task<RuntimeHandshakeResponse> NegotiateAsync(
        RuntimeConnectionInfo connection,
        CancellationToken cancellationToken)
    {
        return await _negotiations.GetOrAddAsync(
            connection,
            negotiationToken => RequestHandshakeAsync(
                connection,
                requireCompatibility: true,
                negotiationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeHandshakeResponse> RequestHandshakeAsync(
        RuntimeConnectionInfo connection,
        bool requireCompatibility,
        CancellationToken cancellationToken)
    {
        if (requireCompatibility)
        {
            _resourceProfiles.Remove(connection);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.RequestTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), "api/handshake"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
        try
        {
            using var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
            EnsureResponseOrigin(connection, request, response);
            var reader = new RuntimeHttpResponseReader(_policy);
            var payload = await reader.ReadRequiredPayloadAsync(
                    response,
                    _policy.MaxHandshakeResponseBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            var handshake = payload.Length == 0
                ? null
                : JsonSerializer.Deserialize<RuntimeHandshakeResponse>(
                    payload,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16 });
            if (handshake is null)
            {
                throw new RuntimeProtocolException("Runtime did not return a handshake.");
            }
            if (requireCompatibility
                && RuntimeProtocolCompatibility.GetIncompatibility(handshake) is { } incompatibility)
            {
                throw new RuntimeProtocolException(incompatibility);
            }
            if (requireCompatibility)
            {
                try
                {
                    _resourceProfiles.Set(
                        connection,
                        RuntimeClientResourceProfileParser.Parse(handshake, payload));
                }
                catch (InvalidDataException exception)
                {
                    _resourceProfiles.Remove(connection);
                    throw new RuntimeProtocolException(exception.Message);
                }
            }
            return handshake;
        }
        catch (OperationCanceledException exception)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Runtime request exceeded the {_policy.RequestTimeout} timeout.", exception);
        }
    }

    private static void EnsureResponseOrigin(
        RuntimeConnectionInfo connection,
        HttpRequestMessage request,
        HttpResponseMessage response)
    {
        var responseUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
        if (responseUri is not null && connection.CanSendTo(responseUri))
        {
            return;
        }

        response.Dispose();
        throw new InvalidDataException("Runtime response changed to an untrusted origin.");
    }

    private async Task PreflightJsonContentAsync(
        HttpRequestMessage request,
        RuntimeClientResourceLimits limits,
        CancellationToken cancellationToken)
    {
        var content = request.Content;
        var mediaType = content?.Headers.ContentType?.MediaType;
        if (content is null
            || mediaType is null
            || (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                && !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var charset = content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset)
            && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Runtime JSON requests must use UTF-8.");
        }

        var maximumBytes = Math.Min(_policy.MaxJsonRequestBytes, limits.MaximumJsonEnvelopeBytes);
        if (content.Headers.ContentLength is { } declaredLength && declaredLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"Runtime request payload exceeds the {maximumBytes} byte limit.");
        }

        var destination = new BoundedWriteStream(maximumBytes);
        try
        {
            await content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (BoundedWriteLimitException)
        {
            throw new InvalidDataException(
                $"Runtime request payload exceeds the {maximumBytes} byte limit.");
        }

        var bytes = destination.ToArray();
        try
        {
            using var _ = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaximumJsonEnvelopeDepth,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Runtime request payload is not valid bounded UTF-8 JSON.",
                exception);
        }

        var replacement = new ByteArrayContent(bytes);
        foreach (var header in content.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = replacement;
        content.Dispose();
    }

    private static bool IsVersionedRequest(RuntimeConnectionInfo connection, Uri requestUri)
    {
        var root = RuntimeConnectionInfo.Normalize(connection.RuntimeUrl).AbsolutePath;
        return requestUri.AbsolutePath.StartsWith($"{root}api/v1/", StringComparison.Ordinal);
    }

    private static bool IsStreamRequest(HttpRequestMessage request)
        => request.Headers.Accept.Any(value => string.Equals(value.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
           || request.RequestUri?.AbsolutePath.Contains("/streams/", StringComparison.Ordinal) == true;

    private sealed class BoundedWriteStream(long maximumBytes) : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void EnsureCapacity(int count)
        {
            if (_inner.Length + count > maximumBytes)
            {
                throw new BoundedWriteLimitException();
            }
        }
    }

    private sealed class BoundedWriteLimitException : Exception
    {
    }

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
        private readonly CancellationToken _callerToken;
        private readonly TimeSpan _timeout;

        public DeadlineHttpContent(
            HttpContent inner,
            CancellationTokenSource deadline,
            CancellationToken callerToken,
            TimeSpan timeout)
        {
            _inner = inner;
            _deadline = deadline;
            _callerToken = callerToken;
            _timeout = timeout;
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
        {
            try
            {
                return new DeadlineReadStream(
                    await _inner.ReadAsStreamAsync(_deadline.Token).ConfigureAwait(false),
                    _deadline.Token,
                    _callerToken,
                    _timeout);
            }
            catch (OperationCanceledException exception) when (DeadlineExceeded)
            {
                throw Timeout(exception);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
                return new DeadlineReadStream(
                    await _inner.ReadAsStreamAsync(linked.Token).ConfigureAwait(false),
                    _deadline.Token,
                    _callerToken,
                    _timeout);
            }
            catch (OperationCanceledException exception) when (DeadlineExceeded)
            {
                throw Timeout(exception);
            }
        }

        private async Task CopyToWithDeadlineAsync(Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
                await _inner.CopyToAsync(stream, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (DeadlineExceeded)
            {
                throw Timeout(exception);
            }
        }

        private bool DeadlineExceeded
            => _deadline.IsCancellationRequested && !_callerToken.IsCancellationRequested;

        private TimeoutException Timeout(OperationCanceledException exception)
            => new($"Runtime response body exceeded the {_timeout} timeout.", exception);

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

    private sealed class DeadlineReadStream(
        Stream inner,
        CancellationToken deadlineToken,
        CancellationToken callerToken,
        TimeSpan timeout) : Stream
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
            ThrowIfDeadlineExceeded();
            return inner.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            ThrowIfDeadlineExceeded();
            return inner.Read(buffer);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
                return await inner.ReadAsync(buffer.AsMemory(offset, count), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (DeadlineExceeded)
            {
                throw Timeout(exception);
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
                return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (DeadlineExceeded)
            {
                throw Timeout(exception);
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        private bool DeadlineExceeded
            => deadlineToken.IsCancellationRequested && !callerToken.IsCancellationRequested;

        private void ThrowIfDeadlineExceeded()
        {
            if (DeadlineExceeded)
            {
                throw new TimeoutException($"Runtime response body exceeded the {timeout} timeout.");
            }
            deadlineToken.ThrowIfCancellationRequested();
        }

        private TimeoutException Timeout(OperationCanceledException exception)
            => new($"Runtime response body exceeded the {timeout} timeout.", exception);

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

    public async Task<RuntimeHandshakeResponse> RefreshAsync(
        RuntimeConnectionInfo connection,
        Func<CancellationToken, Task<RuntimeHandshakeResponse>> negotiateAsync,
        CancellationToken cancellationToken)
    {
        var identity = RuntimeConnectionIdentity.Create(connection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _handshakes.Remove(identity);
            try
            {
                var handshake = await negotiateAsync(cancellationToken).ConfigureAwait(false);
                if (_handshakes.Count >= MaximumEntries)
                {
                    _handshakes.Clear();
                }
                _handshakes[identity] = handshake;
                return handshake;
            }
            catch
            {
                _handshakes.Remove(identity);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

}
