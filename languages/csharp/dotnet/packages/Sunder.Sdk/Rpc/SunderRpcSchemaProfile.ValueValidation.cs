using System.Globalization;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcSchemaProfile
{
    private static bool IsFormattedString(string format, string value)
        => format switch
        {
            "uuid" => IsUuid(value),
            "date" => IsDate(value),
            "date-time" => IsOffsetDateTime(value),
            "duration" => IsDuration(value),
            "uri" => IsAbsoluteUri(value),
            _ => false,
        };

    private static bool IsDate(string value)
        => value.Length == 10
           && value[4] == '-'
           && value[7] == '-'
           && TryReadDigits(value, 0, 4, out var year)
           && TryReadDigits(value, 5, 2, out var month)
           && TryReadDigits(value, 8, 2, out var day)
           && year is >= 1 and <= 9999
           && month is >= 1 and <= 12
           && day >= 1
           && day <= DateTime.DaysInMonth(year, month);

    private static bool IsOffsetDateTime(string value)
    {
        if (value.Length < 20
            || value[10] != 'T'
            || value[13] != ':'
            || value[16] != ':'
            || !IsDate(value[..10])
            || !TryReadDigits(value, 11, 2, out var hour)
            || !TryReadDigits(value, 14, 2, out var minute)
            || !TryReadDigits(value, 17, 2, out var second)
            || hour > 23
            || minute > 59
            || second > 59)
        {
            return false;
        }

        var zoneIndex = 19;
        if (value[zoneIndex] == '.')
        {
            var fractionStart = ++zoneIndex;
            while (zoneIndex < value.Length && value[zoneIndex] is >= '0' and <= '9') zoneIndex++;
            if (zoneIndex - fractionStart is < 1 or > 9) return false;
        }
        TimeSpan offset;
        if (zoneIndex == value.Length - 1 && value[zoneIndex] == 'Z')
        {
            offset = TimeSpan.Zero;
        }
        else
        {
            if (value.Length - zoneIndex != 6
                || value[zoneIndex] is not ('+' or '-')
                || value[zoneIndex + 3] != ':'
                || !TryReadDigits(value, zoneIndex + 1, 2, out var offsetHours)
                || !TryReadDigits(value, zoneIndex + 4, 2, out var offsetMinutes)
                || offsetHours > 14
                || offsetMinutes > 59
                || offsetHours == 14 && offsetMinutes != 0)
            {
                return false;
            }
            offset = new TimeSpan(offsetHours, offsetMinutes, 0);
            if (value[zoneIndex] == '-') offset = -offset;
        }
        try
        {
            _ = new DateTimeOffset(
                int.Parse(value.AsSpan(0, 4), CultureInfo.InvariantCulture),
                int.Parse(value.AsSpan(5, 2), CultureInfo.InvariantCulture),
                int.Parse(value.AsSpan(8, 2), CultureInfo.InvariantCulture),
                hour,
                minute,
                second,
                offset);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsDuration(string value)
    {
        if (value.Length < 3 || value[0] != 'P') return false;
        var index = 1;
        var components = 0;
        foreach (var designator in new[] { 'Y', 'M', 'D' })
        {
            var start = index;
            if (ReadDurationNumber(value, ref index, allowFraction: false)
                && index < value.Length
                && value[index] == designator)
            {
                index++;
                components++;
            }
            else
            {
                index = start;
            }
        }
        if (index < value.Length && value[index] == 'T')
        {
            index++;
            var timeComponents = 0;
            foreach (var designator in new[] { 'H', 'M', 'S' })
            {
                var start = index;
                if (ReadDurationNumber(value, ref index, allowFraction: designator == 'S')
                    && index < value.Length
                    && value[index] == designator)
                {
                    index++;
                    timeComponents++;
                    components++;
                }
                else
                {
                    index = start;
                }
            }
            if (timeComponents == 0) return false;
        }
        return components > 0 && index == value.Length;
    }

    private static bool ReadDurationNumber(string value, ref int index, bool allowFraction)
    {
        var start = index;
        while (index < value.Length && value[index] is >= '0' and <= '9') index++;
        if (index == start) return false;
        if (value[start] == '0' && index - start > 1) return false;
        if (allowFraction && index < value.Length && value[index] == '.')
        {
            var fractionStart = ++index;
            while (index < value.Length && value[index] is >= '0' and <= '9') index++;
            if (index == fractionStart || value[index - 1] == '0') return false;
        }
        return true;
    }

    private static bool IsAbsoluteUri(string value)
    {
        if (value.Length == 0
            || value.Any(static character => character >= 0x7f || character <= 0x20 || character == '\\'))
        {
            return false;
        }
        var colon = value.IndexOf(':');
        if (colon <= 0
            || value[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')
            || value.AsSpan(1, colon - 1).IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-.".AsSpan()) >= 0)
        {
            return false;
        }
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2])) return false;
            index += 2;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out _);
    }

    private static bool IsHex(char value)
        => value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static bool ValidateContentReference(
        JsonElement value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return state.Reject(
                "content-reference-invalid",
                path,
                $"{path} must be a content-reference object.",
                out error);
        }
        if (!ValidateObjectPropertyNames(value.EnumerateObject(), path, state, out error))
        {
            return false;
        }
        if (value.GetPropertyCount() != 7
            || !value.TryGetProperty("id", out var idElement)
            || !value.TryGetProperty("length", out var lengthElement)
            || !value.TryGetProperty("sha256", out var sha256Element)
            || !value.TryGetProperty("mediaType", out var mediaTypeElement)
            || !value.TryGetProperty("fileName", out var fileNameElement)
            || !value.TryGetProperty("expiresAtUtc", out var expiresAtElement)
            || !value.TryGetProperty("repeatability", out var repeatabilityElement))
        {
            return state.Reject(
                "content-reference-invalid",
                path,
                $"{path} must contain exactly the seven content-reference members.",
                out error);
        }
        if (!TryGetScalarString(idElement, out var id)
            || id.Length == 0
            || id.EnumerateRunes().Count() > 256)
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".id",
                $"{path}.id must contain 1 through 256 characters.",
                out error);
        }
        if (!TryGetSafeInteger(lengthElement, out var length) || length < 0)
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".length",
                $"{path}.length must be a non-negative safe integer.",
                out error);
        }
        if (!TryGetScalarString(sha256Element, out var sha256)
            || sha256.Length != 64
            || sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".sha256",
                $"{path}.sha256 must be 64 lowercase hexadecimal characters.",
                out error);
        }
        if (!TryGetScalarString(mediaTypeElement, out var mediaType)
            || mediaType.Length is 0 or > 256
            || mediaType.Any(static character => character > 0x7f))
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".mediaType",
                $"{path}.mediaType must contain 1 through 256 ASCII characters.",
                out error);
        }
        if (!TryGetScalarString(fileNameElement, out var fileName)
            || fileName.EnumerateRunes().Count() > 1024)
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".fileName",
                $"{path}.fileName may contain at most 1024 characters.",
                out error);
        }
        if (!TryGetScalarString(expiresAtElement, out var expiresAt) || !IsUtcDateTime(expiresAt))
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".expiresAtUtc",
                $"{path}.expiresAtUtc must be a canonical UTC date-time.",
                out error);
        }
        if (!TryGetScalarString(repeatabilityElement, out var repeatability)
            || repeatability is not ("single-use" or "repeatable"))
        {
            return state.Reject(
                "content-reference-invalid",
                path + ".repeatability",
                $"{path}.repeatability must be 'single-use' or 'repeatable'.",
                out error);
        }
        error = null;
        return true;
    }

    private static bool ValidateObjectPropertyNames(
        IEnumerable<JsonProperty> properties,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitive = state.IsV2
            ? null
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (!HasOnlyUnicodeScalars(property.Name)
                || !exact.Add(property.Name)
                || caseInsensitive is not null && !caseInsensitive.Add(property.Name))
            {
                return state.Reject(
                    "property-name-invalid",
                    path,
                    $"{path} contains an invalid, duplicate, or case-colliding property '{property.Name}'.",
                    out error);
            }
        }
        error = null;
        return true;
    }

    private static bool TryGetScalarString(JsonElement value, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.String) return false;
        try
        {
            text = value.GetString()!;
            return HasOnlyUnicodeScalars(text);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasOnlyUnicodeScalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index]))
            {
                return false;
            }
        }
        return true;
    }

    private static string AppendPropertyPath(string path, string propertyName)
        => path.Length >= 4096 || propertyName.Length >= 4096 - path.Length
            ? path
            : path + "." + propertyName;

    private static string AppendItemPath(string path, int index)
        => path.Length >= 4096 ? path : path + "[" + index + "]";

    internal static bool IsUuid(string value)
    {
        if (value.Length != 36) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (index is 8 or 13 or 18 or 23)
            {
                if (value[index] != '-') return false;
            }
            else if (value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsUtcDateTime(string value)
    {
        if (value.Length < 20
            || value[4] != '-'
            || value[7] != '-'
            || value[10] != 'T'
            || value[13] != ':'
            || value[16] != ':'
            || !TryReadDigits(value, 0, 4, out var year)
            || !TryReadDigits(value, 5, 2, out var month)
            || !TryReadDigits(value, 8, 2, out var day)
            || !TryReadDigits(value, 11, 2, out var hour)
            || !TryReadDigits(value, 14, 2, out var minute)
            || !TryReadDigits(value, 17, 2, out var second)
            || year is < 1 or > 9999
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || hour > 23
            || minute > 59
            || second > 59)
        {
            return false;
        }
        if (value.Length == 20) return value[19] == 'Z';
        var fractionLength = value.Length - 21;
        return fractionLength is >= 1 and <= 9
               && value[19] == '.'
               && value[^1] == 'Z'
               && value[^2] != '0'
               && value.AsSpan(20, fractionLength).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool TryReadDigits(string value, int offset, int length, out int result)
    {
        result = 0;
        for (var index = offset; index < offset + length; index++)
        {
            if (value[index] is not (>= '0' and <= '9')) return false;
            result = result * 10 + value[index] - '0';
        }
        return true;
    }

    private static bool TryGetFiniteDouble(JsonElement value, out double result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Number
               && double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
               && double.IsFinite(result);
    }

    private static bool TryGetSafeInteger(JsonElement value, out long result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Number) return false;

        var text = value.GetRawText().AsSpan();
        var offset = 0;
        var negative = text[0] == '-';
        if (negative) offset++;
        var exponentOffset = text[offset..].IndexOfAny('e', 'E');
        if (exponentOffset >= 0) exponentOffset += offset;
        var mantissaEnd = exponentOffset >= 0 ? exponentOffset : text.Length;
        var decimalOffset = text[offset..mantissaEnd].IndexOf('.');
        if (decimalOffset >= 0) decimalOffset += offset;
        var fractionDigits = decimalOffset >= 0 ? mantissaEnd - decimalOffset - 1 : 0;

        var coefficient = new char[mantissaEnd - offset - (decimalOffset >= 0 ? 1 : 0)];
        var coefficientLength = 0;
        for (var index = offset; index < mantissaEnd; index++)
        {
            if (text[index] != '.') coefficient[coefficientLength++] = text[index];
        }

        long exponent = 0;
        if (exponentOffset >= 0)
        {
            var exponentIndex = exponentOffset + 1;
            var exponentNegative = text[exponentIndex] == '-';
            if (text[exponentIndex] is '+' or '-') exponentIndex++;
            var exponentLimit = (long)text.Length + 32;
            for (; exponentIndex < text.Length; exponentIndex++)
            {
                exponent = Math.Min(exponentLimit, exponent * 10 + text[exponentIndex] - '0');
            }
            if (exponentNegative) exponent = -exponent;
        }

        var firstNonZero = 0;
        while (firstNonZero < coefficientLength && coefficient[firstNonZero] == '0') firstNonZero++;
        if (firstNonZero == coefficientLength)
        {
            result = 0;
            return true;
        }

        var scale = fractionDigits - exponent;
        var coefficientEnd = coefficientLength;
        long appendedZeroes = 0;
        if (scale > 0)
        {
            if (scale > coefficientLength) return false;
            coefficientEnd -= (int)scale;
            for (var index = coefficientEnd; index < coefficientLength; index++)
            {
                if (coefficient[index] != '0') return false;
            }
        }
        else
        {
            appendedZeroes = -scale;
        }

        while (firstNonZero < coefficientEnd && coefficient[firstNonZero] == '0') firstNonZero++;
        if (firstNonZero == coefficientEnd)
        {
            result = 0;
            return true;
        }
        if ((long)coefficientEnd - firstNonZero + appendedZeroes > 16) return false;

        long magnitude = 0;
        for (var index = firstNonZero; index < coefficientEnd; index++)
        {
            magnitude = checked(magnitude * 10 + coefficient[index] - '0');
            if (magnitude > 9_007_199_254_740_991) return false;
        }
        for (long index = 0; index < appendedZeroes; index++)
        {
            magnitude = checked(magnitude * 10);
            if (magnitude > 9_007_199_254_740_991) return false;
        }
        result = negative ? -magnitude : magnitude;
        return true;
    }

    private static bool ValidateNumberInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        if (state.IsV2)
        {
            var type = schema.GetProperty("type").GetString();
            if (type == "integer")
            {
                if (!TryGetSafeInteger(value, out var integer))
                {
                    return state.Reject(
                        "integer-invalid",
                        path,
                        $"{path} must be a safe mathematical integer.",
                        out error);
                }
                if (schema.TryGetProperty("minimum", out var integerMinimum) && integer < ReadSafeInteger(integerMinimum, path)
                    || schema.TryGetProperty("exclusiveMinimum", out var integerExclusiveMinimum) && integer <= ReadSafeInteger(integerExclusiveMinimum, path)
                    || schema.TryGetProperty("maximum", out var integerMaximum) && integer > ReadSafeInteger(integerMaximum, path)
                    || schema.TryGetProperty("exclusiveMaximum", out var integerExclusiveMaximum) && integer >= ReadSafeInteger(integerExclusiveMaximum, path))
                {
                    return state.Reject(
                        "integer-invalid",
                        path,
                        $"{path} is outside its integer bounds.",
                        out error);
                }
            }
            else
            {
                if (!TryGetFormattedFloat(schema, value, out var formattedNumber))
                {
                    return state.Reject(
                        "number-invalid",
                        path,
                        $"{path} is not a finite {schema.GetProperty("format").GetString()} value.",
                        out error);
                }
                if (schema.TryGetProperty("minimum", out var floatMinimum) && formattedNumber < ReadFormattedFloat(schema, floatMinimum, path)
                    || schema.TryGetProperty("exclusiveMinimum", out var floatExclusiveMinimum) && formattedNumber <= ReadFormattedFloat(schema, floatExclusiveMinimum, path)
                    || schema.TryGetProperty("maximum", out var floatMaximum) && formattedNumber > ReadFormattedFloat(schema, floatMaximum, path)
                    || schema.TryGetProperty("exclusiveMaximum", out var floatExclusiveMaximum) && formattedNumber >= ReadFormattedFloat(schema, floatExclusiveMaximum, path))
                {
                    return state.Reject(
                        "number-invalid",
                        path,
                        $"{path} is outside its floating-point bounds.",
                        out error);
                }
            }
            error = null;
            return true;
        }

        if (!decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return state.Reject(
                "number-invalid",
                path,
                $"{path} is outside the supported RPC numeric range.",
                out error);
        }
        if (schema.GetProperty("type").GetString() == "integer" && decimal.Truncate(number) != number)
        {
            return state.Reject("integer-invalid", path, $"{path} must be an integer.", out error);
        }
        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal()
            || schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && number <= exclusiveMinimum.GetDecimal()
            || schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal()
            || schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && number >= exclusiveMaximum.GetDecimal())
        {
            return state.Reject(
                "number-invalid",
                path,
                $"{path} is outside its numeric bounds.",
                out error);
        }
        error = null;
        return true;
    }

    private static bool MatchesType(JsonElement value, string type, bool isV2)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number
                         && (isV2
                             ? TryGetSafeInteger(value, out _)
                             : decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var integer)
                               && decimal.Truncate(integer) == integer),
            "number" => value.ValueKind == JsonValueKind.Number
                        && (!isV2 || TryGetFiniteDouble(value, out _)),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static bool LiteralEquals(
        JsonElement left,
        JsonElement right,
        bool isV2,
        JsonElement schema)
    {
        if (isV2
            && left.ValueKind == JsonValueKind.Number
            && right.ValueKind == JsonValueKind.Number)
        {
            if (schema.GetProperty("type").GetString() == "integer")
            {
                return TryGetSafeInteger(left, out var leftInteger)
                       && TryGetSafeInteger(right, out var rightInteger)
                       && leftInteger == rightInteger;
            }
            return TryGetFormattedFloat(schema, left, out var leftFormattedNumber)
                   && TryGetFormattedFloat(schema, right, out var rightFormattedNumber)
                   && leftFormattedNumber == rightFormattedNumber;
        }
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number
            && decimal.TryParse(left.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber)
            && decimal.TryParse(right.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }
        return JsonElement.DeepEquals(left, right);
    }

    private sealed class InstanceValidationState(SunderRpcContractDescriptor descriptor)
    {
        public SunderRpcContractDescriptor Descriptor { get; } = descriptor;
        public bool IsV2 { get; } = descriptor.DescriptorVersion == SunderRpcProtocol.DescriptorVersionV2;

        public SunderRpcValidationFailure? Failure { get; private set; }

        public bool Reject(string code, string path, string message, out string? error)
        {
            Failure ??= new SunderRpcValidationFailure(code, path);
            error = message;
            return false;
        }
    }

    private readonly record struct InstanceWork(JsonElement Schema, JsonElement Value, string Path);

    private readonly record struct UnconstrainedValueWork(JsonElement Value, string Path);

    private readonly record struct ExactNumber(
        bool Negative,
        string IntegerPart,
        string FractionPart,
        int Precision,
        int Scale,
        int DigitCount)
    {
        public bool IsZero => IntegerPart == "0" && FractionPart.All(static digit => digit == '0');
    }
}
