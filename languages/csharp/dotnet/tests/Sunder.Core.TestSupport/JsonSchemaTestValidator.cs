using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Core.TestSupport;

public static class JsonSchemaTestValidator
{
    public static bool IsValid(JsonElement schema, JsonElement instance)
        => IsValid(schema, instance, schema);

    public static bool IsValid(JsonElement schema, JsonElement instance, JsonElement rootSchema,
        IReadOnlyDictionary<string, JsonElement>? referencedSchemas = null)
    {
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/$defs/";
            var value = reference.GetString()!;
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (!rootSchema.GetProperty("$defs").TryGetProperty(value[prefix.Length..], out var target)
                    || !IsValid(target, instance, rootSchema, referencedSchemas)) return false;
            }
            else if (referencedSchemas is null || !referencedSchemas.TryGetValue(value, out var external)
                || !IsValid(external, instance, external, referencedSchemas)) return false;
        }
        if (schema.TryGetProperty("type", out var type) && !MatchesType(type.GetString()!, instance))
        {
            return false;
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, instance))
        {
            return false;
        }
        if (schema.TryGetProperty("enum", out var enumValues)
            && !enumValues.EnumerateArray().Any(value => JsonElement.DeepEquals(value, instance)))
        {
            return false;
        }
        if (schema.TryGetProperty("allOf", out var allOf)
            && allOf.EnumerateArray().Any(item => !IsValid(item, instance, rootSchema, referencedSchemas)))
        {
            return false;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf)
            && oneOf.EnumerateArray().Count(item => IsValid(item, instance, rootSchema, referencedSchemas)) != 1)
        {
            return false;
        }
        if (schema.TryGetProperty("anyOf", out var anyOf)
            && !anyOf.EnumerateArray().Any(item => IsValid(item, instance, rootSchema, referencedSchemas)))
        {
            return false;
        }
        if (schema.TryGetProperty("not", out var not) && IsValid(not, instance, rootSchema, referencedSchemas))
        {
            return false;
        }
        if (schema.TryGetProperty("if", out var condition))
        {
            var conditionMatches = IsValid(condition, instance, rootSchema, referencedSchemas);
            if (conditionMatches
                && schema.TryGetProperty("then", out var consequence)
                && !IsValid(consequence, instance, rootSchema, referencedSchemas))
            {
                return false;
            }
            if (!conditionMatches
                && schema.TryGetProperty("else", out var alternative)
                && !IsValid(alternative, instance, rootSchema, referencedSchemas))
            {
                return false;
            }
        }

        return instance.ValueKind switch
        {
            JsonValueKind.Object => IsObjectValid(schema, instance, rootSchema, referencedSchemas),
            JsonValueKind.Array => IsArrayValid(schema, instance, rootSchema, referencedSchemas),
            JsonValueKind.String => IsStringValid(schema, instance),
            JsonValueKind.Number => IsNumberValid(schema, instance),
            _ => true,
        };
    }

    private static bool IsObjectValid(JsonElement schema, JsonElement instance, JsonElement rootSchema,
        IReadOnlyDictionary<string, JsonElement>? referencedSchemas)
    {
        var properties = instance.EnumerateObject().ToArray();
        if (schema.TryGetProperty("minProperties", out var minimum) && properties.Length < minimum.GetInt32()
            || schema.TryGetProperty("maxProperties", out var maximum) && properties.Length > maximum.GetInt32())
        {
            return false;
        }
        if (schema.TryGetProperty("required", out var required)
            && required.EnumerateArray().Any(name => !instance.TryGetProperty(name.GetString()!, out _)))
        {
            return false;
        }
        if (schema.TryGetProperty("propertyNames", out var propertyNames)
            && properties.Any(property => !IsValid(
                propertyNames,
                JsonSerializer.SerializeToElement(property.Name),
                rootSchema, referencedSchemas)))
        {
            return false;
        }

        var hasDeclaredProperties = schema.TryGetProperty("properties", out var declaredProperties);
        foreach (var property in properties)
        {
            if (hasDeclaredProperties && declaredProperties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!IsValid(propertySchema, property.Value, rootSchema, referencedSchemas)) return false;
                continue;
            }
            if (!schema.TryGetProperty("additionalProperties", out var additional)) continue;
            if (additional.ValueKind == JsonValueKind.False
                || additional.ValueKind == JsonValueKind.Object
                && !IsValid(additional, property.Value, rootSchema, referencedSchemas))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsArrayValid(JsonElement schema, JsonElement instance, JsonElement rootSchema,
        IReadOnlyDictionary<string, JsonElement>? referencedSchemas)
    {
        var values = instance.EnumerateArray().ToArray();
        if (schema.TryGetProperty("minItems", out var minimum) && values.Length < minimum.GetInt32()
            || schema.TryGetProperty("maxItems", out var maximum) && values.Length > maximum.GetInt32())
        {
            return false;
        }
        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
        {
            for (var left = 0; left < values.Length; left++)
            {
                for (var right = left + 1; right < values.Length; right++)
                {
                    if (JsonElement.DeepEquals(values[left], values[right])) return false;
                }
            }
        }
        var prefixCount = 0;
        if (schema.TryGetProperty("prefixItems", out var prefixItems))
        {
            var prefixes = prefixItems.EnumerateArray().ToArray();
            prefixCount = Math.Min(prefixes.Length, values.Length);
            for (var index = 0; index < prefixCount; index++)
            {
                if (!IsValid(prefixes[index], values[index], rootSchema, referencedSchemas)) return false;
            }
        }
        if (schema.TryGetProperty("items", out var items)
            && values.Skip(prefixCount).Any(item => !IsValid(items, item, rootSchema, referencedSchemas)))
        {
            return false;
        }
        if (!schema.TryGetProperty("contains", out var contains)) return true;

        var containsCount = values.Count(item => IsValid(contains, item, rootSchema, referencedSchemas));
        var minimumContains = schema.TryGetProperty("minContains", out var containsMinimum)
            ? containsMinimum.GetInt32()
            : 1;
        var maximumContains = schema.TryGetProperty("maxContains", out var containsMaximum)
            ? containsMaximum.GetInt32()
            : int.MaxValue;
        return containsCount >= minimumContains && containsCount <= maximumContains;
    }

    private static bool IsStringValid(JsonElement schema, JsonElement instance)
    {
        var value = instance.GetString()!;
        var length = value.EnumerateRunes().Count();
        return (!schema.TryGetProperty("minLength", out var minimum) || length >= minimum.GetInt32())
               && (!schema.TryGetProperty("maxLength", out var maximum) || length <= maximum.GetInt32())
                && (!schema.TryGetProperty("pattern", out var pattern)
                    || Regex.IsMatch(
                        value,
                        pattern.GetString()!.Replace("$", "\\z", StringComparison.Ordinal),
                        RegexOptions.CultureInvariant));
    }

    private static bool IsNumberValid(JsonElement schema, JsonElement instance)
    {
        if (!decimal.TryParse(
                instance.GetRawText(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return !schema.TryGetProperty("minimum", out _) && !schema.TryGetProperty("maximum", out _);
        }
        return (!schema.TryGetProperty("minimum", out var minimum) || value >= minimum.GetDecimal())
               && (!schema.TryGetProperty("maximum", out var maximum) || value <= maximum.GetDecimal());
    }

    private static bool MatchesType(string type, JsonElement value)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && IsMathematicalInteger(value.GetRawText()),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static bool IsMathematicalInteger(string token)
    {
        var unsigned = token[0] == '-' ? token.AsSpan(1) : token.AsSpan();
        var exponentMarker = unsigned.IndexOfAny('e', 'E');
        var significand = exponentMarker >= 0 ? unsigned[..exponentMarker] : unsigned;
        var exponent = 0;
        if (exponentMarker >= 0
            && !int.TryParse(
                unsigned[(exponentMarker + 1)..],
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out exponent))
        {
            exponent = unsigned[exponentMarker + 1] == '-' ? int.MinValue : int.MaxValue;
        }
        var decimalPoint = significand.IndexOf('.');
        var fractionalDigits = decimalPoint >= 0 ? significand.Length - decimalPoint - 1 : 0;
        var requiredTrailingZeros = (long)fractionalDigits - exponent;
        if (requiredTrailingZeros <= 0) return true;

        var digits = decimalPoint >= 0
            ? string.Concat(significand[..decimalPoint], significand[(decimalPoint + 1)..])
            : significand.ToString();
        if (digits.AsSpan().IndexOfAnyExcept('0') < 0) return true;
        if (requiredTrailingZeros > digits.Length) return false;
        return digits.AsSpan(digits.Length - (int)requiredTrailingZeros).IndexOfAnyExcept('0') < 0;
    }
}
