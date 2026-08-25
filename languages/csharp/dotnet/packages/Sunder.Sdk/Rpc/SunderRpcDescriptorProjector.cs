using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal readonly record struct SunderRpcDescriptorProjection(
    byte[] CanonicalUtf8,
    string Sha256,
    byte[]? CanonicalAbiProjectionUtf8,
    string? AbiSha256);

internal static class SunderRpcDescriptorProjector
{
    private static readonly byte[] AbiV2DomainSeparator = "sunder.rpc.abi.v2\0"u8.ToArray();

    public static SunderRpcDescriptorProjection Project(
        JsonElement root,
        IReadOnlyDictionary<string, JsonElement> definitions,
        bool includeAbiProjection)
    {
        var canonicalUtf8 = Canonicalize(root);
        var sha256 = Convert.ToHexString(SHA256.HashData(canonicalUtf8)).ToLowerInvariant();
        var canonicalAbiProjectionUtf8 = includeAbiProjection
            ? CanonicalizeAbiProjection(root, definitions)
            : null;
        var abiSha256 = canonicalAbiProjectionUtf8 is null
            ? null
            : HashAbiV2(canonicalAbiProjectionUtf8);
        return new SunderRpcDescriptorProjection(
            canonicalUtf8,
            sha256,
            canonicalAbiProjectionUtf8,
            abiSha256);
    }

    private static byte[] Canonicalize(JsonElement root)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteCanonical(output, root);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(ArrayBufferWriter<byte> output, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteByte(output, (byte)'{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) WriteByte(output, (byte)',');
                    firstProperty = false;
                    WriteJsonString(output, property.Name);
                    WriteByte(output, (byte)':');
                    WriteCanonical(output, property.Value);
                }
                WriteByte(output, (byte)'}');
                break;
            case JsonValueKind.Array:
                WriteByte(output, (byte)'[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) WriteByte(output, (byte)',');
                    firstItem = false;
                    WriteCanonical(output, item);
                }
                WriteByte(output, (byte)']');
                break;
            case JsonValueKind.String:
                WriteJsonString(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                WriteAscii(output, FormatCanonicalNumber(element));
                break;
            case JsonValueKind.True:
                WriteBytes(output, "true"u8);
                break;
            case JsonValueKind.False:
                WriteBytes(output, "false"u8);
                break;
            case JsonValueKind.Null:
                WriteBytes(output, "null"u8);
                break;
            default:
                throw SunderRpcDescriptorParser.Error("An RPC descriptor contains an unsupported JSON token.");
        }
    }

    private static byte[] CanonicalizeAbiProjection(
        JsonElement root,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteAbiElement(output, root, AbiContext.Root, definitions);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteAbiElement(
        ArrayBufferWriter<byte> output,
        JsonElement element,
        AbiContext context,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            WriteAbiObject(output, element, context, definitions);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            WriteAbiArray(output, element.EnumerateArray(), AbiContext.Generic, definitions);
            return;
        }
        WriteCanonical(output, element);
    }

    private static void WriteAbiObject(
        ArrayBufferWriter<byte> output,
        JsonElement element,
        AbiContext context,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var isReference = context == AbiContext.Schema && element.TryGetProperty("$ref", out _);
        var properties = element.EnumerateObject()
            .Where(property => context switch
            {
                AbiContext.Root => property.Name is not "$schema" and not "version",
                AbiContext.ErrorDeclaration => property.Name != "description",
                AbiContext.Schema when !isReference => property.Name is not "title" and not "description",
                _ => true,
            })
            .OrderBy(static property => property.Name, StringComparer.Ordinal);

        WriteByte(output, (byte)'{');
        var first = true;
        foreach (var property in properties)
        {
            if (!first) WriteByte(output, (byte)',');
            first = false;
            WriteJsonString(output, property.Name);
            WriteByte(output, (byte)':');
            WriteAbiPropertyValue(output, property, context, definitions);
        }
        WriteByte(output, (byte)'}');
    }

