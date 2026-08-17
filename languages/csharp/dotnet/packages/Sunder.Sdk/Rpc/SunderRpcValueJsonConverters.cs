using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Sdk.Rpc;

internal sealed class SunderRpcDateJsonConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return value is not null
               && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
               && result.Year is >= 1 and <= 9999
            ? result
            : throw new JsonException("An RPC date must use canonical YYYY-MM-DD text.");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

internal sealed class SunderRpcOffsetDateTimeJsonConverter : JsonConverter<SunderRpcOffsetDateTime>
{
    public override SunderRpcOffsetDateTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return SunderRpcOffsetDateTime.TryParse(value, out var result)
            ? result
            : throw new JsonException("An RPC date-time must use canonical offset date-time text.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        SunderRpcOffsetDateTime value,
        JsonSerializerOptions options)
    {
        var text = value.Value;
        if (text is null || !SunderRpcValueText.IsOffsetDateTime(text))
        {
            throw new JsonException("The RPC offset date-time is uninitialized or invalid.");
        }
        writer.WriteStringValue(text);
    }
}

internal sealed class SunderRpcDurationJsonConverter : JsonConverter<SunderRpcDuration>
{
    public override SunderRpcDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return SunderRpcDuration.TryParse(value, out var result)
            ? result
            : throw new JsonException("An RPC duration must use canonical non-negative ISO 8601 text.");
    }

    public override void Write(Utf8JsonWriter writer, SunderRpcDuration value, JsonSerializerOptions options)
    {
        var text = value.Value;
        if (text is null || !SunderRpcValueText.IsDuration(text))
        {
            throw new JsonException("The RPC duration is uninitialized or invalid.");
        }
        writer.WriteStringValue(text);
    }
}

internal sealed class SunderRpcUriJsonConverter : JsonConverter<SunderRpcUri>
{
    public override SunderRpcUri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return SunderRpcUri.TryParse(value, out var result)
            ? result
            : throw new JsonException("An RPC URI must be an exact valid absolute URI.");
    }

    public override void Write(Utf8JsonWriter writer, SunderRpcUri value, JsonSerializerOptions options)
    {
        var text = value.Value;
        if (text is null || !SunderRpcValueText.IsAbsoluteUri(text))
        {
            throw new JsonException("The RPC URI is uninitialized or invalid.");
        }
        writer.WriteStringValue(text);
    }
}

internal sealed class SunderRpcDecimalJsonConverter : JsonConverter<SunderRpcDecimal>
{
    public override SunderRpcDecimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return SunderRpcDecimal.TryParse(value, out var result)
            ? result
            : throw new JsonException("An RPC decimal must use canonical exact decimal text.");
    }

    public override void Write(Utf8JsonWriter writer, SunderRpcDecimal value, JsonSerializerOptions options)
    {
        var text = value.Value;
        if (text is null || !SunderRpcValueText.IsCanonicalDecimal(text))
        {
            throw new JsonException("The RPC decimal is uninitialized or invalid.");
        }
        writer.WriteStringValue(text);
    }
}

