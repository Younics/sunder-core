using System.Net;
using System.Text.Json;
using Sunder.Registry.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryHttpClient(
    IHttpClientFactory httpClientFactory,
    RuntimeTransportPolicyOptions policy,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var timeout = new CancellationTokenSource(policy.RegistryRequestTimeout, timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var response = await httpClientFactory.CreateClient("registry").SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
            EnsureResponseOrigin(request, response);
            response.Content = new DeadlineHttpContent(
                response.Content,
                linked,
                timeout,
                cancellationToken,
                policy.RegistryRequestTimeout);
            return response;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            linked.Dispose();
            timeout.Dispose();
            throw new TimeoutException($"Registry request exceeded the {policy.RegistryRequestTimeout} timeout.", exception);
        }
        catch
        {
            linked.Dispose();
            timeout.Dispose();
            throw;
        }
    }

    public async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await ReadBoundedAsync(response.Content, policy.MaxRegistryJsonBytes, cancellationToken);
        return payload.Length == 0 ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await ReadBoundedAsync(response.Content, policy.MaxRegistryErrorBytes, cancellationToken);
            if (payload.Length > 0)
            {
                var problem = JsonSerializer.Deserialize<RegistryProblemDetails>(payload, JsonOptions);
                if (!string.IsNullOrWhiteSpace(problem?.Detail)) return problem.Detail;
                if (!string.IsNullOrWhiteSpace(problem?.Title)) return problem.Title;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "Registry rejected the request.",
            HttpStatusCode.Unauthorized => "Registry sign-in is required.",
            HttpStatusCode.Forbidden => "Registry operation is forbidden.",
            HttpStatusCode.NotFound => "Registry resource was not found.",
            HttpStatusCode.Conflict => "Registry operation conflicted with existing state.",
            _ => $"Registry request failed with HTTP {(int)response.StatusCode}.",
        };
    }

    public async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(
            await ReadErrorAsync(response, cancellationToken),
            inner: null,
            response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (content.Headers.ContentLength is { } declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidDataException($"HTTP response payload exceeds the {maxBytes} byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maxBytes - destination.Length;
            var readLength = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
            if (read == 0) return destination.ToArray();
            if (read > remaining)
            {
                throw new InvalidDataException($"HTTP response payload exceeds the {maxBytes} byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }

    private static void EnsureResponseOrigin(HttpRequestMessage request, HttpResponseMessage response)
    {
        var expected = request.RequestUri;
        var actual = response.RequestMessage?.RequestUri ?? expected;
        if (expected is not null
            && actual is not null
            && string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            && expected.Port == actual.Port
            && string.IsNullOrEmpty(actual.UserInfo))
        {
            return;
        }

        response.Dispose();
        throw new InvalidDataException("Registry response changed to an untrusted origin.");
    }

    private sealed class DeadlineHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationToken _callerToken;
        private readonly TimeSpan _timeoutDuration;

        public DeadlineHttpContent(
            HttpContent inner,
            CancellationTokenSource deadline,
            CancellationTokenSource timeout,
            CancellationToken callerToken,
            TimeSpan timeoutDuration)
        {
            _inner = inner;
            _deadline = deadline;
            _timeout = timeout;
            _callerToken = callerToken;
            _timeoutDuration = timeoutDuration;
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
            => await CreateStreamAsync(CancellationToken.None).ConfigureAwait(false);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => await CreateStreamAsync(cancellationToken).ConfigureAwait(false);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        private async Task<Stream> CreateStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _deadline.Token);
                return new DeadlineReadStream(
                    await _inner.ReadAsStreamAsync(readDeadline.Token).ConfigureAwait(false),
                    _deadline.Token,
                    _timeout.Token,
                    _callerToken,
                    _timeoutDuration);
            }
            catch (OperationCanceledException exception) when (TimedOut)
            {
                throw Timeout(exception);
            }
        }

        private async Task CopyToWithDeadlineAsync(Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                using var copyDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _deadline.Token);
                await _inner.CopyToAsync(stream, copyDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (TimedOut)
            {
                throw Timeout(exception);
            }
        }

        private bool TimedOut => _timeout.IsCancellationRequested && !_callerToken.IsCancellationRequested;

        private TimeoutException Timeout(OperationCanceledException exception)
            => new($"Registry response body exceeded the {_timeoutDuration} timeout.", exception);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _deadline.Dispose();
                _timeout.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DeadlineReadStream(
        Stream inner,
        CancellationToken deadlineToken,
        CancellationToken timeoutToken,
        CancellationToken callerToken,
        TimeSpan timeoutDuration) : Stream
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
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    deadlineToken);
                return await inner.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (TimedOut)
            {
                throw new TimeoutException(
                    $"Registry response body exceeded the {timeoutDuration} timeout.",
                    exception);
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        private bool TimedOut => timeoutToken.IsCancellationRequested && !callerToken.IsCancellationRequested;

        private void ThrowIfDeadlineExceeded()
        {
            if (TimedOut)
            {
                throw new TimeoutException($"Registry response body exceeded the {timeoutDuration} timeout.");
            }
            deadlineToken.ThrowIfCancellationRequested();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
