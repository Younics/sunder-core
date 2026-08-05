using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal static class SunderWorkerProtocol
{
    public const string Name = "sunder.worker.v1";
    public const int Version = 1;
    public const string V2Capability = "worker-protocol.v2";

    public static SunderWorkerProtocolIdentity V1 { get; } = new(Name, Version, UsesV2Lifecycle: false);
    public static SunderWorkerProtocolIdentity V2 { get; } = new("sunder.worker.v2", 2, UsesV2Lifecycle: true);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<JsonDocument> ReadFrameAsync(
        Stream stream,
        RuntimeProcessPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[policy.MaxHeaderBytes];
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
            || contentLength > policy.MaxFrameBytes)
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
                ValidateJsonTokens(utf8, policy.MaxMessageDepth);
                return JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = policy.MaxMessageDepth,
                });
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

    public static byte[] SerializeFrameBody(object value, RuntimeProcessPolicyOptions policy)
    {
        byte[] body;
        try
        {
            body = JsonSerializer.SerializeToUtf8Bytes(value, WorkerJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SunderWorkerProtocolException("Host could not serialize a worker protocol envelope.", exception);
        }
        if (body.Length == 0 || body.Length > policy.MaxFrameBytes)
        {
            throw new SunderWorkerProtocolException("Host worker protocol frame exceeded the configured limit.");
        }
        return body;
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
            || !value.TryGetInt64(out var result)
            || result < 0)
        {
            throw new SunderWorkerProtocolException(
                $"Worker envelope property '{propertyName}' must be a non-negative integer.");
        }
        return result;
    }

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

    private sealed class JsonObjectNames
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoreCase { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record SunderWorkerProtocolIdentity(string Name, int Version, bool UsesV2Lifecycle);

internal static class WorkerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
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
