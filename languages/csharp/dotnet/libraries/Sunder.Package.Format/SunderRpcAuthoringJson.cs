using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Format;

internal static class SunderRpcAuthoringJson
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const int MaximumValuesAndProperties = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static JsonDocument Parse(ReadOnlySpan<byte> bytes, int maximumBytes)
        => ParseCore(bytes, maximumBytes, allowNull: false);

    // Registry DTO projections can contain nullable presentation/lifecycle fields.
    // This reader is not used for source locks, inventories or signed envelopes.
    internal static JsonDocument ParseRegistryMetadata(ReadOnlySpan<byte> bytes, int maximumBytes)
        => ParseCore(bytes, maximumBytes, allowNull: true);

    private static JsonDocument ParseCore(ReadOnlySpan<byte> bytes, int maximumBytes, bool allowNull)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (bytes.Length == 0 || bytes.Length > maximumBytes || bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            throw new InvalidDataException("RPC authoring JSON must be bounded UTF-8 without a BOM.");

        JsonDocument? document = null;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var valuesAndProperties = 0;
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject)
                    && ++valuesAndProperties > MaximumValuesAndProperties)
                    throw new InvalidDataException("RPC authoring JSON exceeds its structural value/property budget.");
            }
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            ValidateRepresentation(document.RootElement, allowNull);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            document?.Dispose();
            throw new InvalidDataException("RPC authoring JSON has invalid UTF-8, scalar text, or JSON representation.", exception);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    internal static void RequireObject(JsonElement value, params string[] exactOrderedMembers)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.EnumerateObject().Select(static property => property.Name)
                .SequenceEqual(exactOrderedMembers, StringComparer.Ordinal))
            throw new InvalidDataException("RPC authoring object members must match the exact closed schema order.");
    }

    internal static byte[] WriteCompact(JsonElement value, int maximumBytes)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ValidateRepresentation(value);
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        Write(value, 0);
        return output.ToArray();

        void Write(JsonElement item, int depth)
        {
            if (depth > 16 || depth == 16 && item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new InvalidDataException("RPC authoring JSON exceeds the depth limit.");
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    Token("{");
                    var firstProperty = true;
                    foreach (var property in item.EnumerateObject())
                    {
                        if (!firstProperty) Token(",");
                        firstProperty = false;
                        String(property.Name);
                        Token(":");
                        Write(property.Value, depth + 1);
                    }
                    Token("}");
                    break;
                case JsonValueKind.Array:
                    Token("[");
                    var firstItem = true;
                    foreach (var child in item.EnumerateArray())
                    {
                        if (!firstItem) Token(",");
                        firstItem = false;
                        Write(child, depth + 1);
                    }
                    Token("]");
                    break;
                case JsonValueKind.String:
                    String(item.GetString()!);
                    break;
                case JsonValueKind.Number:
                    Token(item.GetInt64().ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValueKind.True:
                    Token("true");
                    break;
                case JsonValueKind.False:
                    Token("false");
                    break;
                default:
                    throw new InvalidDataException("RPC authoring JSON does not permit null or undefined values.");
            }
        }

        void Token(string token)
        {
            EnsureCapacity(token.Length);
            foreach (var character in token) output.WriteByte((byte)character);
        }

        void String(string text)
        {
            long length = StrictUtf8.GetByteCount(text) + 2L;
            foreach (var character in text)
            {
                if (character is '"' or '\\' or '\b' or '\t' or '\n' or '\f' or '\r') length++;
                else if (character < ' ') length += 5;
            }
            EnsureCapacity(length);
            var escaped = new StringBuilder();
            AppendString(escaped, text);
            output.Write(StrictUtf8.GetBytes(escaped.ToString()));
        }

        void EnsureCapacity(long bytes)
        {
            if (bytes > maximumBytes - output.Length)
                throw new InvalidDataException("RPC authoring JSON exceeds the canonical byte limit.");
        }
    }

    internal static void AppendString(StringBuilder output, string value)
    {
        ValidateScalars(value);
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\t': output.Append("\\t"); break;
                case '\n': output.Append("\\n"); break;
                case '\f': output.Append("\\f"); break;
                case '\r': output.Append("\\r"); break;
                case < ' ':
                    output.Append("\\u00").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default: output.Append(character); break;
            }
        }
        output.Append('"');
    }

    private static void ValidateRepresentation(JsonElement value, bool allowNull = false)
    {
        var valuesAndProperties = 0;
        ValidateRepresentation(value, 0, ref valuesAndProperties, allowNull);
    }

    private static void ValidateRepresentation(JsonElement value, int depth, ref int valuesAndProperties, bool allowNull)
    {
        if (depth > 16 || depth == 16 && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            throw new InvalidDataException("RPC authoring JSON exceeds the depth limit.");
        if (++valuesAndProperties > MaximumValuesAndProperties)
            throw new InvalidDataException("RPC authoring JSON exceeds its structural value/property budget.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (++valuesAndProperties > MaximumValuesAndProperties)
                        throw new InvalidDataException("RPC authoring JSON exceeds its structural value/property budget.");
                    ValidateScalars(property.Name);
                    var folded = string.Create(property.Name.Length, property.Name, static (span, name) =>
                    {
                        for (var index = 0; index < span.Length; index++)
                            span[index] = name[index] is >= 'A' and <= 'Z' ? (char)(name[index] + ('a' - 'A')) : name[index];
                    });
                    if (!names.Add(folded)) throw new InvalidDataException("RPC authoring JSON contains duplicate or ASCII-case-colliding members.");
                    ValidateRepresentation(property.Value, depth + 1, ref valuesAndProperties, allowNull);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateRepresentation(item, depth + 1, ref valuesAndProperties, allowNull);
                break;
            case JsonValueKind.String:
                ValidateScalars(value.GetString()!);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out var integer) || integer < -MaximumSafeInteger || integer > MaximumSafeInteger
                    || value.GetRawText() != integer.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("RPC authoring JSON numbers must be canonical safe integers.");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                break;
            case JsonValueKind.Null when allowNull:
                break;
            default:
                throw new InvalidDataException("RPC authoring JSON does not permit null or undefined values.");
        }
    }

    private static void ValidateScalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1]))
                throw new InvalidDataException("RPC authoring JSON strings must contain Unicode scalar values.");
            index++;
        }
    }
}
