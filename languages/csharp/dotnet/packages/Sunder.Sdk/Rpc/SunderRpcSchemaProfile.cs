using System.Globalization;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static class SunderRpcSchemaProfile
{
    private const int MaximumSchemaDepth = 64;
    private const int MaximumSchemaNodes = 4096;
    private const int MaximumProperties = 256;
    private const int MaximumV1CollectionItems = 1_000_000;
    private const int MaximumV1StringCharacters = 1_000_000;
    private const int MaximumUnionVariants = 16;
    private const int MaximumEnumValues = 256;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly string[] AnnotationProperties = ["title", "description"];

    public static void ValidateDefinitions(
        IReadOnlyDictionary<string, JsonElement> definitions,
        int descriptorVersion)
    {
        try
        {
            ValidateDefinitionsCore(definitions, descriptorVersion);
        }
        catch (SunderRpcDescriptorException exception)
            when (exception.ErrorCode == "descriptor-representation")
        {
            throw Error(exception.Message);
        }
    }

    private static void ValidateDefinitionsCore(
        IReadOnlyDictionary<string, JsonElement> definitions,
        int descriptorVersion)
    {
        var isV2 = descriptorVersion == SunderRpcProtocol.DescriptorVersionV2;
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
                depth: 1,
                isV2);
        }

        ValidateReferenceCycles(definitions, allowProductiveBoundaries: isV2);
        ValidateResolvedDepth(definitions);
    }

    public static bool IsInstanceValid(
        SunderRpcContractDescriptor descriptor,
        string schemaReference,
        JsonElement value,
        out string? error)
    {
        if (!SunderRpcDescriptorParser.TryGetReferenceName(schemaReference, out var name)
            || !descriptor.TryGetDefinition(name, out var schema))
        {
            error = "The schema reference is not a definition in this contract.";
            return false;
        }

        var state = new InstanceValidationState(descriptor);
        return ValidateInstance(schema, value, "$", state, out error);
    }

    private static void ValidateSchema(
        JsonElement schema,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth,
        bool isV2)
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
            if (isV2)
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
            }
            else
            {
                SunderRpcDescriptorParser.RequireOnlyProperties(schema, path, "$ref", "title", "description");
            }
            if (referenceElement.ValueKind != JsonValueKind.String
                || !SunderRpcDescriptorParser.TryGetReferenceName(referenceElement.GetString(), out var referenceName)
                || !definitions.ContainsKey(referenceName))
            {
                throw Error(
                    $"{path} contains a remote, malformed, or missing $ref.",
                    isV2 ? "schema-reference-shape" : "schema-profile");
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
                    depth + 1,
                    isV2);
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
                    depth + 1,
                    isV2);
            }
            return;
        }

        if (isV2 && schema.EnumerateObject().All(static property =>
                property.Name is "title" or "description"))
        {
            return;
        }

        var typeElement = SunderRpcDescriptorParser.Required(schema, "type", path, JsonValueKind.String);
        var type = typeElement.GetString();
        if (isV2 && schema.TryGetProperty("logicalType", out _))
        {
            ValidateLogicalSchema(schema, path, type);
            return;
        }
        switch (type)
        {
            case "object":
                if (isV2 && !schema.TryGetProperty("additionalProperties", out _))
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
                        depth,
                        isV2);
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
                    depth,
                    isV2);
                break;
            case "string":
                if (isV2 && schema.TryGetProperty("contentEncoding", out _))
                {
                    ValidateBase64Schema(schema, path);
                    return;
                }
                if (isV2 && schema.TryGetProperty("format", out _))
                {
                    ValidateFormattedStringSchema(schema, path);
                    return;
                }
                ValidateStringSchema(schema, path, isV2);
                break;
            case "integer":
                ValidateIntegerSchema(schema, path, isV2);
                break;
            case "number":
                if (isV2)
                {
                    ValidateFloatingPointSchema(schema, path);
                }
                else
                {
                    ValidateNumberSchemaV1(schema, path, type);
                }
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

        ValidateEnumAndConst(schema, path, type!, isV2);
    }

    private static void ValidateObjectSchema(
        JsonElement schema,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth,
        bool isV2)
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

        if (isV2 && isRecord)
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
        else if (isV2)
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
                "enum",
                "const",
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
                    depth + 1,
                    isV2);
            }
            ValidateOptionalPropertyBounds(schema, path, propertyCount, isV2);
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
            var minimum = isV2
                ? OptionalBound(schema, "minProperties", path, 0, MaximumSafeInteger, 0)
                : RequiredBound(schema, "minProperties", path, 0, MaximumV1CollectionItems);
            var maximum = isV2
                ? OptionalBound(schema, "maxProperties", path, 0, MaximumSafeInteger, long.MaxValue)
                : RequiredBound(schema, "maxProperties", path, 0, MaximumV1CollectionItems);
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
                depth + 1,
                isV2);
            if (!propertyNames.TryGetProperty("type", out var keyType)
                || keyType.GetString() != "string"
                || propertyNames.TryGetProperty("logicalType", out _)
                || propertyNames.TryGetProperty("format", out _)
                || propertyNames.TryGetProperty("contentEncoding", out _)
                || !isV2 && (!propertyNames.TryGetProperty("minLength", out _)
                             || !propertyNames.TryGetProperty("maxLength", out _)))
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
                depth + 1,
                isV2);
        }
    }

    private static void ValidateArraySchema(
        JsonElement schema,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth,
        bool isV2)
    {
        if (isV2)
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
        }
        else
        {
            SunderRpcDescriptorParser.RequireOnlyProperties(
                schema,
                path,
                "type",
                "items",
                "minItems",
                "maxItems",
                "enum",
                "const",
                "title",
                "description");
        }
        var minimum = isV2
            ? OptionalBound(schema, "minItems", path, 0, MaximumSafeInteger, 0)
            : RequiredBound(schema, "minItems", path, 0, MaximumV1CollectionItems);
        var maximum = isV2
            ? OptionalBound(schema, "maxItems", path, 0, MaximumSafeInteger, long.MaxValue)
            : RequiredBound(schema, "maxItems", path, 0, MaximumV1CollectionItems);
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
            depth + 1,
            isV2);
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

    private static void ValidateStringSchema(JsonElement schema, string path, bool isV2)
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
        var minimum = isV2
            ? OptionalBound(schema, "minLength", path, 0, MaximumSafeInteger, 0)
            : RequiredBound(schema, "minLength", path, 0, MaximumV1StringCharacters);
        var maximum = isV2
            ? OptionalBound(schema, "maxLength", path, 0, MaximumSafeInteger, long.MaxValue)
            : RequiredBound(schema, "maxLength", path, 0, MaximumV1StringCharacters);
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

    private static void ValidateIntegerSchema(JsonElement schema, string path, bool isV2)
    {
        if (!isV2)
        {
            ValidateNumberSchemaV1(schema, path, "integer");
            return;
        }
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

    private static void ValidateNumberSchemaV1(JsonElement schema, string path, string type)
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
        ValidateNumericBoundShape(schema, path, requireBoth: true);
        var lower = ReadNumberBound(schema, schema.TryGetProperty("minimum", out _) ? "minimum" : "exclusiveMinimum", path);
        var upper = ReadNumberBound(schema, schema.TryGetProperty("maximum", out _) ? "maximum" : "exclusiveMaximum", path);
        if (lower > upper)
        {
            throw Error($"{path}'s lower numeric bound must not exceed its upper bound.");
        }
        if (type == "integer" && (decimal.Truncate(lower) != lower || decimal.Truncate(upper) != upper))
        {
            throw Error($"{path} integer bounds must be integers.");
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

    private static void ValidateTaggedUnion(
        JsonElement oneOf,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth,
        bool isV2)
    {
        if (oneOf.ValueKind != JsonValueKind.Array
            || oneOf.GetArrayLength() is < 2 or > MaximumUnionVariants)
        {
            throw Error($"{path} must contain between 2 and {MaximumUnionVariants} variants.");
        }

        var variants = new List<JsonElement>();
        var index = 0;
        foreach (var variant in oneOf.EnumerateArray())
        {
            ValidateSchema(
                variant,
                path + "[" + index++ + "]",
                definitions,
                referenceStack,
                validatedDefinitions,
                ref nodes,
                depth + 1,
                isV2);
            variants.Add(ResolveSchema(variant, definitions));
        }

        if (variants.Any(static variant => !IsClosedObjectSchema(variant)))
        {
            throw Error($"{path} variants must resolve to closed object schemas.");
        }

        var candidates = GetTagCandidates(variants[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var variant in variants.Skip(1))
        {
            candidates.IntersectWith(GetTagCandidates(variant));
        }

        var validCandidates = candidates.Where(candidate => HasUniqueStringTags(variants, candidate)).ToArray();
        if (validCandidates.Length != 1)
        {
            throw Error($"{path} must have exactly one unambiguous required string const discriminator.");
        }
    }

    private static bool IsNullableUnion(
        JsonElement oneOf,
        IReadOnlyDictionary<string, JsonElement> definitions,
        int nullAcceptingVariants)
        => oneOf.ValueKind == JsonValueKind.Array
           && oneOf.GetArrayLength() == 2
           && nullAcceptingVariants == 1
           && oneOf.EnumerateArray().Count(variant =>
               TryResolveSchema(variant, definitions, out var resolved) && IsNullSchema(resolved)) == 1;

    private static int CountNullAcceptingVariants(
        JsonElement oneOf,
        IReadOnlyDictionary<string, JsonElement> definitions)
        => CountNullAcceptingVariants(
            oneOf,
            definitions,
            new HashSet<string>(StringComparer.Ordinal));

    private static int CountNullAcceptingVariants(
        JsonElement oneOf,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack)
        => oneOf.ValueKind == JsonValueKind.Array
            ? oneOf.EnumerateArray().Count(variant =>
                SchemaAcceptsNull(variant, definitions, referenceStack))
            : 0;

    private static bool SchemaAcceptsNull(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String
                || !SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name)
                || !referenceStack.Add(name)
                || !definitions.TryGetValue(name, out var target))
            {
                return false;
            }
            var acceptsNull = SchemaAcceptsNull(target, definitions, referenceStack);
            referenceStack.Remove(name);
            return acceptsNull;
        }
        if (IsNullSchema(schema))
        {
            return true;
        }
        if (!schema.TryGetProperty("oneOf", out var oneOf)
            || oneOf.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return CountNullAcceptingVariants(oneOf, definitions, referenceStack) == 1;
    }

    private static void ValidateNullableUnion(
        JsonElement oneOf,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth,
        bool isV2)
    {
        var index = 0;
        foreach (var variant in oneOf.EnumerateArray())
        {
            ValidateSchema(
                variant,
                path + "[" + index++ + "]",
                definitions,
                referenceStack,
                validatedDefinitions,
                ref nodes,
                depth + 1,
                isV2);
        }
    }

    private static bool IsNullSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type) && type.GetString() == "null";

    private static IEnumerable<string> GetTagCandidates(JsonElement schema)
    {
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            var resolved = property.Value;
            if (required.Contains(property.Name)
                && resolved.TryGetProperty("type", out var type)
                && type.GetString() == "string"
                && resolved.TryGetProperty("const", out var constant)
                && constant.ValueKind == JsonValueKind.String)
            {
                yield return property.Name;
            }
        }
    }

    private static bool HasUniqueStringTags(IReadOnlyList<JsonElement> variants, string propertyName)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var property = variant.GetProperty("properties").GetProperty(propertyName);
            if (!property.TryGetProperty("const", out var constant)
                || constant.ValueKind != JsonValueKind.String
                || !tags.Add(constant.GetString()!))
            {
                return false;
            }
        }
        return true;
    }

    private static JsonElement ResolveSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        if (!TryResolveSchema(schema, definitions, out var resolved))
        {
            throw Error("A schema reference could not be resolved.");
        }
        return resolved;
    }

    private static bool TryResolveSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions,
        out JsonElement resolved)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (schema.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String
                || !SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name)
                || !visited.Add(name)
                || !definitions.TryGetValue(name, out schema))
            {
                resolved = default;
                return false;
            }
        }
        resolved = schema;
        return true;
    }

    private static void ValidateResolvedDepth(IReadOnlyDictionary<string, JsonElement> definitions)
    {
        foreach (var name in definitions.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {
            var height = GetSchemaHeight(
                definitions[name],
                definitions,
                new HashSet<string>(StringComparer.Ordinal) { name });
            if (height > MaximumSchemaDepth)
            {
                throw Error($"$.$defs.{name} exceeds the resolved RPC schema depth limit of {MaximumSchemaDepth}.");
            }
        }
    }

    private static int GetSchemaHeight(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            if (!referenceStack.Add(name)) return 1;
            var height = 1 + GetSchemaHeight(definitions[name], definitions, referenceStack);
            referenceStack.Remove(name);
            return height;
        }

        var maximumChildHeight = 0;
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            foreach (var variant in oneOf.EnumerateArray())
            {
                maximumChildHeight = Math.Max(
                    maximumChildHeight,
                    GetSchemaHeight(variant, definitions, referenceStack));
            }
        }
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                maximumChildHeight = Math.Max(
                    maximumChildHeight,
                    GetSchemaHeight(property.Value, definitions, referenceStack));
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            maximumChildHeight = Math.Max(
                maximumChildHeight,
                GetSchemaHeight(items, definitions, referenceStack));
        }
        if (schema.TryGetProperty("propertyNames", out var propertyNames))
        {
            maximumChildHeight = Math.Max(
                maximumChildHeight,
                GetSchemaHeight(propertyNames, definitions, referenceStack));
        }
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.Object)
        {
            maximumChildHeight = Math.Max(
                maximumChildHeight,
                GetSchemaHeight(additional, definitions, referenceStack));
        }
        return 1 + maximumChildHeight;
    }

    private static void ValidateReferenceCycles(
        IReadOnlyDictionary<string, JsonElement> definitions,
        bool allowProductiveBoundaries)
    {
        var requiredEdges = definitions.Keys.ToDictionary(
            static name => name,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            CollectRequiredReferenceEdges(
                definition.Value,
                definition.Key,
                definitions,
                requiredEdges,
                hasProductiveBoundary: false,
                allowProductiveBoundaries);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in definitions.Keys)
        {
            if (HasRequiredReferenceCycle(name, requiredEdges, visited, active))
            {
                throw Error(
                    $"$.$defs.{name} contains a non-terminating required reference cycle.",
                    "schema-reference-cycle");
            }
        }
    }

    private static void CollectRequiredReferenceEdges(
        JsonElement schema,
        string sourceDefinition,
        IReadOnlyDictionary<string, JsonElement> definitions,
        IReadOnlyDictionary<string, HashSet<string>> requiredEdges,
        bool hasProductiveBoundary,
        bool allowProductiveBoundaries)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            if (!hasProductiveBoundary
                && SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var target)
                && definitions.ContainsKey(target))
            {
                requiredEdges[sourceDefinition].Add(target);
            }
            return;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            foreach (var variant in oneOf.EnumerateArray())
            {
                CollectRequiredReferenceEdges(
                    variant,
                    sourceDefinition,
                    definitions,
                    requiredEdges,
                    hasProductiveBoundary,
                    allowProductiveBoundaries);
            }
        }
        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            var requiredNames = schema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                CollectRequiredReferenceEdges(
                    property.Value,
                    sourceDefinition,
                    definitions,
                    requiredEdges,
                    hasProductiveBoundary
                    || allowProductiveBoundaries && !requiredNames.Contains(property.Name),
                    allowProductiveBoundaries);
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            var mayBeEmpty = !schema.TryGetProperty("minItems", out var minimum)
                             || TryGetSafeInteger(minimum, out var minimumValue) && minimumValue == 0;
            CollectRequiredReferenceEdges(
                items,
                sourceDefinition,
                definitions,
                requiredEdges,
                hasProductiveBoundary || allowProductiveBoundaries && mayBeEmpty,
                allowProductiveBoundaries);
        }
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.Object)
        {
            var mayBeEmpty = !schema.TryGetProperty("minProperties", out var minimum)
                             || TryGetSafeInteger(minimum, out var minimumValue) && minimumValue == 0;
            CollectRequiredReferenceEdges(
                additional,
                sourceDefinition,
                definitions,
                requiredEdges,
                hasProductiveBoundary || allowProductiveBoundaries && mayBeEmpty,
                allowProductiveBoundaries);
        }
    }

    private static bool HasRequiredReferenceCycle(
        string name,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        HashSet<string> visited,
        HashSet<string> active)
    {
        if (active.Contains(name)) return true;
        if (!visited.Add(name)) return false;
        active.Add(name);
        foreach (var target in edges[name])
        {
            if (HasRequiredReferenceCycle(target, edges, visited, active)) return true;
        }
        active.Remove(name);
        return false;
    }

    private static bool IsClosedObjectSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type)
           && type.GetString() == "object"
           && schema.TryGetProperty("additionalProperties", out var additional)
           && additional.ValueKind == JsonValueKind.False;

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

    private static bool ValidateInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        var pending = new Stack<InstanceWork>();
        pending.Push(new InstanceWork(schema, value, path));
        while (pending.TryPop(out var work))
        {
            if (!ValidateInstanceValue(work.Schema, work.Value, work.Path, state, pending, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateInstanceValue(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        Stack<InstanceWork> pending,
        out string? error)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            state.Descriptor.TryGetDefinition(name, out var target);
            schema = target;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            if (!TrySelectUnionVariant(oneOf, value, path, state, out var variant, out error))
            {
                return false;
            }
            pending.Push(new InstanceWork(variant, value, path));
            error = null;
            return true;
        }

        if (state.IsV2 && schema.EnumerateObject().All(static property =>
                property.Name is "title" or "description"))
        {
            return ValidateUnconstrainedJsonValue(value, path, state, out error);
        }

        var type = schema.GetProperty("type").GetString()!;
        if (state.IsV2 && schema.TryGetProperty("logicalType", out var logicalType))
        {
            return ValidateLogicalInstance(logicalType.GetString()!, value, path, state, out error);
        }
        if (!MatchesType(value, type, state.IsV2))
        {
            error = $"{path} must be of type '{type}'.";
            return false;
        }
        string? scalarText = null;
        if (type == "string" && !TryGetScalarString(value, out scalarText))
        {
            error = $"{path} must contain Unicode scalar values.";
            return false;
        }
        if (schema.TryGetProperty("const", out var constant)
            && !LiteralEquals(value, constant, state.IsV2, schema))
        {
            error = $"{path} does not match its const value.";
            return false;
        }
        if (schema.TryGetProperty("enum", out var enumElement)
            && !enumElement.EnumerateArray().Any(item => LiteralEquals(value, item, state.IsV2, schema)))
        {
            error = $"{path} is not an allowed enum value.";
            return false;
        }

        switch (type)
        {
            case "object":
                if (state.IsV2 && !schema.TryGetProperty("additionalProperties", out _))
                {
                    return ValidateDynamicObjectInstance(schema, value, path, state, out error);
                }
                return ValidateObjectInstance(schema, value, path, state, pending, out error);
            case "array":
                return ValidateArrayInstance(schema, value, path, pending, out error);
            case "string":
                if (state.IsV2 && schema.TryGetProperty("contentEncoding", out _))
                {
                    return ValidateBase64Instance(schema, scalarText!, path, out error);
                }
                if (state.IsV2 && schema.TryGetProperty("format", out var format))
                {
                    if (!ValidateFormattedStringInstance(schema, format.GetString()!, scalarText!, path, out error))
                    {
                        return false;
                    }
                    break;
                }
                var length = scalarText!.EnumerateRunes().Count();
                var minLength = schema.TryGetProperty("minLength", out var minimumLength)
                    ? ReadQuantitativeBound(minimumLength)
                    : 0;
                var maxLength = schema.TryGetProperty("maxLength", out var maximumLength)
                    ? ReadQuantitativeBound(maximumLength)
                    : long.MaxValue;
                if (length < minLength || length > maxLength)
                {
                    error = $"{path} has {length} characters outside its declared length bounds.";
                    return false;
                }
                break;
            case "integer":
            case "number":
                if (!ValidateNumberInstance(schema, value, path, state.IsV2, out error)) return false;
                break;
        }
        error = null;
        return true;
    }

    private static bool TrySelectUnionVariant(
        JsonElement oneOf,
        JsonElement value,
        string path,
        InstanceValidationState state,
        out JsonElement selected,
        out string? error)
    {
        var variants = oneOf.EnumerateArray().ToArray();
        var resolved = variants.Select(variant => ResolveSchema(variant, state.Descriptor.Definitions)).ToArray();
        var nullIndex = Array.FindIndex(resolved, IsNullSchema);
        if (variants.Length == 2 && nullIndex >= 0)
        {
            selected = value.ValueKind == JsonValueKind.Null
                ? variants[nullIndex]
                : variants[1 - nullIndex];
            error = null;
            return true;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            selected = default;
            error = $"{path} does not match a tagged union variant.";
            return false;
        }

        var candidates = GetTagCandidates(resolved[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var variant in resolved.Skip(1))
        {
            candidates.IntersectWith(GetTagCandidates(variant));
        }
        var discriminator = candidates.First(candidate => HasUniqueStringTags(resolved, candidate));
        if (!value.TryGetProperty(discriminator, out var discriminatorValue)
            || discriminatorValue.ValueKind != JsonValueKind.String)
        {
            selected = default;
            error = $"{path} is missing the tagged union discriminator '{discriminator}'.";
            return false;
        }

        var tag = discriminatorValue.GetString();
        for (var index = 0; index < variants.Length; index++)
        {
            var constant = resolved[index].GetProperty("properties")
                .GetProperty(discriminator)
                .GetProperty("const")
                .GetString();
            if (string.Equals(tag, constant, StringComparison.Ordinal))
            {
                selected = variants[index];
                error = null;
                return true;
            }
        }

        selected = default;
        error = $"{path} has an unknown tagged union discriminator value.";
        return false;
    }

    private static bool ValidateDynamicObjectInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        if (!ValidateUnconstrainedJsonValue(value, path, state, out error)) return false;
        var maximum = schema.TryGetProperty("maxProperties", out var bound)
            ? ReadQuantitativeBound(bound)
            : long.MaxValue;
        var propertyCount = value.GetPropertyCount();
        if (propertyCount > maximum)
        {
            error = $"{path} has {propertyCount} properties; expected at most {maximum}.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidateUnconstrainedJsonValue(
        JsonElement value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        var pending = new Stack<UnconstrainedValueWork>();
        pending.Push(new UnconstrainedValueWork(value, path));
        while (pending.TryPop(out var work))
        {
            switch (work.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!ValidateObjectPropertyNames(work.Value.EnumerateObject(), work.Path, state.IsV2, out error))
                    {
                        return false;
                    }
                    foreach (var property in work.Value.EnumerateObject())
                    {
                        pending.Push(new UnconstrainedValueWork(
                            property.Value,
                            AppendPropertyPath(work.Path, property.Name)));
                    }
                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in work.Value.EnumerateArray())
                    {
                        pending.Push(new UnconstrainedValueWork(item, AppendItemPath(work.Path, index++)));
                    }
                    break;
                case JsonValueKind.String:
                    if (!TryGetScalarString(work.Value, out _))
                    {
                        error = $"{work.Path} is not scalar Unicode.";
                        return false;
                    }
                    break;
                case JsonValueKind.Number:
                    if (!TryGetFiniteDouble(work.Value, out _))
                    {
                        error = $"{work.Path} is not a finite JSON number.";
                        return false;
                    }
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    break;
                default:
                    error = $"{work.Path} is not a JSON value.";
                    return false;
            }
        }
        error = null;
        return true;
    }

    private static bool ValidateObjectInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        Stack<InstanceWork> pending,
        out string? error)
    {
        if (!ValidateObjectPropertyNames(value.EnumerateObject(), path, state.IsV2, out error))
        {
            return false;
        }
        var propertyCount = value.GetPropertyCount();
        var minimum = schema.TryGetProperty("minProperties", out var minElement)
            ? ReadQuantitativeBound(minElement)
            : 0;
        var maximum = schema.TryGetProperty("maxProperties", out var maxElement)
            ? ReadQuantitativeBound(maxElement)
            : long.MaxValue;
        if (propertyCount < minimum || propertyCount > maximum)
        {
            error = $"{path} has {propertyCount} properties outside its declared property-count bounds.";
            return false;
        }

        var additional = schema.GetProperty("additionalProperties");
        if (additional.ValueKind == JsonValueKind.False)
        {
            var declared = schema.GetProperty("properties");
            foreach (var required in schema.GetProperty("required").EnumerateArray())
            {
                if (!value.TryGetProperty(required.GetString()!, out _))
                {
                    error = $"{path} is missing required property '{required.GetString()}'.";
                    return false;
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                if (!declared.TryGetProperty(property.Name, out var propertySchema))
                {
                    error = $"{path} contains undeclared property '{property.Name}'.";
                    return false;
                }
                pending.Push(new InstanceWork(
                    propertySchema,
                    property.Value,
                    AppendPropertyPath(path, property.Name)));
            }
        }
        else
        {
            var keySchema = schema.GetProperty("propertyNames");
            foreach (var property in value.EnumerateObject())
            {
                var key = JsonSerializer.SerializeToElement(property.Name);
                pending.Push(new InstanceWork(
                    additional,
                    property.Value,
                    AppendPropertyPath(path, property.Name)));
                pending.Push(new InstanceWork(keySchema, key, AppendPropertyPath(path, "<key>")));
            }
        }
        error = null;
        return true;
    }

    private static bool ValidateArrayInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        Stack<InstanceWork> pending,
        out string? error)
    {
        var length = value.GetArrayLength();
        var minimum = schema.TryGetProperty("minItems", out var minimumItems)
            ? ReadQuantitativeBound(minimumItems)
            : 0;
        var maximum = schema.TryGetProperty("maxItems", out var maximumItems)
            ? ReadQuantitativeBound(maximumItems)
            : long.MaxValue;
        if (length < minimum || length > maximum)
        {
            error = $"{path} has {length} items outside its declared item-count bounds.";
            return false;
        }
        var items = schema.GetProperty("items");
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            pending.Push(new InstanceWork(items, item, AppendItemPath(path, index++)));
        }
        error = null;
        return true;
    }

    private static bool ValidateLogicalInstance(
        string logicalType,
        JsonElement value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        switch (logicalType)
        {
            case "content-reference":
                return ValidateContentReference(value, path, state.IsV2, out error);
            default:
                error = $"{path} uses an unsupported logical type.";
                return false;
        }
    }

    private static bool ValidateBase64Instance(
        JsonElement schema,
        string value,
        string path,
        out string? error)
    {
        var maximum = schema.TryGetProperty("maxDecodedBytes", out var maximumElement)
            ? ReadQuantitativeBound(maximumElement)
            : long.MaxValue;
        if (value.Length % 4 != 0)
        {
            error = $"{path} must be canonical padded RFC 4648 base64.";
            return false;
        }
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2
            : value.EndsWith('=') ? 1
            : 0;
        var contentLength = value.Length - padding;
        for (var index = 0; index < contentLength; index++)
        {
            if (Base64Sextet(value[index]) < 0)
            {
                error = $"{path} must be canonical padded RFC 4648 base64.";
                return false;
            }
        }
        for (var index = contentLength; index < value.Length; index++)
        {
            if (value[index] != '=')
            {
                error = $"{path} must be canonical padded RFC 4648 base64.";
                return false;
            }
        }
        if (padding == 2 && Base64Sextet(value[^3]) % 16 != 0
            || padding == 1 && Base64Sextet(value[^2]) % 4 != 0)
        {
            error = $"{path} must be canonical padded RFC 4648 base64.";
            return false;
        }

        var decodedLength = (long)value.Length / 4 * 3 - padding;
        if (decodedLength > maximum)
        {
            error = $"{path} decodes to {decodedLength} bytes; expected at most {maximum}.";
            return false;
        }
        error = null;
        return true;
    }

    private static int Base64Sextet(char value)
        => value switch
        {
            >= 'A' and <= 'Z' => value - 'A',
            >= 'a' and <= 'z' => value - 'a' + 26,
            >= '0' and <= '9' => value - '0' + 52,
            '+' => 62,
            '/' => 63,
            _ => -1,
        };

    private static bool ValidateFormattedStringInstance(
        JsonElement schema,
        string format,
        string value,
        string path,
        out string? error)
    {
        var length = value.EnumerateRunes().Count();
        var minimum = schema.TryGetProperty("minLength", out var minimumElement)
            ? ReadQuantitativeBound(minimumElement)
            : 0;
        var maximum = schema.TryGetProperty("maxLength", out var maximumElement)
            ? ReadQuantitativeBound(maximumElement)
            : long.MaxValue;
        if (length < minimum || length > maximum)
        {
            error = $"{path} has {length} characters outside its declared length bounds.";
            return false;
        }
        if (format is "big-integer" or "decimal")
        {
            if (!TryParseExactNumber(value, format == "decimal", out var exact)
                || !ExactNumberFitsSchema(schema, exact, format)
                || !ExactNumberSatisfiesBounds(schema, exact, format))
            {
                error = $"{path} is not a canonical {format} value satisfying its limits and bounds.";
                return false;
            }
        }
        else if (!IsFormattedString(format, value))
        {
            error = $"{path} does not satisfy asserted format '{format}'.";
            return false;
        }
        error = null;
        return true;
    }

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
        bool isV2,
        out string? error)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"{path} must be a content-reference object.";
            return false;
        }
        if (!ValidateObjectPropertyNames(value.EnumerateObject(), path, isV2, out error))
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
            error = $"{path} must contain exactly the seven content-reference members.";
            return false;
        }
        if (!TryGetScalarString(idElement, out var id)
            || id.Length == 0
            || id.EnumerateRunes().Count() > 256)
        {
            error = $"{path}.id must contain 1 through 256 characters.";
            return false;
        }
        if (!TryGetSafeInteger(lengthElement, out var length) || length < 0)
        {
            error = $"{path}.length must be a non-negative safe integer.";
            return false;
        }
        if (!TryGetScalarString(sha256Element, out var sha256)
            || sha256.Length != 64
            || sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            error = $"{path}.sha256 must be 64 lowercase hexadecimal characters.";
            return false;
        }
        if (!TryGetScalarString(mediaTypeElement, out var mediaType)
            || mediaType.Length is 0 or > 256
            || mediaType.Any(static character => character > 0x7f))
        {
            error = $"{path}.mediaType must contain 1 through 256 ASCII characters.";
            return false;
        }
        if (!TryGetScalarString(fileNameElement, out var fileName)
            || fileName.EnumerateRunes().Count() > 1024)
        {
            error = $"{path}.fileName may contain at most 1024 characters.";
            return false;
        }
        if (!TryGetScalarString(expiresAtElement, out var expiresAt) || !IsUtcDateTime(expiresAt))
        {
            error = $"{path}.expiresAtUtc must be a canonical UTC date-time.";
            return false;
        }
        if (!TryGetScalarString(repeatabilityElement, out var repeatability)
            || repeatability is not ("single-use" or "repeatable"))
        {
            error = $"{path}.repeatability must be 'single-use' or 'repeatable'.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidateObjectPropertyNames(
        IEnumerable<JsonProperty> properties,
        string path,
        bool isV2,
        out string? error)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitive = isV2
            ? null
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (!HasOnlyUnicodeScalars(property.Name)
                || !exact.Add(property.Name)
                || caseInsensitive is not null && !caseInsensitive.Add(property.Name))
            {
                error = $"{path} contains an invalid, duplicate, or case-colliding property '{property.Name}'.";
                return false;
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
        bool isV2,
        out string? error)
    {
        if (isV2)
        {
            var type = schema.GetProperty("type").GetString();
            if (type == "integer")
            {
                if (!TryGetSafeInteger(value, out var integer))
                {
                    error = $"{path} must be a safe mathematical integer.";
                    return false;
                }
                if (schema.TryGetProperty("minimum", out var integerMinimum) && integer < ReadSafeInteger(integerMinimum, path)
                    || schema.TryGetProperty("exclusiveMinimum", out var integerExclusiveMinimum) && integer <= ReadSafeInteger(integerExclusiveMinimum, path)
                    || schema.TryGetProperty("maximum", out var integerMaximum) && integer > ReadSafeInteger(integerMaximum, path)
                    || schema.TryGetProperty("exclusiveMaximum", out var integerExclusiveMaximum) && integer >= ReadSafeInteger(integerExclusiveMaximum, path))
                {
                    error = $"{path} is outside its integer bounds.";
                    return false;
                }
            }
            else
            {
                if (!TryGetFormattedFloat(schema, value, out var formattedNumber))
                {
                    error = $"{path} is not a finite {schema.GetProperty("format").GetString()} value.";
                    return false;
                }
                if (schema.TryGetProperty("minimum", out var floatMinimum) && formattedNumber < ReadFormattedFloat(schema, floatMinimum, path)
                    || schema.TryGetProperty("exclusiveMinimum", out var floatExclusiveMinimum) && formattedNumber <= ReadFormattedFloat(schema, floatExclusiveMinimum, path)
                    || schema.TryGetProperty("maximum", out var floatMaximum) && formattedNumber > ReadFormattedFloat(schema, floatMaximum, path)
                    || schema.TryGetProperty("exclusiveMaximum", out var floatExclusiveMaximum) && formattedNumber >= ReadFormattedFloat(schema, floatExclusiveMaximum, path))
                {
                    error = $"{path} is outside its floating-point bounds.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        if (!decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            error = $"{path} is outside the supported RPC numeric range.";
            return false;
        }
        if (schema.GetProperty("type").GetString() == "integer" && decimal.Truncate(number) != number)
        {
            error = $"{path} must be an integer.";
            return false;
        }
        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal()
            || schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && number <= exclusiveMinimum.GetDecimal()
            || schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal()
            || schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && number >= exclusiveMaximum.GetDecimal())
        {
            error = $"{path} is outside its numeric bounds.";
            return false;
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
