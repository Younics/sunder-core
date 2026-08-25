using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class WorkerFrameReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _stream;
    private readonly WorkerLimits _limits;
    private readonly byte[] _header;

    public WorkerFrameReader(Stream stream, WorkerLimits limits)
    {
        _stream = stream;
        _limits = limits;
        _header = new byte[limits.MaximumHeaderBytes];
    }

    public async ValueTask<JsonDocument> ReadAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (true)
        {
            if (count == _header.Length)
            {
                throw new WorkerProtocolException("Host frame header exceeded the configured limit.");
            }

            var read = await _stream.ReadAsync(_header.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(count == 0
                    ? "Host protocol stream closed before shutdown completed."
                    : "Host protocol stream closed in a frame header.");
            }

            count++;
            if (count >= 4
                && _header[count - 4] == (byte)'\r'
                && _header[count - 3] == (byte)'\n'
                && _header[count - 2] == (byte)'\r'
                && _header[count - 1] == (byte)'\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(_header, 0, count - 4);
        const string prefix = "Content-Length: ";
        if (!headerText.StartsWith(prefix, StringComparison.Ordinal)
            || headerText.Length == prefix.Length
            || headerText.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') >= 0
            || headerText[prefix.Length] == '0' && headerText.Length != prefix.Length + 1
            || !int.TryParse(
                headerText.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var contentLength)
            || contentLength <= 0
            || contentLength > _limits.MaximumFrameBytes)
        {
            throw new WorkerProtocolException(
                "Host frame must contain exactly one canonical bounded Content-Length header.");
        }

        var body = new byte[contentLength];
        try
        {
            await _stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            if (body.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                throw new WorkerProtocolException("Host JSON frames must not contain a UTF-8 byte-order mark.");
            }

            _ = StrictUtf8.GetCharCount(body);
            ValidateJsonTokens(body, _limits.MaximumMessageDepth);
            var document = JsonDocument.Parse(body.AsMemory(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = _limits.MaximumMessageDepth,
            });
            try
            {
                WorkerJson.ValidateEnvelopeBounds(document.RootElement, body.Length, _limits);
                return document;
            }
            catch (WorkerTransportResourceException exception)
            {
                document.Dispose();
                throw new WorkerProtocolException(
                    "Host envelope exceeds the negotiated worker transport limits.",
                    exception);
            }
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new WorkerProtocolException("Host frame is not strict UTF-8 JSON.", exception);
        }
    }

    private static void ValidateJsonTokens(ReadOnlySpan<byte> utf8, int maximumDepth)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = maximumDepth,
        });
        var scopes = new Stack<JsonObjectNames?>();
        var roots = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (scopes.Count == 0) roots++;
                    scopes.Push(new JsonObjectNames());
                    break;
                case JsonTokenType.StartArray:
                    if (scopes.Count == 0) roots++;
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var names = scopes.Peek()
                                ?? throw new WorkerProtocolException("A Host JSON property appeared outside an object.");
                    var name = reader.GetString()!;
                    if (!names.Exact.Add(name) || !names.IgnoreCase.Add(name))
                    {
                        throw new WorkerProtocolException(
                            $"Host JSON contains duplicate or case-colliding property '{name}'.");
                    }
                    break;
                case JsonTokenType.Number:
                    if (!reader.TryGetDouble(out var number) || !double.IsFinite(number))
                    {
                        throw new WorkerProtocolException("Host JSON numbers must be finite IEEE 754 values.");
                    }
                    if (scopes.Count == 0) roots++;
                    break;
                default:
                    if (scopes.Count == 0) roots++;
                    break;
            }
        }

        if (roots != 1)
        {
            throw new WorkerProtocolException("Host frame must contain exactly one JSON value.");
        }
    }

    private sealed class JsonObjectNames
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoreCase { get; } = new(WorkerAsciiCaseInsensitiveComparer.Instance);
    }
}

internal sealed class WorkerProtocolOutput
{
    private readonly Stream _stream;
    private readonly WorkerLimits _limits;
    private readonly CancellationToken _stopping;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly object _queueGate = new();
    private int _queuedWrites;
    private long _queuedBytes;

    public WorkerProtocolOutput(Stream stream, WorkerLimits limits, CancellationToken stopping)
    {
        _stream = stream;
        _limits = limits;
        _stopping = stopping;
    }

