using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sunder.Sdk.Worker.Internal;

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
        ValidateValueBounds(value, limits);
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
            || !IsCanonicalNonNegativeIntegerToken(value.GetRawText())
            || !value.TryGetInt64(out var result)
            || result is < 0 or > WorkerProtocol.MaximumSafeInteger)
        {
            throw new WorkerProtocolException(
                $"Host envelope property '{propertyName}' must be an integer from 0 through {WorkerProtocol.MaximumSafeInteger}.");
        }
        return result;
    }

    internal static bool IsCanonicalNonNegativeIntegerToken(string value)
        => value.Length > 0
           && (value == "0"
               || value[0] is >= '1' and <= '9'
               && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0);

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

    private static void ValidateValueBounds(JsonElement value, WorkerLimits limits)
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
                    "RPC value exceeded the negotiated depth limit.");
            }
            if (++nodes > limits.MaximumValueNodes)
            {
                throw new WorkerTransportResourceException(
                    "RPC value exceeded the negotiated node-count limit.");
            }
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.Value.EnumerateArray()) pending.Push((child, item.Depth + 1));
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                var members = 0;
                foreach (var property in item.Value.EnumerateObject())
                {
                    if (++members > limits.MaximumObjectMembers)
                    {
                        throw new WorkerTransportResourceException(
                            "RPC value exceeded the negotiated object-member limit.");
                    }
                    pending.Push((property.Value, item.Depth + 1));
                }
            }
        }
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
