using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Sunder.Sdk.Worker.Internal;

internal static class WorkerProtocol
{
    public const string Name = "sunder.worker.v3";
    public const int Version = 3;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public const int EnvelopeReserveBytes = 64 * 1024;

}

internal sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class WorkerTransportResourceException : Exception
{
    public WorkerTransportResourceException(string message)
        : base(message)
    {
    }

    public WorkerTransportResourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class WorkerAsciiCaseInsensitiveComparer : IEqualityComparer<string>
{
    public static WorkerAsciiCaseInsensitiveComparer Instance { get; } = new();

    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (Fold(left[index]) != Fold(right[index])) return false;
        }
        return true;
    }

    public int GetHashCode(string value)
    {
        var hash = new HashCode();
        foreach (var character in value) hash.Add(Fold(character));
        return hash.ToHashCode();
    }

    private static char Fold(char value)
        => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
}

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

internal static class WorkerJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static JavaScriptEncoder Encoder { get; } = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static byte[] Serialize(WorkerLimits limits, Action<Utf8JsonWriter> write)
    {
        using var buffer = new FixedPooledBufferWriter(limits.MaximumFrameBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                MaxDepth = limits.MaximumMessageDepth,
                Encoder = Encoder,
            }))
            {
                write(writer);
                writer.Flush();
            }
            if (buffer.WrittenCount == 0)
            {
                throw new WorkerProtocolException("Worker protocol envelope serialized to an empty frame.");
            }
            var body = buffer.WrittenSpan.ToArray();
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaximumMessageDepth,
            });
            ValidateEnvelopeBounds(document.RootElement, body.Length, limits);
            return body;
        }
        catch (WorkerTransportResourceException)
        {
            throw;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new WorkerProtocolException("Worker could not serialize a protocol envelope.", exception);
        }
    }

    public static int GetSerializedValueByteCount(JsonElement value, WorkerLimits limits)
        => SerializeValue(value, limits).Length;

    public static JsonElement CloneValueForQueue(
        JsonElement value,
        WorkerLimits limits,
        out int serializedBytes)
    {
        var body = SerializeValue(value, limits);
        serializedBytes = body.Length;
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = limits.MaximumValueDepth,
        });
        return document.RootElement.Clone();
    }

    private static byte[] SerializeValue(JsonElement value, WorkerLimits limits)
    {
        using var buffer = new FixedPooledBufferWriter(limits.MaximumRpcValueBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = Encoder,
                Indented = false,
                SkipValidation = false,
                MaxDepth = limits.MaximumValueDepth,
            }))
            {
                WriteJsonValue(writer, value);
                writer.Flush();
            }
            return buffer.WrittenSpan.ToArray();
        }
        catch (WorkerTransportResourceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new WorkerTransportResourceException(
                "RPC value exceeds the negotiated worker transport limits.",
                exception);
        }
    }

    public static void ValidateEnvelopeBounds(JsonElement root, int bodyBytes, WorkerLimits limits)
    {
        RequireObject(root, "protocol envelope");
        JsonElement rpcValue = default;
        var hasRpcValue = root.TryGetProperty("request", out rpcValue)
                          || root.TryGetProperty("value", out rpcValue)
                          || TryGetErrorDetails(root, out rpcValue);
        var valueBytes = hasRpcValue ? StrictUtf8.GetByteCount(rpcValue.GetRawText()) : 0;
        if (valueBytes > limits.MaximumRpcValueBytes)
        {
            throw new WorkerTransportResourceException(
                "RPC value exceeded the negotiated byte limit.");
        }
        if (bodyBytes - valueBytes > WorkerProtocol.EnvelopeReserveBytes)
        {
            throw new WorkerTransportResourceException(
                "Protocol envelope metadata exceeded its reserved byte limit.");
        }
        if (hasRpcValue && GetValueDepth(rpcValue) > limits.MaximumValueDepth)
        {
            throw new WorkerTransportResourceException(
                "RPC value exceeded the negotiated depth limit.");
        }
    }

    private static bool TryGetErrorDetails(JsonElement root, out JsonElement details)
    {
        if (root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("details", out details))
        {
            return true;
        }
        details = default;
        return false;
    }

    public static string ReadEnvelopeType(JsonElement root)
    {
        RequireObject(root, "Host envelope");
        return RequiredString(root, "type", 64);
    }

    public static void RequireOnlyProperties(JsonElement root, params string[] allowedProperties)
    {
        RequireObject(root, "Host envelope");
        var allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new WorkerProtocolException(
                    $"Host envelope contains unsupported property '{property.Name}'.");
            }
        }
    }

    public static void RequireObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkerProtocolException($"{label} must be a JSON object.");
        }
    }

    public static JsonElement RequiredValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new WorkerProtocolException($"Host envelope is missing property '{propertyName}'.");
        }
        return value;
    }

    public static string RequiredString(JsonElement root, string propertyName, int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString())
            || value.GetString()!.Length > maximumLength)
        {
            throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' must be a non-empty bounded string.");
        }
        return value.GetString()!;
    }

    public static string? OptionalStringOrNull(JsonElement root, string propertyName, int maximumLength)
    {
        var value = RequiredValue(root, propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString())
            || value.GetString()!.Length > maximumLength)
        {
            throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' must be null or a non-empty bounded string.");
        }
        return value.GetString();
    }

    public static string RequiredSafeId(JsonElement root, string propertyName, int maximumLength = 128)
    {
        var id = RequiredString(root, propertyName, maximumLength);
        if (!IsSafeId(id))
        {
            throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' contains unsupported characters.");
        }
        return id;
    }

    public static bool IsSafeId(string value)
        => value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static long RequiredNonNegativeInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result is < 0 or > WorkerProtocol.MaximumSafeInteger)
        {
            throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' must be an integer from 0 through {WorkerProtocol.MaximumSafeInteger}.");
        }
        return result;
    }

    public static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        var value = RequiredValue(root, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' must be a Boolean."),
        };
    }

    public static DateTimeOffset RequiredUtcTimestamp(JsonElement root, string propertyName)
    {
        var value = RequiredString(root, propertyName, 64);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp)
            || timestamp.Offset != TimeSpan.Zero)
        {
            throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' must be a round-trip UTC timestamp.");
        }
        return timestamp;
    }

    public static bool IsSafeErrorCode(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    public static void WriteJsonValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    writer.WriteStartObject();
                    var exact = new HashSet<string>(StringComparer.Ordinal);
                    var ignoreCase = new HashSet<string>(WorkerAsciiCaseInsensitiveComparer.Instance);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!exact.Add(property.Name) || !ignoreCase.Add(property.Name))
                        {
                            throw new InvalidOperationException(
                                $"JSON contains duplicate or case-colliding property '{property.Name}'.");
                        }
                        writer.WritePropertyName(property.Name);
                        WriteJsonValue(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    return;
                }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteJsonValue(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValueKind.Undefined:
                throw new InvalidOperationException("Provider output cannot be an undefined JSON value.");
            default:
                value.WriteTo(writer);
                return;
        }
    }

    public static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static int GetValueDepth(JsonElement value)
    {
        var maximum = 0;
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((value, 1));
        while (pending.TryPop(out var item))
        {
            maximum = Math.Max(maximum, item.Depth);
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.Value.EnumerateArray()) pending.Push((child, item.Depth + 1));
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.Value.EnumerateObject())
                {
                    pending.Push((property.Value, item.Depth + 1));
                }
            }
        }
        return maximum;
    }

    private sealed class FixedPooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[]? _buffer;
        private readonly int _capacity;

        public FixedPooledBufferWriter(int capacity)
        {
            _capacity = capacity;
            _buffer = ArrayPool<byte>.Shared.Rent(checked(capacity + WorkerProtocol.EnvelopeReserveBytes));
        }

        public int WrittenCount { get; private set; }
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WrittenCount);

        public void Advance(int count)
        {
            if (count < 0 || count > _capacity - WrittenCount)
            {
                throw new WorkerTransportResourceException("Worker protocol frame exceeded the configured limit.");
            }
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(WrittenCount);
        }

        public void Dispose()
        {
            if (_buffer is null) return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (_buffer is null || sizeHint > _buffer.Length - WrittenCount)
            {
                throw new WorkerTransportResourceException("Worker protocol frame exceeded the configured limit.");
            }
        }
    }
}
