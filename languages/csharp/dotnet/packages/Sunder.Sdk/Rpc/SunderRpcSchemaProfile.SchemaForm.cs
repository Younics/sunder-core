using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcSchemaProfile
{
    private static void ValidateDefinitionsCore(IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var nodes = 0;
        foreach (var definition in definitions)
        {
            ValidateSchema(
                definition.Value,
                "$.$defs." + definition.Key,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                ref nodes,
                depth: 1);
        }

        ValidateReferenceCycles(definitions);
        ValidateResolvedDepth(definitions);
    }

    private static void ValidateSchema(
        JsonElement schema,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth)
    {
        if (depth > MaximumSchemaDepth)
        {
            throw Error($"{path} exceeds the RPC schema depth limit of {MaximumSchemaDepth}.");
        }
        if (++nodes > MaximumSchemaNodes)
        {
            throw Error($"{path} exceeds the RPC schema node-count limit.");
        }
        RequireObject(schema, path);
        ValidateAnnotations(schema, path);

        if (schema.TryGetProperty("$ref", out var referenceElement))
        {
            foreach (var property in schema.EnumerateObject())
            {
                if (property.Name != "$ref")
                {
                    throw Error(
                        $"{path} contains unsupported $ref sibling '{property.Name}'.",
                        "schema-reference-shape");
                }
            }
            if (referenceElement.ValueKind != JsonValueKind.String
                || !SunderRpcDescriptorParser.TryGetReferenceName(referenceElement.GetString(), out var referenceName)
                || !definitions.ContainsKey(referenceName))
            {
                throw Error(
                    $"{path} contains a remote, malformed, or missing $ref.",
                    "schema-reference-shape");
            }
            return;
        }

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            SunderRpcDescriptorParser.RequireOnlyProperties(schema, path, "oneOf", "title", "description");
            var nullAcceptingVariants = CountNullAcceptingVariants(oneOf, definitions);
            if (nullAcceptingVariants > 0)
            {
                if (!IsNullableUnion(oneOf, definitions, nullAcceptingVariants))
                {
                    throw Error($"{path}.oneOf must have exactly one direct null variant and exactly one branch that accepts null.");
                }
                ValidateNullableUnion(
                    oneOf,
                    path + ".oneOf",
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth);
            }
            else
            {
                ValidateTaggedUnion(
                    oneOf,
                    path + ".oneOf",
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth);
            }
            return;
        }

        if (schema.EnumerateObject().All(static property =>
                property.Name is "title" or "description"))
        {
            return;
        }

        var typeElement = SunderRpcDescriptorParser.Required(schema, "type", path, JsonValueKind.String);
        var type = typeElement.GetString();
        if (schema.TryGetProperty("logicalType", out _))
        {
            ValidateLogicalSchema(schema, path, type);
            return;
        }
        switch (type)
        {
            case "object":
                if (!schema.TryGetProperty("additionalProperties", out _))
                {
                    ValidateDynamicObjectSchema(schema, path);
                }
                else
                {
                    ValidateObjectSchema(
                        schema,
                        path,
                        definitions,
                        referenceStack,
                        validatedDefinitions,
                        ref nodes,
                        depth);
                }
                break;
            case "array":
                ValidateArraySchema(
                    schema,
                    path,
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth);
                break;
            case "string":
                if (schema.TryGetProperty("contentEncoding", out _))
                {
                    ValidateBase64Schema(schema, path);
                    return;
                }
                if (schema.TryGetProperty("format", out _))
                {
                    ValidateFormattedStringSchema(schema, path);
                    return;
                }
                ValidateStringSchema(schema, path);
                break;
            case "integer":
                ValidateIntegerSchema(schema, path);
                break;
            case "number":
                ValidateFloatingPointSchema(schema, path);
                break;
            case "boolean":
            case "null":
                SunderRpcDescriptorParser.RequireOnlyProperties(
                    schema,
                    path,
                    "type",
                    "enum",
                    "const",
                    "title",
                    "description");
                break;
            default:
                throw Error($"{path}.type '{type}' is outside the RPC schema profile.");
        }

        ValidateEnumAndConst(schema, path, type!);
    }

    private static void ValidateObjectSchema(
        JsonElement schema,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth)
    {
        var properties = schema.TryGetProperty("properties", out var propertiesElement)
            ? propertiesElement
            : default;
        var hasProperties = properties.ValueKind != JsonValueKind.Undefined;
        if (hasProperties) RequireObject(properties, path + ".properties");
        var additional = SunderRpcDescriptorParser.Required(schema, "additionalProperties", path);
        var isRecord = additional.ValueKind == JsonValueKind.False;
        var isMap = additional.ValueKind == JsonValueKind.Object;
        if (!isRecord && !isMap)
        {
            throw Error($"{path}.additionalProperties must be false for records or a bounded value schema for maps.");
        }

        if (isRecord)
        {
            SunderRpcDescriptorParser.RequireOnlyProperties(
                schema,
                path,
                "type",
                "properties",
                "required",
                "additionalProperties",
                "minProperties",
                "maxProperties",
                "title",
                "description");
        }
        else
        {
            SunderRpcDescriptorParser.RequireOnlyProperties(
                schema,
                path,
                "type",
                "properties",
                "required",
                "additionalProperties",
                "propertyNames",
                "minProperties",
                "maxProperties",
                "title",
                "description");
        }
        if (isRecord)
        {
            if (!hasProperties)
            {
                throw Error($"{path}.properties is required for a closed object schema.");
            }
            var propertyCount = properties.GetPropertyCount();
            if (propertyCount > MaximumProperties)
            {
                throw Error($"{path}.properties exceeds the {MaximumProperties}-property limit.");
            }
            var required = SunderRpcDescriptorParser.Required(schema, "required", path, JsonValueKind.Array);
            if (required.GetArrayLength() > MaximumProperties)
            {
                throw Error($"{path}.required exceeds the {MaximumProperties}-property limit.");
            }
            var requiredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(item.GetString())
                    || !requiredNames.Add(item.GetString()!))
                {
                    throw Error($"{path}.required must contain unique non-empty property names.");
                }
            }
            foreach (var requiredName in requiredNames)
            {
                if (!properties.TryGetProperty(requiredName, out _))
                {
                    throw Error($"{path}.required references undeclared property '{requiredName}'.");
                }
            }
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name.Length == 0
                    || SunderRpcDescriptorParser.CountUnicodeScalars(property.Name) > 128)
                {
                    throw Error($"{path}.properties contains an empty or overlong property name.");
                }
                ValidateSchema(
                    property.Value,
                    path + ".properties." + property.Name,
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth + 1);
            }
            ValidateOptionalPropertyBounds(schema, path);
            if (schema.TryGetProperty("propertyNames", out _))
            {
                throw Error($"{path}.propertyNames is only supported for bounded map schemas.");
            }
        }
        else
        {
            if (hasProperties && properties.GetPropertyCount() != 0)
            {
                throw Error($"{path} cannot mix named properties with map additionalProperties.");
            }
            if (schema.TryGetProperty("required", out var required)
                && (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() != 0))
            {
                throw Error($"{path}.required must be absent or empty for a map schema.");
            }
            var minimum = OptionalBound(schema, "minProperties", path, 0, MaximumSafeInteger, 0);
            var maximum = OptionalBound(schema, "maxProperties", path, 0, MaximumSafeInteger, long.MaxValue);
            if (minimum > maximum)
            {
                throw Error($"{path}.minProperties must not exceed maxProperties.");
            }
            var propertyNames = SunderRpcDescriptorParser.Required(schema, "propertyNames", path, JsonValueKind.Object);
            ValidateSchema(
                propertyNames,
                path + ".propertyNames",
                definitions,
                referenceStack,
                validatedDefinitions,
                ref nodes,
                depth + 1);
            if (!propertyNames.TryGetProperty("type", out var keyType)
                || keyType.GetString() != "string"
                || propertyNames.TryGetProperty("logicalType", out _)
                || propertyNames.TryGetProperty("format", out _)
                || propertyNames.TryGetProperty("contentEncoding", out _))
            {
                throw Error($"{path}.propertyNames must be an ordinary string schema.");
            }
            ValidateSchema(
                additional,
                path + ".additionalProperties",
                definitions,
                referenceStack,
                validatedDefinitions,
                ref nodes,
                depth + 1);
        }
    }

    private static void ValidateArraySchema(
        JsonElement schema,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "items",
            "minItems",
            "maxItems",
            "title",
            "description");
        var minimum = OptionalBound(schema, "minItems", path, 0, MaximumSafeInteger, 0);
        var maximum = OptionalBound(schema, "maxItems", path, 0, MaximumSafeInteger, long.MaxValue);
        if (minimum > maximum)
        {
            throw Error($"{path}.minItems must not exceed maxItems.");
        }
        var items = SunderRpcDescriptorParser.Required(schema, "items", path, JsonValueKind.Object);
        ValidateSchema(
            items,
            path + ".items",
            definitions,
            referenceStack,
            validatedDefinitions,
            ref nodes,
            depth + 1);
    }

    private static void ValidateDynamicObjectSchema(JsonElement schema, string path)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "maxProperties",
            "title",
            "description");
        if (schema.TryGetProperty("maxProperties", out _))
        {
            _ = RequiredBound(schema, "maxProperties", path, 0, MaximumSafeInteger);
        }
    }

    private static void ValidateStringSchema(JsonElement schema, string path)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "minLength",
            "maxLength",
            "enum",
            "const",
            "title",
            "description");
        var minimum = OptionalBound(schema, "minLength", path, 0, MaximumSafeInteger, 0);
        var maximum = OptionalBound(schema, "maxLength", path, 0, MaximumSafeInteger, long.MaxValue);
        if (minimum > maximum)
        {
            throw Error($"{path}.minLength must not exceed maxLength.");
        }
    }

    private static void ValidateBase64Schema(JsonElement schema, string path)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "contentEncoding",
            "maxDecodedBytes",
            "title",
            "description");
        var encoding = SunderRpcDescriptorParser.Required(
            schema,
            "contentEncoding",
            path,
            JsonValueKind.String).GetString();
        if (encoding != "base64")
        {
            throw Error($"{path}.contentEncoding must be 'base64'.");
        }
        if (schema.TryGetProperty("maxDecodedBytes", out _))
        {
            _ = RequiredBound(schema, "maxDecodedBytes", path, 0, MaximumSafeInteger);
        }
    }

    private static void ValidateFormattedStringSchema(JsonElement schema, string path)
    {
        var format = SunderRpcDescriptorParser.Required(
            schema,
            "format",
            path,
            JsonValueKind.String).GetString()!;
        switch (format)
        {
            case "uuid":
            case "date":
            case "date-time":
            case "duration":
            case "uri":
                SunderRpcDescriptorParser.RequireOnlyProperties(
                    schema,
                    path,
                    "type",
                    "format",
                    "title",
                    "description");
                break;
            case "big-integer":
                SunderRpcDescriptorParser.RequireOnlyProperties(
                    schema,
                    path,
                    "type",
                    "format",
                    "maxDigits",
                    "valueMinimum",
                    "valueExclusiveMinimum",
                    "valueMaximum",
                    "valueExclusiveMaximum",
                    "title",
                    "description");
                if (schema.TryGetProperty("maxDigits", out _))
                {
                    _ = RequiredBound(schema, "maxDigits", path, 1, MaximumSafeInteger);
                }
                ValidateExactValueBounds(schema, path, format);
                break;
            case "decimal":
                SunderRpcDescriptorParser.RequireOnlyProperties(
                    schema,
                    path,
                    "type",
                    "format",
                    "maxPrecision",
                    "maxScale",
                    "valueMinimum",
                    "valueExclusiveMinimum",
                    "valueMaximum",
                    "valueExclusiveMaximum",
                    "title",
                    "description");
                if (schema.TryGetProperty("maxPrecision", out _))
                {
                    _ = RequiredBound(schema, "maxPrecision", path, 1, MaximumSafeInteger);
                }
                if (schema.TryGetProperty("maxScale", out _))
                {
                    _ = RequiredBound(schema, "maxScale", path, 0, MaximumSafeInteger);
                }
                ValidateExactValueBounds(schema, path, format);
                break;
            default:
                throw Error($"{path}.format '{format}' is outside the RPC value profile.");
        }

    }

    private static void ValidateIntegerSchema(JsonElement schema, string path)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "minimum",
            "maximum",
            "exclusiveMinimum",
            "exclusiveMaximum",
            "enum",
            "const",
            "title",
            "description");
        ValidateNumericBoundShape(schema, path, requireBoth: false);
        long? lower = schema.TryGetProperty("minimum", out _)
            ? ReadSafeIntegerKeyword(schema, "minimum", path)
            : schema.TryGetProperty("exclusiveMinimum", out _)
                ? ReadSafeIntegerKeyword(schema, "exclusiveMinimum", path)
                : null;
        long? upper = schema.TryGetProperty("maximum", out _)
            ? ReadSafeIntegerKeyword(schema, "maximum", path)
            : schema.TryGetProperty("exclusiveMaximum", out _)
                ? ReadSafeIntegerKeyword(schema, "exclusiveMaximum", path)
                : null;
        if (lower > upper)
        {
            throw Error($"{path}'s lower numeric bound must not exceed its upper bound.");
        }
    }

    private static void ValidateFloatingPointSchema(JsonElement schema, string path)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "format",
            "minimum",
            "maximum",
            "exclusiveMinimum",
            "exclusiveMaximum",
            "enum",
            "const",
            "title",
            "description");
        var format = SunderRpcDescriptorParser.Required(
            schema,
            "format",
            path,
            JsonValueKind.String).GetString();
        if (format is not ("float32" or "float64"))
        {
            throw Error($"{path}.format must be 'float32' or 'float64' for a number schema.");
        }
        ValidateNumericBoundShape(schema, path, requireBoth: false);
        double? lower = schema.TryGetProperty("minimum", out _)
            ? ReadFloatingPointKeyword(schema, "minimum", path, format)
            : schema.TryGetProperty("exclusiveMinimum", out _)
                ? ReadFloatingPointKeyword(schema, "exclusiveMinimum", path, format)
                : null;
        double? upper = schema.TryGetProperty("maximum", out _)
            ? ReadFloatingPointKeyword(schema, "maximum", path, format)
            : schema.TryGetProperty("exclusiveMaximum", out _)
                ? ReadFloatingPointKeyword(schema, "exclusiveMaximum", path, format)
                : null;
        if (lower > upper)
        {
            throw Error($"{path}'s lower numeric bound must not exceed its upper bound.");
        }
    }

    private static void ValidateNumericBoundShape(JsonElement schema, string path, bool requireBoth)
    {
        var lowerCount = (schema.TryGetProperty("minimum", out _) ? 1 : 0)
                         + (schema.TryGetProperty("exclusiveMinimum", out _) ? 1 : 0);
        var upperCount = (schema.TryGetProperty("maximum", out _) ? 1 : 0)
                         + (schema.TryGetProperty("exclusiveMaximum", out _) ? 1 : 0);
        if (lowerCount > 1 || upperCount > 1
            || requireBoth && (lowerCount != 1 || upperCount != 1))
        {
            throw Error(requireBoth
                ? $"{path} must declare exactly one lower and one upper numeric bound."
                : $"{path} cannot mix inclusive and exclusive bounds on one side.");
        }
    }

    private static void ValidateLogicalSchema(JsonElement schema, string path, string? type)
    {
        SunderRpcDescriptorParser.RequireOnlyProperties(
            schema,
            path,
            "type",
            "logicalType",
            "title",
            "description");
        var logicalType = SunderRpcDescriptorParser.Required(
            schema,
            "logicalType",
            path,
            JsonValueKind.String).GetString();
        var valid = logicalType == "content-reference" && type == "object";
        if (!valid)
        {
            throw Error($"{path} has an unsupported logical type or underlying type.");
        }
    }
}