    private static void WriteAbiPropertyValue(
        ArrayBufferWriter<byte> output,
        JsonProperty property,
        AbiContext parentContext,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var childContext = parentContext switch
        {
            AbiContext.Root when property.Name == "errors" => AbiContext.ErrorMap,
            AbiContext.Root when property.Name == "services" => AbiContext.Services,
            AbiContext.Root when property.Name == "$defs" => AbiContext.Definitions,
            AbiContext.ErrorMap => AbiContext.ErrorDeclaration,
            AbiContext.ErrorDeclaration when property.Name == "detailsSchema" => AbiContext.Schema,
            AbiContext.Services => AbiContext.Service,
            AbiContext.Service when property.Name == "methods" => AbiContext.Methods,
            AbiContext.Methods => AbiContext.Method,
            AbiContext.Definitions => AbiContext.Schema,
            AbiContext.RecordProperties => AbiContext.Schema,
            AbiContext.Method when property.Name is "requestSchema" or "responseSchema" or "eventSchema" => AbiContext.Schema,
            AbiContext.Schema when property.Name == "properties" => AbiContext.RecordProperties,
            AbiContext.Schema when property.Name is "items" or "additionalProperties" or "propertyNames" => AbiContext.Schema,
            _ => AbiContext.Generic,
        };

        if (parentContext == AbiContext.Method && property.Name == "errors")
        {
            WriteSortedStringArray(output, property.Value, definitions);
        }
        else if (parentContext == AbiContext.Schema && property.Name == "required")
        {
            WriteSortedStringArray(output, property.Value, definitions);
        }
        else if (parentContext == AbiContext.Schema && property.Name == "enum")
        {
            WriteSortedEnum(output, property.Value, definitions);
        }
        else if (parentContext == AbiContext.Schema && property.Name == "oneOf")
        {
            WriteNormalizedOneOf(output, property.Value, definitions);
        }
        else
        {
            WriteAbiElement(output, property.Value, childContext, definitions);
        }
    }

    private static void WriteSortedStringArray(
        ArrayBufferWriter<byte> output,
        JsonElement array,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var items = array.EnumerateArray()
            .OrderBy(static item => item.GetString(), StringComparer.Ordinal)
            .ToArray();
        WriteAbiArray(output, items, AbiContext.Generic, definitions);
    }

    private static void WriteSortedEnum(
        ArrayBufferWriter<byte> output,
        JsonElement array,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var items = array.EnumerateArray()
            .Select(static item => (Value: item, Canonical: Canonicalize(item)))
            .OrderBy(static item => item.Canonical, ByteArrayComparer.Instance)
            .Select(static item => item.Value)
            .ToArray();
        WriteAbiArray(output, items, AbiContext.Generic, definitions);
    }

    private static void WriteNormalizedOneOf(
        ArrayBufferWriter<byte> output,
        JsonElement oneOf,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var variants = oneOf.EnumerateArray().ToArray();
        if (variants.Length == 2
            && variants.Count(variant => IsNullSchema(ResolveSchema(variant, definitions))) == 1)
        {
            variants = variants
                .OrderBy(variant => IsNullSchema(ResolveSchema(variant, definitions)) ? 0 : 1)
                .ToArray();
        }
        else
        {
            var discriminator = FindTaggedUnionDiscriminator(variants, definitions);
            variants = variants
                .OrderBy(
                    variant => GetDiscriminatorValue(ResolveSchema(variant, definitions), discriminator),
                    StringComparer.Ordinal)
                .ToArray();
        }
        WriteAbiArray(output, variants, AbiContext.Schema, definitions);
    }

    private static void WriteAbiArray(
        ArrayBufferWriter<byte> output,
        IEnumerable<JsonElement> elements,
        AbiContext itemContext,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        WriteByte(output, (byte)'[');
        var first = true;
        foreach (var element in elements)
        {
            if (!first) WriteByte(output, (byte)',');
            first = false;
            WriteAbiElement(output, element, itemContext, definitions);
        }
        WriteByte(output, (byte)']');
    }

