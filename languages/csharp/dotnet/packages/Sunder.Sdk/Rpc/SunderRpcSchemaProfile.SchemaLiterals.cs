using System.Globalization;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcSchemaProfile
{
    private static void ValidateEnumAndConst(JsonElement schema, string path, string type, bool isV2)
    {
        var hasEnum = schema.TryGetProperty("enum", out var enumElement);
        var hasConst = schema.TryGetProperty("const", out var constElement);
        if (hasEnum && hasConst)
        {
            throw Error($"{path} cannot declare both enum and const.");
        }
        if (hasEnum)
        {
            if (enumElement.ValueKind != JsonValueKind.Array
                || enumElement.GetArrayLength() is 0 or > MaximumEnumValues)
            {
                throw Error($"{path}.enum must contain between 1 and {MaximumEnumValues} values.");
            }
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in enumElement.EnumerateArray())
            {
                ValidateLiteralType(item, type, path + ".enum", isV2, schema);
                if (isV2)
                {
                    ValidateDescriptorLiteralConstraints(schema, item, path + ".enum", type, isV2);
                }
                if (!values.Add(isV2 ? GetPrimitiveLiteralKey(item, schema) : item.GetRawText()))
                {
                    throw Error($"{path}.enum contains a duplicate value.");
                }
            }
        }
        if (hasConst)
        {
            ValidateLiteralType(constElement, type, path + ".const", isV2, schema);
            if (isV2)
            {
                ValidateDescriptorLiteralConstraints(schema, constElement, path + ".const", type, isV2);
            }
        }
    }

    private static string GetPrimitiveLiteralKey(JsonElement value, JsonElement schema)
        => value.ValueKind switch
        {
            JsonValueKind.String => "s:" + value.GetString(),
            JsonValueKind.Number when schema.GetProperty("type").GetString() == "integer"
                => "i:" + (TryGetSafeInteger(value, out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : value.GetRawText()),
            JsonValueKind.Number when schema.TryGetProperty("format", out var format)
                                      && format.GetString() == "float32"
                => "f32:" + CanonicalFloat32Bits(value).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => "f64:" + CanonicalFloat64Bits(value).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "b:true",
            JsonValueKind.False => "b:false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText(),
        };

    private static void ValidateLiteralType(
        JsonElement value,
        string type,
        string path,
        bool isV2,
        JsonElement schema)
    {
        var valid = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number
                         && (!isV2 ? value.TryGetInt64(out _) : TryGetSafeInteger(value, out _)),
            "number" => value.ValueKind == JsonValueKind.Number
                        && (!isV2 || TryGetFormattedFloat(schema, value, out _)),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
        if (!valid)
        {
            throw Error($"{path} contains a value incompatible with schema type '{type}'.");
        }
    }

    private static void ValidateDescriptorLiteralConstraints(
        JsonElement schema,
        JsonElement value,
        string path,
        string type,
        bool isV2)
    {
        if (type == "string")
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minimum)
                && text.EnumerateRunes().Count() < ReadQuantitativeBound(minimum)
                || schema.TryGetProperty("maxLength", out var maximum)
                && text.EnumerateRunes().Count() > ReadQuantitativeBound(maximum))
            {
                throw Error($"{path} is outside the schema's string length bounds.");
            }
            return;
        }
        if (type is not ("integer" or "number")) return;

        if (isV2 && type == "integer")
        {
            _ = TryGetSafeInteger(value, out var integer);
            if (schema.TryGetProperty("minimum", out var minimum) && integer < ReadSafeInteger(minimum, path)
                || schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && integer <= ReadSafeInteger(exclusiveMinimum, path)
                || schema.TryGetProperty("maximum", out var maximum) && integer > ReadSafeInteger(maximum, path)
                || schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && integer >= ReadSafeInteger(exclusiveMaximum, path))
            {
                throw Error($"{path} is outside the schema's integer bounds.");
            }
            return;
        }
        if (isV2)
        {
            _ = TryGetFormattedFloat(schema, value, out var number);
            if (schema.TryGetProperty("minimum", out var minimum) && number < ReadFormattedFloat(schema, minimum, path)
                || schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && number <= ReadFormattedFloat(schema, exclusiveMinimum, path)
                || schema.TryGetProperty("maximum", out var maximum) && number > ReadFormattedFloat(schema, maximum, path)
                || schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && number >= ReadFormattedFloat(schema, exclusiveMaximum, path))
            {
                throw Error($"{path} is outside the schema's floating-point bounds.");
            }
        }
    }

    private static void ValidateAnnotations(JsonElement schema, string path)
    {
        foreach (var annotation in AnnotationProperties)
        {
            if (!schema.TryGetProperty(annotation, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String
                || SunderRpcDescriptorParser.CountUnicodeScalars(value.GetString()!) > 2048)
            {
                throw Error($"{path}.{annotation} must be a string of at most 2048 characters.");
            }
        }
    }

    private static void ValidateOptionalPropertyBounds(
        JsonElement schema,
        string path,
        int propertyCount,
        bool isV2)
    {
        var maximumAllowed = isV2 ? MaximumSafeInteger : MaximumProperties;
        var minimum = OptionalBound(schema, "minProperties", path, 0, maximumAllowed, 0);
        var maximum = OptionalBound(
            schema,
            "maxProperties",
            path,
            0,
            maximumAllowed,
            isV2 ? long.MaxValue : propertyCount);
        if (minimum > maximum || !isV2 && maximum > propertyCount)
        {
            throw Error($"{path}'s object property bounds are inconsistent with its closed properties.");
        }
    }

    private static long OptionalBound(
        JsonElement schema,
        string name,
        string path,
        long minimum,
        long maximum,
        long defaultValue)
        => schema.TryGetProperty(name, out _)
            ? RequiredBound(schema, name, path, minimum, maximum)
            : defaultValue;

    private static long RequiredBound(JsonElement schema, string name, string path, long minimum, long maximum)
    {
        var value = SunderRpcDescriptorParser.Required(schema, name, path, JsonValueKind.Number);
        if (!TryGetSafeInteger(value, out var result) || result < minimum || result > maximum)
        {
            throw Error($"{path}.{name} must be an integer from {minimum} through {maximum}.");
        }
        return result;
    }

    private static long ReadSafeIntegerKeyword(JsonElement schema, string name, string path)
        => ReadSafeInteger(
            SunderRpcDescriptorParser.Required(schema, name, path, JsonValueKind.Number),
            path + "." + name);

    private static long ReadSafeInteger(JsonElement value, string path)
    {
        if (!TryGetSafeInteger(value, out var result))
        {
            throw Error(
                $"{path} must be a mathematical integer in the inclusive IEEE-754 safe-integer range.",
                "descriptor-number");
        }
        return result;
    }

    private static double ReadFloatingPointKeyword(
        JsonElement schema,
        string name,
        string path,
        string format)
    {
        var value = SunderRpcDescriptorParser.Required(schema, name, path, JsonValueKind.Number);
        if (!TryGetFormattedFloat(format, value, out var result))
        {
            throw Error($"{path}.{name} is not a finite {format} value.", "descriptor-number");
        }
        return result;
    }

    private static double ReadFormattedFloat(JsonElement schema, JsonElement value, string path)
    {
        if (!TryGetFormattedFloat(schema, value, out var result))
        {
            var format = schema.GetProperty("format").GetString();
            throw Error($"{path} is not a finite {format} value.", "descriptor-number");
        }
        return result;
    }

    private static bool TryGetFormattedFloat(JsonElement schema, JsonElement value, out double result)
        => TryGetFormattedFloat(schema.GetProperty("format").GetString()!, value, out result);

    private static bool TryGetFormattedFloat(string format, JsonElement value, out double result)
    {
        result = default;
        if (format == "float32")
        {
            if (value.ValueKind != JsonValueKind.Number
                || !float.TryParse(
                    value.GetRawText(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var binary32)
                || !float.IsFinite(binary32))
            {
                return false;
            }
            result = binary32;
            return true;
        }
        return format == "float64" && TryGetFiniteDouble(value, out result);
    }

    private static int CanonicalFloat32Bits(JsonElement value)
    {
        _ = float.TryParse(
            value.GetRawText(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result);
        return result == 0 ? 0 : BitConverter.SingleToInt32Bits(result);
    }

    private static long CanonicalFloat64Bits(JsonElement value)
    {
        _ = TryGetFiniteDouble(value, out var result);
        return result == 0 ? 0 : BitConverter.DoubleToInt64Bits(result);
    }

    private static void ValidateExactValueBounds(JsonElement schema, string path, string format)
    {
        var lowerCount = (schema.TryGetProperty("valueMinimum", out _) ? 1 : 0)
                         + (schema.TryGetProperty("valueExclusiveMinimum", out _) ? 1 : 0);
        var upperCount = (schema.TryGetProperty("valueMaximum", out _) ? 1 : 0)
                         + (schema.TryGetProperty("valueExclusiveMaximum", out _) ? 1 : 0);
        if (lowerCount > 1 || upperCount > 1)
        {
            throw Error($"{path} may declare at most one lower and one upper exact-value bound.");
        }

        ExactNumber? lower = null;
        ExactNumber? upper = null;
        if (lowerCount == 1)
        {
            var name = schema.TryGetProperty("valueMinimum", out _) ? "valueMinimum" : "valueExclusiveMinimum";
            lower = ReadExactKeyword(schema, name, path, format);
        }
        if (upperCount == 1)
        {
            var name = schema.TryGetProperty("valueMaximum", out _) ? "valueMaximum" : "valueExclusiveMaximum";
            upper = ReadExactKeyword(schema, name, path, format);
        }
        if (lower is not null && upper is not null && CompareExact(lower.Value, upper.Value) > 0)
        {
            throw Error($"{path}'s lower exact-value bound must not exceed its upper bound.");
        }
    }

    private static ExactNumber ReadExactKeyword(
        JsonElement schema,
        string name,
        string path,
        string format)
    {
        var value = SunderRpcDescriptorParser.Required(schema, name, path, JsonValueKind.String).GetString()!;
        if (!TryParseExactNumber(value, format == "decimal", out var exact)
            || !ExactNumberFitsSchema(schema, exact, format))
        {
            throw Error($"{path}.{name} is not a canonical {format} value within the declared limits.");
        }
        return exact;
    }

    private static bool ExactNumberFitsSchema(JsonElement schema, ExactNumber value, string format)
        => format == "big-integer"
            ? !schema.TryGetProperty("maxDigits", out var maximumDigits)
              || value.DigitCount <= ReadQuantitativeBound(maximumDigits)
            : (!schema.TryGetProperty("maxPrecision", out var maximumPrecision)
               || value.Precision <= ReadQuantitativeBound(maximumPrecision))
              && (!schema.TryGetProperty("maxScale", out var maximumScale)
                  || value.Scale <= ReadQuantitativeBound(maximumScale));

    private static long ReadQuantitativeBound(JsonElement value)
    {
        _ = TryGetSafeInteger(value, out var result);
        return result;
    }

    private static bool ExactNumberSatisfiesBounds(JsonElement schema, ExactNumber value, string format)
    {
        if (schema.TryGetProperty("valueMinimum", out var minimum)
            && CompareExact(value, ParseExactBound(minimum, format)) < 0
            || schema.TryGetProperty("valueExclusiveMinimum", out var exclusiveMinimum)
            && CompareExact(value, ParseExactBound(exclusiveMinimum, format)) <= 0
            || schema.TryGetProperty("valueMaximum", out var maximum)
            && CompareExact(value, ParseExactBound(maximum, format)) > 0
            || schema.TryGetProperty("valueExclusiveMaximum", out var exclusiveMaximum)
            && CompareExact(value, ParseExactBound(exclusiveMaximum, format)) >= 0)
        {
            return false;
        }
        return true;
    }

    private static ExactNumber ParseExactBound(JsonElement value, string format)
    {
        _ = TryParseExactNumber(value.GetString()!, format == "decimal", out var exact);
        return exact;
    }

    private static bool TryParseExactNumber(string value, bool allowFraction, out ExactNumber exact)
    {
        exact = default;
        if (value.Length == 0) return false;
        var negative = value[0] == '-';
        var offset = negative ? 1 : 0;
        if (offset == value.Length) return false;
        var decimalIndex = value.IndexOf('.', offset);
        if (!allowFraction && decimalIndex >= 0) return false;
        if (decimalIndex >= 0 && value.IndexOf('.', decimalIndex + 1) >= 0) return false;
        var integerEnd = decimalIndex >= 0 ? decimalIndex : value.Length;
        if (integerEnd == offset
            || value[offset] == '0' && integerEnd - offset > 1
            || value.AsSpan(offset, integerEnd - offset).IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return false;
        }
        var fraction = decimalIndex >= 0 ? value[(decimalIndex + 1)..] : string.Empty;
        if (decimalIndex >= 0
            && (fraction.Length == 0
                || fraction[^1] == '0'
                || fraction.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0))
        {
            return false;
        }
        var integer = value[offset..integerEnd];
        var firstSignificant = 0;
        var combinedLength = integer.Length + fraction.Length;
        while (firstSignificant < combinedLength)
        {
            var digit = firstSignificant < integer.Length
                ? integer[firstSignificant]
                : fraction[firstSignificant - integer.Length];
            if (digit != '0') break;
            firstSignificant++;
        }
        if (negative && firstSignificant == combinedLength) return false;
        var precision = firstSignificant == combinedLength ? 1 : combinedLength - firstSignificant;
        exact = new ExactNumber(
            negative,
            integer,
            fraction,
            precision,
            fraction.Length,
            allowFraction ? precision : integer.Length);
        return true;
    }

    private static int CompareExact(ExactNumber left, ExactNumber right)
    {
        var leftZero = left.IsZero;
        var rightZero = right.IsZero;
        if (leftZero && rightZero) return 0;
        if (left.Negative != right.Negative) return left.Negative ? -1 : 1;
        var magnitude = CompareExactMagnitude(left, right);
        return left.Negative ? -magnitude : magnitude;
    }

    private static int CompareExactMagnitude(ExactNumber left, ExactNumber right)
    {
        var leftIntegerLength = left.IntegerPart == "0" ? 0 : left.IntegerPart.Length;
        var rightIntegerLength = right.IntegerPart == "0" ? 0 : right.IntegerPart.Length;
        var comparison = leftIntegerLength.CompareTo(rightIntegerLength);
        if (comparison != 0) return comparison;
        if (leftIntegerLength > 0)
        {
            comparison = string.CompareOrdinal(left.IntegerPart, right.IntegerPart);
            if (comparison != 0) return comparison;
        }
        var maximumFractionLength = Math.Max(left.FractionPart.Length, right.FractionPart.Length);
        for (var index = 0; index < maximumFractionLength; index++)
        {
            var leftDigit = index < left.FractionPart.Length ? left.FractionPart[index] : '0';
            var rightDigit = index < right.FractionPart.Length ? right.FractionPart[index] : '0';
            comparison = leftDigit.CompareTo(rightDigit);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static decimal ReadNumberBound(JsonElement schema, string name, string path)
    {
        var value = SunderRpcDescriptorParser.Required(schema, name, path, JsonValueKind.Number);
        if (!value.TryGetDecimal(out var result))
        {
            throw Error($"{path}.{name} is outside the supported numeric range.");
        }
        return result;
    }

    private static void RequireObject(JsonElement schema, string path)
        => SunderRpcDescriptorParser.RequireKind(schema, JsonValueKind.Object, path, "a schema object");

    private static SunderRpcDescriptorException Error(
        string message,
        string errorCode = "schema-profile")
        => SunderRpcDescriptorParser.Error(message, errorCode);
}
