using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal static class SunderWorkerProtocol
{
    public const string V1Capability = "worker-protocol.v1";
    public const string Name = "sunder.worker.v1";
    public const int Version = 1;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public const int EnvelopeReserveBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ValueTask<JsonDocument> ReadFrameAsync(
        Stream stream,
        RuntimeProcessPolicyOptions policy,
        CancellationToken cancellationToken)
        => ReadFrameAsync(
            stream,
            CreateTransportLimits(
                policy.EffectiveResourcePolicy ?? RuntimeResourcePolicies.Standard,
                policy),
            cancellationToken);

    public static async ValueTask<JsonDocument> ReadFrameAsync(
        Stream stream,
        WorkerTransportLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[limits.MaximumHeaderBytes];
        var count = 0;
        while (true)
        {
            if (count == header.Length)
            {
                throw new SunderWorkerProtocolException("Worker frame header exceeded the configured limit.");
            }
            var read = await stream.ReadAsync(header.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(count == 0
                    ? "Worker protocol stream closed."
                    : "Worker protocol stream closed in a frame header.");
            }
            count++;
            if (count >= 4
                && header[count - 4] == (byte)'\r'
                && header[count - 3] == (byte)'\n'
                && header[count - 2] == (byte)'\r'
                && header[count - 1] == (byte)'\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(header, 0, count - 4);
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
            || contentLength > limits.MaximumFrameBytes)
        {
            throw new SunderWorkerProtocolException("Worker frame must contain exactly one canonical bounded Content-Length header.");
        }

        var body = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await stream.ReadExactlyAsync(body.AsMemory(0, contentLength), cancellationToken).ConfigureAwait(false);
            var utf8 = body.AsSpan(0, contentLength);
            if (utf8.StartsWith(Encoding.UTF8.Preamble))
            {
                throw new SunderWorkerProtocolException("Worker JSON frames must not contain a UTF-8 byte-order mark.");
            }
            try
            {
                _ = StrictUtf8.GetString(utf8);
                ValidateJsonTokens(utf8, limits.MaximumMessageDepth);
                var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaximumMessageDepth,
                });
                try
                {
                    ValidateEnvelopeBounds(document.RootElement, contentLength, limits);
                    return document;
                }
                catch
                {
                    document.Dispose();
                    throw;
                }
            }
            catch (SunderWorkerProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
            {
                throw new SunderWorkerProtocolException("Worker frame is not strict UTF-8 JSON.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    public static byte[] SerializeFrameBody(object value, WorkerTransportLimits limits)
    {
        using var buffer = new FixedPooledBufferWriter(limits.MaximumFrameBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = WorkerJson.Encoder,
                Indented = false,
                MaxDepth = limits.MaximumMessageDepth,
                SkipValidation = false,
            }))
            {
                JsonSerializer.Serialize(writer, value, value.GetType(), WorkerJson.Options);
                writer.Flush();
            }

            if (buffer.WrittenCount == 0)
            {
                throw new WorkerTransportResourceException(
                    WorkerTransportFailureKind.FrameEncoding, "Host worker protocol envelope serialized to an empty frame.");
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
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new SunderWorkerProtocolException("Host could not serialize a worker protocol envelope.", exception);
        }
    }

    public static WorkerTransportLimits CreateTransportLimits(
        RuntimeResourcePolicy resourcePolicy,
        RuntimeProcessPolicyOptions processPolicy)
        => WorkerTransportLimits.Create(resourcePolicy, processPolicy.MaxHeaderBytes);

    public static object ResourceProfileToWire(RuntimeResourcePolicy policy)
        => new
        {
            version = RuntimeResourcePolicyContract.EffectiveProfileFormatVersion,
            rpcValueBytes = policy.MaximumRpcValueBytes,
            valueDepth = policy.MaximumValueDepth,
            valueNodes = policy.MaximumValueNodes,
            objectMembers = policy.MaximumObjectMembers,
            inlineDecodedBytes = policy.MaximumInlineDecodedBytes,
            contentReferenceBytes = policy.MaximumContentReferenceBytes,
        };

    public static int GetSerializedValueByteCount(JsonElement value, WorkerTransportLimits limits)
        => SerializeValue(value, limits).Length;

    public static JsonElement CloneValueForQueue(
        JsonElement value,
        WorkerTransportLimits limits,
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

    private static byte[] SerializeValue(JsonElement value, WorkerTransportLimits limits)
    {
        ValidateValueBounds(value, limits);
        using var buffer = new FixedPooledBufferWriter(limits.MaximumRpcValueBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = WorkerJson.Encoder,
                Indented = false,
                MaxDepth = limits.MaximumValueDepth,
                SkipValidation = false,
            }))
            {
                value.WriteTo(writer);
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
                WorkerTransportFailureKind.ValueEncoding, "RPC value exceeds the negotiated worker transport limits.",
                innerException: exception);
        }
    }

    public static async Task WriteFrameBodyAsync(
        Stream stream,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string ReadEnvelopeType(JsonElement root)
    {
        RequireObject(root, "worker envelope");
        return RequiredString(root, "type", 64);
    }

    public static string RequiredId(JsonElement root)
    {
        var id = RequiredString(root, "id", 128);
        if (!id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new SunderWorkerProtocolException("Worker message id contains unsupported characters.");
        }
        return id;
    }

    public static string RequiredString(JsonElement root, string propertyName, int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString())
            || value.GetString()!.Length > maximumLength)
        {
            throw new SunderWorkerProtocolException(
                $"Worker envelope property '{propertyName}' must be a non-empty bounded string.");
        }
        return value.GetString()!;
    }

    public static long RequiredNonNegativeInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !IsCanonicalNonNegativeIntegerToken(value.GetRawText())
            || !value.TryGetInt64(out var result)
            || result is < 0 or > MaximumSafeInteger)
        {
            throw new SunderWorkerProtocolException(
                $"Worker envelope property '{propertyName}' must be an integer from 0 through {MaximumSafeInteger}.");
        }
        return result;
    }

    public static int RequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !IsCanonicalIntegerToken(value.GetRawText())
            || !value.TryGetInt32(out var result))
        {
            throw new SunderWorkerProtocolException(
                $"Worker envelope property '{propertyName}' must be a signed 32-bit integer.");
        }
        return result;
    }

    public static bool IsCanonicalNonNegativeIntegerToken(string value)
        => value.Length > 0
           && (value == "0"
               || value[0] is >= '1' and <= '9'
               && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0);

    private static bool IsCanonicalIntegerToken(string value)
        => IsCanonicalNonNegativeIntegerToken(value)
           || value.Length > 1
           && value[0] == '-'
           && value[1] is >= '1' and <= '9'
           && value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    public static JsonElement RequiredValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new SunderWorkerProtocolException($"Worker envelope is missing property '{propertyName}'.");
        }
        return value;
    }

    public static void RequireOnlyProperties(JsonElement root, params string[] allowedProperties)
    {
        RequireObject(root, "worker envelope");
        var allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new SunderWorkerProtocolException(
                    $"Worker envelope type contains unsupported property '{property.Name}'.");
            }
        }
    }

    public static void RequireObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new SunderWorkerProtocolException($"{label} must be a JSON object.");
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
                                ?? throw new SunderWorkerProtocolException("A worker JSON property appeared outside an object.");
                    var name = reader.GetString()!;
                    if (!names.Exact.Add(name) || !names.IgnoreCase.Add(name))
                    {
                        throw new SunderWorkerProtocolException(
                            $"Worker JSON contains duplicate or case-colliding property '{name}'.");
                    }
                    break;
                default:
                    if (scopes.Count == 0) roots++;
                    break;
            }
        }
        if (roots != 1)
        {
            throw new SunderWorkerProtocolException("Worker frame must contain exactly one JSON value.");
        }
    }

    private static void ValidateEnvelopeBounds(
        JsonElement root,
        int bodyBytes,
        WorkerTransportLimits limits)
    {
        RequireObject(root, "worker envelope");
        JsonElement rpcValue = default;
        var hasRpcValue = root.TryGetProperty("request", out rpcValue)
                          || root.TryGetProperty("value", out rpcValue)
                          || TryGetErrorDetails(root, out rpcValue);
        var valueBytes = hasRpcValue
            ? StrictUtf8.GetByteCount(rpcValue.GetRawText())
            : 0;
        if (valueBytes > limits.MaximumRpcValueBytes)
        {
            throw new WorkerTransportResourceException(
                WorkerTransportFailureKind.ValueBytes, "Worker RPC value exceeded the negotiated byte limit.", valueBytes, limits.MaximumRpcValueBytes);
        }
        if (bodyBytes - valueBytes > EnvelopeReserveBytes)
        {
            throw new WorkerTransportResourceException(
                WorkerTransportFailureKind.EnvelopeBytes, "Worker protocol envelope metadata exceeded its reserved byte limit.", bodyBytes - valueBytes, EnvelopeReserveBytes);
        }
        if (hasRpcValue) ValidateValueBounds(rpcValue, limits);
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

    private static void ValidateValueBounds(JsonElement value, WorkerTransportLimits limits)
    {
        var maximum = 0;
        var nodes = 0;
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((value, 1));
        while (pending.TryPop(out var item))
        {
            maximum = Math.Max(maximum, item.Depth);
            if (maximum > limits.MaximumValueDepth)
            {
                throw new WorkerTransportResourceException(
                    WorkerTransportFailureKind.ValueDepth, "Worker RPC value exceeded the negotiated depth limit.", maximum, limits.MaximumValueDepth);
            }
            if (++nodes > limits.MaximumValueNodes)
            {
                throw new WorkerTransportResourceException(
                    WorkerTransportFailureKind.ValueNodes, "Worker RPC value exceeded the negotiated node-count limit.", nodes, limits.MaximumValueNodes);
            }
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.Value.EnumerateArray())
                {
                    pending.Push((child, item.Depth + 1));
                }
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                var members = 0;
                foreach (var property in item.Value.EnumerateObject())
                {
                    if (++members > limits.MaximumObjectMembers)
                    {
                        throw new WorkerTransportResourceException(
                            WorkerTransportFailureKind.ObjectMembers, "Worker RPC value exceeded the negotiated object-member limit.", members, limits.MaximumObjectMembers);
                    }
                    pending.Push((property.Value, item.Depth + 1));
                }
            }
        }
    }

    private sealed class JsonObjectNames
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoreCase { get; } = new(AsciiCaseInsensitiveComparer.Instance);
    }

    private sealed class AsciiCaseInsensitiveComparer : IEqualityComparer<string>
    {
        public static AsciiCaseInsensitiveComparer Instance { get; } = new();

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

    private sealed class FixedPooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly int _capacity;
        private byte[]? _buffer;

        public FixedPooledBufferWriter(int capacity)
        {
            _capacity = capacity;
            _buffer = ArrayPool<byte>.Shared.Rent(checked(capacity + EnvelopeReserveBytes));
        }

        public int WrittenCount { get; private set; }
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WrittenCount);

        public void Advance(int count)
        {
            if (count < 0 || count > _capacity - WrittenCount)
            {
                throw new WorkerTransportResourceException(
                    WorkerTransportFailureKind.FrameBytes, "Worker protocol frame exceeded the negotiated byte limit.", (long)WrittenCount + count, _capacity);
            }
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsSpan(WrittenCount);
        }

        public void Dispose()
        {
            if (_buffer is null) return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        private void EnsureAvailable(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (_buffer is null || sizeHint > _buffer.Length - WrittenCount)
            {
                throw new WorkerTransportResourceException(
                    WorkerTransportFailureKind.FrameBytes, "Worker protocol frame exceeded the negotiated byte limit.", (long)WrittenCount + sizeHint, _capacity);
            }
        }
    }
}