    private static string FindTaggedUnionDiscriminator(
        IReadOnlyList<JsonElement> variants,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var resolved = variants.Select(variant => ResolveSchema(variant, definitions)).ToArray();
        var firstRequired = resolved[0].GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in resolved[0].GetProperty("properties").EnumerateObject())
        {
            if (!firstRequired.Contains(property.Name)) continue;
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (resolved.All(variant => TryGetDiscriminatorValue(variant, property.Name, out var value)
                                        && values.Add(value)))
            {
                return property.Name;
            }
        }
        throw SunderRpcDescriptorParser.Error("A validated tagged union has no discriminator.");
    }

    private static bool TryGetDiscriminatorValue(JsonElement schema, string propertyName, out string value)
    {
        var required = schema.GetProperty("required").EnumerateArray()
            .Any(item => string.Equals(item.GetString(), propertyName, StringComparison.Ordinal));
        if (required
            && schema.GetProperty("properties").TryGetProperty(propertyName, out var property)
            && property.TryGetProperty("type", out var type)
            && type.GetString() == "string"
            && property.TryGetProperty("const", out var constant)
            && constant.ValueKind == JsonValueKind.String)
        {
            value = constant.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string GetDiscriminatorValue(JsonElement schema, string propertyName)
    {
        _ = TryGetDiscriminatorValue(schema, propertyName, out var value);
        return value;
    }

    private static JsonElement ResolveSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            if (!visited.Add(name))
            {
                throw SunderRpcDescriptorParser.Error(
                    "A schema reference cycle reached ABI normalization.",
                    "schema-reference-cycle");
            }
            schema = definitions[name];
        }
        return schema;
    }

    private static bool IsNullSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type) && type.GetString() == "null";

    private static string HashAbiV2(byte[] canonicalProjection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(AbiV2DomainSeparator);
        hash.AppendData(canonicalProjection);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteJsonString(ArrayBufferWriter<byte> output, string value)
    {
        WriteByte(output, (byte)'"');
        Span<byte> encoded = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    WriteBytes(output, "\\\""u8);
                    break;
                case '\\':
                    WriteBytes(output, "\\\\"u8);
                    break;
                case '\b':
                    WriteBytes(output, "\\b"u8);
                    break;
                case '\t':
                    WriteBytes(output, "\\t"u8);
                    break;
                case '\n':
                    WriteBytes(output, "\\n"u8);
                    break;
                case '\f':
                    WriteBytes(output, "\\f"u8);
                    break;
                case '\r':
                    WriteBytes(output, "\\r"u8);
                    break;
                case < 0x20:
                    WriteBytes(output, "\\u00"u8);
                    WriteByte(output, ToLowerHex(rune.Value >> 4));
                    WriteByte(output, ToLowerHex(rune.Value));
                    break;
                default:
                    var length = rune.EncodeToUtf8(encoded);
                    WriteBytes(output, encoded[..length]);
                    break;
            }
        }
        WriteByte(output, (byte)'"');
    }

    private static string FormatCanonicalNumber(JsonElement element)
    {
        var raw = element.GetRawText();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw SunderRpcDescriptorParser.Error(
                $"Descriptor number '{raw}' is not finite.",
                "descriptor-number");
        }
        if (value == 0) return "0";

        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = roundTrip[0] == '-';
        var offset = negative ? 1 : 0;
        var exponentIndex = roundTrip.AsSpan(offset).IndexOfAny('E', 'e');
        if (exponentIndex >= 0) exponentIndex += offset;
        var mantissaEnd = exponentIndex >= 0 ? exponentIndex : roundTrip.Length;
        var exponent = exponentIndex >= 0
            ? int.Parse(roundTrip.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : 0;
        var decimalIndex = roundTrip.AsSpan(offset, mantissaEnd - offset).IndexOf('.');
        if (decimalIndex >= 0) decimalIndex += offset;
        var integerDigits = decimalIndex >= 0 ? decimalIndex - offset : mantissaEnd - offset;

        var digitsBuffer = new char[mantissaEnd - offset];
        var digitCount = 0;
        for (var index = offset; index < mantissaEnd; index++)
        {
            if (roundTrip[index] != '.') digitsBuffer[digitCount++] = roundTrip[index];
        }
        var firstNonZero = 0;
        while (firstNonZero < digitCount && digitsBuffer[firstNonZero] == '0') firstNonZero++;
        var lastNonZero = digitCount - 1;
        while (lastNonZero > firstNonZero && digitsBuffer[lastNonZero] == '0') lastNonZero--;
        var digits = new string(digitsBuffer[firstNonZero..(lastNonZero + 1)]);
        var decimalPosition = integerDigits + exponent - firstNonZero;

        var output = new StringBuilder(negative ? digits.Length + 8 : digits.Length + 7);
        if (negative) output.Append('-');
        if (digits.Length <= decimalPosition && decimalPosition <= 21)
        {
            output.Append(digits);
            output.Append('0', decimalPosition - digits.Length);
        }
        else if (decimalPosition is > 0 and <= 21)
        {
            output.Append(digits.AsSpan(0, decimalPosition));
            output.Append('.');
            output.Append(digits.AsSpan(decimalPosition));
        }
        else if (decimalPosition is > -6 and <= 0)
        {
            output.Append("0.");
            output.Append('0', -decimalPosition);
            output.Append(digits);
        }
        else
        {
            output.Append(digits[0]);
            if (digits.Length > 1)
            {
                output.Append('.');
                output.Append(digits.AsSpan(1));
            }
            var scientificExponent = decimalPosition - 1;
            output.Append('e');
            if (scientificExponent >= 0) output.Append('+');
            output.Append(scientificExponent.ToString(CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    private static byte ToLowerHex(int value)
        => (byte)((value & 0xf) is < 10 ? '0' + (value & 0xf) : 'a' + (value & 0xf) - 10);

    private static void WriteAscii(ArrayBufferWriter<byte> output, string value)
    {
        var target = output.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++) target[index] = (byte)value[index];
        output.Advance(value.Length);
    }

    private static void WriteByte(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    private enum AbiContext
    {
        Generic,
        Root,
        ErrorMap,
        ErrorDeclaration,
        Services,
        Service,
        Methods,
        Method,
        Definitions,
        Schema,
        RecordProperties,
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var length = Math.Min(left.Length, right.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