    public ValueTask WriteAsync(byte[] body)
    {
        if (body.Length == 0 || body.Length > _limits.MaximumFrameBytes)
        {
            throw new WorkerTransportResourceException("Worker protocol frame exceeded the configured limit.");
        }

        lock (_queueGate)
        {
            if (_queuedWrites >= _limits.MaximumWriteQueueMessages
                || body.Length > _limits.MaximumWriteQueueBytes - _queuedBytes)
            {
                throw new WorkerTransportResourceException(
                    "Worker protocol write queue exceeded its count or byte bound.");
            }
            _queuedWrites++;
            _queuedBytes += body.Length;
        }
        return WriteReservedAsync(body);
    }

    private async ValueTask WriteReservedAsync(byte[] body)
    {
        var dequeued = false;
        try
        {
            await _writer.WaitAsync(_stopping).ConfigureAwait(false);
            ReleaseQueueReservation(body.Length);
            dequeued = true;
            try
            {
                var header = Encoding.ASCII.GetBytes(
                    $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
                await _stream.WriteAsync(header, _stopping).ConfigureAwait(false);
                await _stream.WriteAsync(body, _stopping).ConfigureAwait(false);
                await _stream.FlushAsync(_stopping).ConfigureAwait(false);
            }
            finally
            {
                _writer.Release();
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            throw new WorkerProtocolException("Worker protocol output failed.", exception);
        }
        finally
        {
            if (!dequeued) ReleaseQueueReservation(body.Length);
        }
    }

    private void ReleaseQueueReservation(int bodyBytes)
    {
        lock (_queueGate)
        {
            _queuedWrites--;
            _queuedBytes -= bodyBytes;
            if (_queuedWrites < 0 || _queuedBytes < 0)
            {
                throw new InvalidOperationException("Worker write-queue accounting underflowed.");
            }
        }
    }
}

internal sealed class WorkerJsonEventQueue
{
    private readonly Channel<QueuedValue> _channel;
    private readonly int _maximumMessages;
    private readonly long _maximumBytes;
    private readonly object _gate = new();
    private int _queuedMessages;
    private long _queuedBytes;

    public WorkerJsonEventQueue(int maximumMessages, long maximumBytes)
    {
        _maximumMessages = maximumMessages;
        _maximumBytes = maximumBytes;
        _channel = Channel.CreateBounded<QueuedValue>(new BoundedChannelOptions(maximumMessages)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    public WorkerJsonEventQueueRejection? TryWrite(JsonElement value, int bytes)
    {
        lock (_gate)
        {
            var exceededLimit = WorkerJsonEventQueueLimit.None;
            if (_queuedMessages >= _maximumMessages) exceededLimit |= WorkerJsonEventQueueLimit.Count;
            if (bytes > _maximumBytes - _queuedBytes) exceededLimit |= WorkerJsonEventQueueLimit.Bytes;
            if (exceededLimit != WorkerJsonEventQueueLimit.None)
            {
                return new WorkerJsonEventQueueRejection(
                    exceededLimit,
                    _queuedMessages,
                    _queuedBytes,
                    _maximumMessages,
                    _maximumBytes,
                    bytes);
            }
            _queuedMessages++;
            _queuedBytes += bytes;
        }
        if (_channel.Writer.TryWrite(new QueuedValue(value, bytes))) return null;
        Release(bytes);
        throw new InvalidOperationException("Worker event queue rejected an admitted value.");
    }

    public async IAsyncEnumerable<JsonElement> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Release(item.Bytes);
            yield return item.Value;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

    public void Fail(Exception exception)
    {
        _channel.Writer.TryComplete(exception);
        while (_channel.Reader.TryRead(out var item)) Release(item.Bytes);
    }

    private void Release(int bytes)
    {
        lock (_gate)
        {
            _queuedMessages--;
            _queuedBytes -= bytes;
            if (_queuedMessages < 0 || _queuedBytes < 0)
            {
                throw new InvalidOperationException("Worker event-queue accounting underflowed.");
            }
        }
    }

    private sealed record QueuedValue(JsonElement Value, int Bytes);
}

[Flags]
internal enum WorkerJsonEventQueueLimit
{
    None = 0,
    Count = 1,
    Bytes = 2,
}

internal readonly record struct WorkerJsonEventQueueRejection(
    WorkerJsonEventQueueLimit Limit,
    int QueuedMessages,
    long QueuedBytes,
    int MaximumMessages,
    long MaximumBytes,
    int IncomingBytes);
