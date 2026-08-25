using System.Text;
using System.Text.Json;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerPayloadAllocation(string HandleId, string FilePath, long Length);

internal sealed record WorkerDataPayload(string HandleId, string FilePath, long Length, string? Sha256);

internal sealed record WorkerDataValue(bool IsInline, string? Value, WorkerDataPayload? Payload);

internal sealed record WorkerDataKeys(IReadOnlyList<string>? Keys, WorkerDataPayload? Payload);

internal static class WorkerPackagePayloadCodec
{
    internal const int InlineDataUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictDataUtf8 = new(false, true);

    public static WorkerPayloadAllocation ReadPayloadAllocation(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireOnlyProperties(value, "handleId", "filePath", "length");
        var length = WorkerJson.RequiredNonNegativeInt64(value, "length");
        if (length > maximumPayloadBytes)
        {
            throw new WorkerProtocolException("Host payload allocation exceeds the configured limit.");
        }
        return new WorkerPayloadAllocation(
            WorkerJson.RequiredSafeId(value, "handleId"),
            WorkerJson.RequiredString(value, "filePath", 4096),
            length);
    }

    public static WorkerDataValue ReadDataValue(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireObject(value, "Host package-data result");
        var kind = WorkerJson.RequiredString(value, "kind", 32);
        if (string.Equals(kind, "inline", StringComparison.Ordinal))
        {
            WorkerJson.RequireOnlyProperties(value, "kind", "value");
            var item = WorkerJson.RequiredValue(value, "value");
            if (item.ValueKind == JsonValueKind.Null) return new WorkerDataValue(true, null, null);
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new WorkerProtocolException("Host inline package-data value must be a string or null.");
            }
            var text = item.GetString()!;
            if (StrictDataUtf8.GetByteCount(text) > InlineDataUtf8Bytes)
            {
                throw new WorkerProtocolException("Host inline package-data value exceeds the inline transport limit.");
            }
            if (!PackageStorageValidation.IsValidValue(text))
            {
                throw new WorkerProtocolException("Host inline package-data value exceeds the package value limit.");
            }
            return new WorkerDataValue(true, text, null);
        }
        if (!string.Equals(kind, "payload", StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Host package-data result kind is invalid.");
        }
        return new WorkerDataValue(false, null, ReadDataPayload(value, maximumPayloadBytes));
    }

    public static WorkerDataKeys ReadDataKeys(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireObject(value, "Host package-state list result");
        var kind = WorkerJson.RequiredString(value, "kind", 32);
        if (string.Equals(kind, "inline", StringComparison.Ordinal))
        {
            WorkerJson.RequireOnlyProperties(value, "kind", "keys");
            var keysValue = WorkerJson.RequiredValue(value, "keys");
            if (keysValue.ValueKind != JsonValueKind.Array)
            {
                throw new WorkerProtocolException("Host inline package-state keys must be an array.");
            }
            var keys = keysValue.EnumerateArray().Select(static item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new WorkerProtocolException("Host inline package-state keys contain a non-string value.");
                }
                return item.GetString()!;
            }).ToArray();
            if (StrictDataUtf8.GetByteCount(keysValue.GetRawText()) > InlineDataUtf8Bytes)
            {
                throw new WorkerProtocolException("Host inline package-state keys exceed the inline transport limit.");
            }
            return new WorkerDataKeys(keys, null);
        }
        if (!string.Equals(kind, "payload", StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Host package-state list result kind is invalid.");
        }
        return new WorkerDataKeys(null, ReadDataPayload(value, maximumPayloadBytes));
    }

    public static WorkerDataPayload? ReadFilePayload(JsonElement value, int maximumPayloadBytes)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        WorkerJson.RequireObject(value, "Host package-file result");
        if (!string.Equals(WorkerJson.RequiredString(value, "kind", 32), "payload", StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Host package-file result kind is invalid.");
        }
        return ReadDataPayload(value, maximumPayloadBytes);
    }

    public static string DecodeDataString(byte[] bytes)
    {
        string decoded;
        try
        {
            decoded = StrictDataUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new WorkerProtocolException(
                "Host package-data payload is not strict UTF-8.",
                exception);
        }
        if (!PackageStorageValidation.IsValidValue(decoded))
        {
            throw new WorkerProtocolException(
                "Host package-data payload exceeds the package value limit.");
        }
        return decoded;
    }

    public static IReadOnlyList<string> ReadPayloadKeys(byte[] bytes, int maximumDepth)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new WorkerProtocolException("Host package-state key payload must be a JSON array.");
            }
            var keys = document.RootElement.EnumerateArray().Select(static item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new WorkerProtocolException("Host package-state key payload contains a non-string value.");
                }
                return item.GetString()!;
            }).ToArray();
            return ValidateReturnedKeys(keys);
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new WorkerProtocolException(
                "Host package-state key payload is not strict JSON.",
                exception);
        }
    }

    public static IReadOnlyList<string> ValidateReturnedKeys(IReadOnlyList<string> keys)
    {
        string? previous = null;
        foreach (var key in keys)
        {
            if (!PackageStorageValidation.IsValidKey(key)
                || previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0)
            {
                throw new WorkerProtocolException(
                    "Host package-state keys are invalid, duplicated, or not ordinally sorted.");
            }
            previous = key;
        }
        return keys;
    }

    public static void WriteInlineDataValue(Utf8JsonWriter writer, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "inline");
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    public static void WriteUploadDataValue(
        Utf8JsonWriter writer,
        string handleId,
        long length,
        string sha256)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "payload");
        writer.WriteString("handleId", handleId);
        writer.WriteNumber("length", length);
        writer.WriteString("sha256", sha256);
        writer.WriteEndObject();
    }

    private static WorkerDataPayload ReadDataPayload(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireOnlyProperties(value, "kind", "handleId", "filePath", "length", "sha256");
        var length = WorkerJson.RequiredNonNegativeInt64(value, "length");
        if (length > maximumPayloadBytes)
        {
            throw new WorkerProtocolException("Host package-data payload exceeds the configured limit.");
        }
        var sha256 = WorkerJson.RequiredString(value, "sha256", 64);
        if (sha256.Length != 64 || sha256.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new WorkerProtocolException("Host package-data payload hash is invalid.");
        }
        return new WorkerDataPayload(
            WorkerJson.RequiredSafeId(value, "handleId"),
            WorkerJson.RequiredString(value, "filePath", 4096),
            length,
            sha256);
    }
}