internal sealed class SunderRpcBigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return value is not null
               && SunderRpcValueText.IsCanonicalBigInteger(value)
               && BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new JsonException("An RPC big-integer must use canonical decimal integer text.");
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class SunderRpcBase64JsonConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (value is null || value.Length % 4 != 0)
        {
            throw new JsonException("RPC bytes must use canonical padded RFC 4648 base64.");
        }
        try
        {
            var result = Convert.FromBase64String(value);
            return string.Equals(Convert.ToBase64String(result), value, StringComparison.Ordinal)
                ? result
                : throw new JsonException("RPC bytes must use canonical padded RFC 4648 base64.");
        }
        catch (FormatException exception)
        {
            throw new JsonException("RPC bytes must use canonical padded RFC 4648 base64.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        => writer.WriteStringValue(Convert.ToBase64String(value));
}

internal sealed class SunderRpcSafeIntegerJsonConverter : JsonConverter<long>
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;

    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("An RPC integer must be a JSON number.");
        }
        using var document = JsonDocument.ParseValue(ref reader);
        return TryParseSafeInteger(document.RootElement.GetRawText(), out var result)
            ? result
            : throw new JsonException("An RPC integer must be a mathematical IEEE-754 safe integer.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        if (value is < -MaximumSafeInteger or > MaximumSafeInteger)
        {
            throw new JsonException("An RPC integer must be in the IEEE-754 safe-integer range.");
        }
        writer.WriteNumberValue(value);
    }

    private static bool TryParseSafeInteger(string text, out long result)
    {
        result = default;
        var span = text.AsSpan();
        var offset = span[0] == '-' ? 1 : 0;
        var exponentOffset = span[offset..].IndexOfAny('e', 'E');
        if (exponentOffset >= 0) exponentOffset += offset;
        var mantissaEnd = exponentOffset >= 0 ? exponentOffset : span.Length;
        var decimalOffset = span[offset..mantissaEnd].IndexOf('.');
        if (decimalOffset >= 0) decimalOffset += offset;
        var fractionDigits = decimalOffset >= 0 ? mantissaEnd - decimalOffset - 1 : 0;
        var digits = new char[mantissaEnd - offset - (decimalOffset >= 0 ? 1 : 0)];
        var digitCount = 0;
        for (var index = offset; index < mantissaEnd; index++)
        {
            if (span[index] != '.') digits[digitCount++] = span[index];
        }
        var firstNonZero = 0;
        while (firstNonZero < digitCount && digits[firstNonZero] == '0') firstNonZero++;
        if (firstNonZero == digitCount) return true;

        long exponent = 0;
        if (exponentOffset >= 0)
        {
            var exponentIndex = exponentOffset + 1;
            var exponentNegative = span[exponentIndex] == '-';
            if (span[exponentIndex] is '+' or '-') exponentIndex++;
            var exponentLimit = (long)span.Length + 32;
            for (; exponentIndex < span.Length; exponentIndex++)
            {
                exponent = Math.Min(exponentLimit, exponent * 10 + span[exponentIndex] - '0');
            }
            if (exponentNegative) exponent = -exponent;
        }
        var scale = fractionDigits - exponent;
        var coefficientEnd = digitCount;
        long appendedZeroes = 0;
        if (scale > 0)
        {
            if (scale > digitCount) return false;
            coefficientEnd -= (int)scale;
            for (var index = coefficientEnd; index < digitCount; index++)
            {
                if (digits[index] != '0') return false;
            }
        }
        else
        {
            appendedZeroes = -scale;
        }
        while (firstNonZero < coefficientEnd && digits[firstNonZero] == '0') firstNonZero++;
        if (firstNonZero == coefficientEnd) return true;
        if ((long)coefficientEnd - firstNonZero + appendedZeroes > 16) return false;

        long magnitude = 0;
        for (var index = firstNonZero; index < coefficientEnd; index++)
        {
            magnitude = magnitude * 10 + digits[index] - '0';
            if (magnitude > MaximumSafeInteger) return false;
        }
        for (long index = 0; index < appendedZeroes; index++)
        {
            magnitude *= 10;
            if (magnitude > MaximumSafeInteger) return false;
        }
        result = offset == 1 ? -magnitude : magnitude;
        return true;
    }
}

internal sealed class SunderRpcFloat32JsonConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("An RPC float32 must be a JSON number.");
        }
        using var document = JsonDocument.ParseValue(ref reader);
        return float.TryParse(
                   document.RootElement.GetRawText(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var result)
               && float.IsFinite(result)
            ? result
            : throw new JsonException("An RPC float32 must decode to a finite binary32 value.");
    }

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (!float.IsFinite(value)) throw new JsonException("An RPC float32 must be finite.");
        writer.WriteNumberValue(value == 0 ? 0 : value);
    }
}

internal sealed class SunderRpcFloat64JsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("An RPC float64 must be a JSON number.");
        }
        using var document = JsonDocument.ParseValue(ref reader);
        return double.TryParse(
                   document.RootElement.GetRawText(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var result)
               && double.IsFinite(result)
            ? result
            : throw new JsonException("An RPC float64 must decode to a finite binary64 value.");
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (!double.IsFinite(value)) throw new JsonException("An RPC float64 must be finite.");
        writer.WriteNumberValue(value == 0 ? 0 : value);
    }
}