internal sealed record WorkerTransportLimits(
    int MaximumRpcValueBytes,
    int MaximumValueDepth,
    int MaximumValueNodes,
    int MaximumObjectMembers,
    long MaximumContentReferenceBytes,
    int MaximumFrameBytes,
    int MaximumHeaderBytes,
    int MaximumMessageDepth,
    long MaximumWriteQueueBytes,
    long MaximumStreamQueueBytes)
{
    public static WorkerTransportLimits Create(RuntimeResourcePolicy policy, int maximumHeaderBytes)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (maximumHeaderBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        }

        return new WorkerTransportLimits(
            policy.MaximumRpcValueBytes,
            policy.MaximumValueDepth,
            policy.MaximumValueNodes,
            policy.MaximumObjectMembers,
            policy.MaximumContentReferenceBytes,
            checked(policy.MaximumRpcValueBytes + SunderWorkerProtocol.EnvelopeReserveBytes),
            maximumHeaderBytes,
            checked(policy.MaximumValueDepth + 8),
            checked((long)policy.MaximumRpcValueBytes * 8),
            checked((long)policy.MaximumRpcValueBytes * 2));
    }
}

internal static class WorkerJson
{
    public static JavaScriptEncoder Encoder { get; } = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = Encoder,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
}

internal class ProcessRuntimeWorkerException : Exception
{
    public ProcessRuntimeWorkerException(string message)
        : base(message)
    {
    }

    public ProcessRuntimeWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class SunderWorkerProtocolException : ProcessRuntimeWorkerException
{
    public SunderWorkerProtocolException(string message)
        : base(message)
    {
    }

    public SunderWorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal enum WorkerTransportFailureKind
{
    Unknown, FrameEncoding, FrameBytes, EnvelopeBytes, ValueEncoding, ValueBytes,
    ValueDepth, ValueNodes, ObjectMembers, WriteQueueAdmission, WriteQueueClosed,
}

internal sealed class WorkerTransportResourceException : ProcessRuntimeWorkerException
{
    internal WorkerTransportFailureKind Kind { get; }
    internal long? Observed { get; }
    internal long? Limit { get; }

    internal WorkerTransportResourceException(WorkerTransportFailureKind kind, string message,
        long? observed = null, long? limit = null, Exception? innerException = null)
        : base(message, innerException!)
    { Kind = kind; Observed = observed; Limit = limit; }

    public WorkerTransportResourceException(string message)
        : base(message)
    {
    }

    public WorkerTransportResourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
