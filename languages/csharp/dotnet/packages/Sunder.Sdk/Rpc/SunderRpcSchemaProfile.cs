using System.Globalization;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static class SunderRpcSchemaProfile
{
    private const int MaximumSchemaDepth = 64;
    private const int MaximumSchemaNodes = 4096;
    private const int MaximumProperties = 256;
    private const int MaximumCollectionItems = 1_000_000;
    private const int MaximumStringCharacters = 1_000_000;
    private const int MaximumUnionVariants = 16;
    private const int MaximumEnumValues = 256;
    private static readonly string[] AnnotationProperties = ["title", "description"];

    public static void ValidateDefinitions(
        IReadOnlyDictionary<string, JsonElement> definitions,
        int descriptorVersion)
    {
        var isV2 = descriptorVersion == SunderRpcProtocol.DescriptorVersionV2;
        var validated = new HashSet<string>(StringComparer.Ordinal);
        var nodes = 0;
        foreach (var definition in definitions)
        {
            if (validated.Contains(definition.Key)) continue;
            ValidateSchema(
                definition.Value,
                "$.$defs." + definition.Key,
                definitions,
                new HashSet<string>(StringComparer.Ordinal) { definition.Key },
                validated,
                ref nodes,
                depth: 1,
                isV2);
            validated.Add(definition.Key);
        }

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
        return ValidateInstance(schema, value, "$", state, 1, out error);
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
                SunderRpcDescriptorParser.RequireOnlyProperties(schema, path, "$ref");
            }
            else
            {
                SunderRpcDescriptorParser.RequireOnlyProperties(schema, path, "$ref", "title", "description");
            }
            if (referenceElement.ValueKind != JsonValueKind.String
                || !SunderRpcDescriptorParser.TryGetReferenceName(referenceElement.GetString(), out var referenceName)
                || !definitions.TryGetValue(referenceName, out var referencedSchema))
            {
                throw Error($"{path} contains a remote, malformed, or missing $ref.");
            }
            if (!referenceStack.Add(referenceName))
            {
                throw Error($"{path} contains a recursive $ref cycle, which is outside the bounded RPC profile.");
            }
            if (!validatedDefinitions.Contains(referenceName))
            {
                ValidateSchema(
                    referencedSchema,
                    "$.$defs." + referenceName,
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth + 1,
                    isV2);
                validatedDefinitions.Add(referenceName);
            }
            referenceStack.Remove(referenceName);
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
                ValidateObjectSchema(
                    schema,
                    path,
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth,
                    isV2);
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
                ValidateStringSchema(schema, path);
                break;
            case "integer":
            case "number":
                ValidateNumberSchema(schema, path, type);
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
            ValidateOptionalPropertyBounds(schema, path, propertyCount);
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
            var minimum = RequiredBound(schema, "minProperties", path, 0, MaximumCollectionItems);
            var maximum = RequiredBound(schema, "maxProperties", path, 0, MaximumCollectionItems);
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
                || !propertyNames.TryGetProperty("minLength", out _)
                || !propertyNames.TryGetProperty("maxLength", out _))
            {
                throw Error($"{path}.propertyNames must be a bounded string schema.");
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
        var minimum = RequiredBound(schema, "minItems", path, 0, MaximumCollectionItems);
        var maximum = RequiredBound(schema, "maxItems", path, 0, MaximumCollectionItems);
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
        var minimum = RequiredBound(schema, "minLength", path, 0, MaximumStringCharacters);
        var maximum = RequiredBound(schema, "maxLength", path, 0, MaximumStringCharacters);
        if (minimum > maximum)
        {
            throw Error($"{path}.minLength must not exceed maxLength.");
        }
    }

    private static void ValidateNumberSchema(JsonElement schema, string path, string type)
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
        var lowerCount = (schema.TryGetProperty("minimum", out _) ? 1 : 0)
                         + (schema.TryGetProperty("exclusiveMinimum", out _) ? 1 : 0);
        var upperCount = (schema.TryGetProperty("maximum", out _) ? 1 : 0)
                         + (schema.TryGetProperty("exclusiveMaximum", out _) ? 1 : 0);
        if (lowerCount != 1 || upperCount != 1)
        {
            throw Error($"{path} must declare exactly one lower and one upper numeric bound.");
        }
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
        var valid = logicalType switch
        {
            "uuid" or "utc-date-time" => type == "string",
            "content-reference" => type == "object",
            _ => false,
        };
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
        var heights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in definitions.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {
            var height = GetDefinitionHeight(
                name,
                definitions,
                heights,
                new HashSet<string>(StringComparer.Ordinal));
            if (height > MaximumSchemaDepth)
            {
                throw Error($"$.$defs.{name} exceeds the resolved RPC schema depth limit of {MaximumSchemaDepth}.");
            }
        }
    }

    private static int GetDefinitionHeight(
        string name,
        IReadOnlyDictionary<string, JsonElement> definitions,
        Dictionary<string, int> heights,
        HashSet<string> referenceStack)
    {
        if (heights.TryGetValue(name, out var height))
        {
            return height;
        }
        if (!referenceStack.Add(name))
        {
            throw Error($"$.$defs.{name} contains a recursive $ref cycle, which is outside the bounded RPC profile.");
        }

        height = GetSchemaHeight(definitions[name], definitions, heights, referenceStack);
        referenceStack.Remove(name);
        heights.Add(name, height);
        return height;
    }

    private static int GetSchemaHeight(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions,
        Dictionary<string, int> heights,
        HashSet<string> referenceStack)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            return 1 + GetDefinitionHeight(name, definitions, heights, referenceStack);
        }

        var maximumChildHeight = 0;
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            foreach (var variant in oneOf.EnumerateArray())
            {
                maximumChildHeight = Math.Max(
                    maximumChildHeight,
                    GetSchemaHeight(variant, definitions, heights, referenceStack));
            }
        }
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                maximumChildHeight = Math.Max(
                    maximumChildHeight,
                    GetSchemaHeight(property.Value, definitions, heights, referenceStack));
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            maximumChildHeight = Math.Max(
                maximumChildHeight,
                GetSchemaHeight(items, definitions, heights, referenceStack));
        }
        if (schema.TryGetProperty("propertyNames", out var propertyNames))
        {
            maximumChildHeight = Math.Max(
                maximumChildHeight,
                GetSchemaHeight(propertyNames, definitions, heights, referenceStack));
        }
        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.Object)
        {
            maximumChildHeight = Math.Max(
                maximumChildHeight,
                GetSchemaHeight(additional, definitions, heights, referenceStack));
        }
        return 1 + maximumChildHeight;
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
                ValidateLiteralType(item, type, path + ".enum");
                if (!values.Add(isV2 ? GetPrimitiveLiteralKey(item) : item.GetRawText()))
                {
                    throw Error($"{path}.enum contains a duplicate value.");
                }
            }
        }
        if (hasConst)
        {
            ValidateLiteralType(constElement, type, path + ".const");
        }
    }

    private static string GetPrimitiveLiteralKey(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => "s:" + value.GetString(),
            JsonValueKind.Number => "n:" + value.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "b:true",
            JsonValueKind.False => "b:false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText(),
        };

    private static void ValidateLiteralType(JsonElement value, string type, string path)
    {
        var valid = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
        if (!valid)
        {
            throw Error($"{path} contains a value incompatible with schema type '{type}'.");
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

    private static void ValidateOptionalPropertyBounds(JsonElement schema, string path, int propertyCount)
    {
        var minimum = schema.TryGetProperty("minProperties", out _)
            ? RequiredBound(schema, "minProperties", path, 0, MaximumProperties)
            : 0;
        var maximum = schema.TryGetProperty("maxProperties", out _)
            ? RequiredBound(schema, "maxProperties", path, 0, MaximumProperties)
            : propertyCount;
        if (minimum > maximum || maximum > propertyCount)
        {
            throw Error($"{path}'s object property bounds are inconsistent with its closed properties.");
        }
    }

    private static int RequiredBound(JsonElement schema, string name, string path, int minimum, int maximum)
    {
        var value = SunderRpcDescriptorParser.Required(schema, name, path, JsonValueKind.Number);
        if (!value.TryGetInt32(out var result) || result < minimum || result > maximum)
        {
            throw Error($"{path}.{name} must be an integer from {minimum} through {maximum}.");
        }
        return result;
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

    private static SunderRpcDescriptorException Error(string message)
        => SunderRpcDescriptorParser.Error(message);

    private static bool ValidateInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        int depth,
        out string? error)
    {
        if (depth > MaximumSchemaDepth)
        {
            error = $"{path} exceeds the RPC payload depth limit.";
            return false;
        }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            state.Descriptor.TryGetDefinition(name, out var target);
            return ValidateInstance(target, value, path, state, depth + 1, out error);
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = 0;
            string? lastError = null;
            foreach (var variant in oneOf.EnumerateArray())
            {
                if (ValidateInstance(variant, value, path, state, depth + 1, out var branchError))
                {
                    matches++;
                }
                else
                {
                    lastError = branchError;
                }
            }
            if (matches != 1)
            {
                error = matches == 0 ? lastError ?? $"{path} does not match a union variant." : $"{path} matches multiple union variants.";
                return false;
            }
            error = null;
            return true;
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
        if (schema.TryGetProperty("const", out var constant) && !LiteralEquals(value, constant, state.IsV2))
        {
            error = $"{path} does not match its const value.";
            return false;
        }
        if (schema.TryGetProperty("enum", out var enumElement)
            && !enumElement.EnumerateArray().Any(item => LiteralEquals(value, item, state.IsV2)))
        {
            error = $"{path} is not an allowed enum value.";
            return false;
        }

        switch (type)
        {
            case "object":
                return ValidateObjectInstance(schema, value, path, state, depth, out error);
            case "array":
                return ValidateArrayInstance(schema, value, path, state, depth, out error);
            case "string":
                var length = scalarText!.EnumerateRunes().Count();
                var minLength = schema.GetProperty("minLength").GetInt32();
                var maxLength = schema.GetProperty("maxLength").GetInt32();
                if (length < minLength || length > maxLength)
                {
                    error = $"{path} has {length} characters; expected {minLength} through {maxLength}.";
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

    private static bool ValidateObjectInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        int depth,
        out string? error)
    {
        var properties = value.EnumerateObject().ToArray();
        if (!ValidateObjectPropertyNames(properties, path, state.IsV2, out error))
        {
            return false;
        }
        var minimum = schema.TryGetProperty("minProperties", out var minElement) ? minElement.GetInt32() : 0;
        var maximum = schema.TryGetProperty("maxProperties", out var maxElement)
            ? maxElement.GetInt32()
            : schema.TryGetProperty("properties", out var declaredProperties) ? declaredProperties.GetPropertyCount() : int.MaxValue;
        if (properties.Length < minimum || properties.Length > maximum)
        {
            error = $"{path} has {properties.Length} properties; expected {minimum} through {maximum}.";
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
            foreach (var property in properties)
            {
                if (!declared.TryGetProperty(property.Name, out var propertySchema))
                {
                    error = $"{path} contains undeclared property '{property.Name}'.";
                    return false;
                }
                if (!ValidateInstance(propertySchema, property.Value, path + "." + property.Name, state, depth + 1, out error))
                {
                    return false;
                }
            }
        }
        else
        {
            var keySchema = schema.GetProperty("propertyNames");
            foreach (var property in properties)
            {
                var key = JsonSerializer.SerializeToElement(property.Name);
                if (!ValidateInstance(keySchema, key, path + ".<key>", state, depth + 1, out error)
                    || !ValidateInstance(additional, property.Value, path + "." + property.Name, state, depth + 1, out error))
                {
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    private static bool ValidateArrayInstance(
        JsonElement schema,
        JsonElement value,
        string path,
        InstanceValidationState state,
        int depth,
        out string? error)
    {
        var length = value.GetArrayLength();
        var minimum = schema.GetProperty("minItems").GetInt32();
        var maximum = schema.GetProperty("maxItems").GetInt32();
        if (length < minimum || length > maximum)
        {
            error = $"{path} has {length} items; expected {minimum} through {maximum}.";
            return false;
        }
        var items = schema.GetProperty("items");
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!ValidateInstance(items, item, path + "[" + index++ + "]", state, depth + 1, out error))
            {
                return false;
            }
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
            case "uuid":
                if (TryGetScalarString(value, out var uuid) && IsUuid(uuid))
                {
                    error = null;
                    return true;
                }
                error = $"{path} must be a canonical lowercase UUID.";
                return false;
            case "utc-date-time":
                if (TryGetScalarString(value, out var instant) && IsUtcDateTime(instant))
                {
                    error = null;
                    return true;
                }
                error = $"{path} must be a canonical UTC date-time.";
                return false;
            case "content-reference":
                return ValidateContentReference(value, path, state.IsV2, out error);
            default:
                error = $"{path} uses an unsupported logical type.";
                return false;
        }
    }

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
        var properties = value.EnumerateObject().ToArray();
        if (!ValidateObjectPropertyNames(properties, path, isV2, out error))
        {
            return false;
        }
        if (properties.Length != 7
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
        IReadOnlyList<JsonProperty> properties,
        string path,
        bool isV2,
        out string? error)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(
            isV2 ? AsciiCaseInsensitiveComparer.Instance : StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (!HasOnlyUnicodeScalars(property.Name)
                || !exact.Add(property.Name)
                || !caseInsensitive.Add(property.Name))
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
            if (!TryGetFiniteDouble(value, out var binary64))
            {
                error = $"{path} is not a finite IEEE-754 binary64 value.";
                return false;
            }
            if (schema.GetProperty("type").GetString() == "integer"
                && !TryGetSafeInteger(value, out _))
            {
                error = $"{path} must be a safe mathematical integer.";
                return false;
            }
            if (schema.TryGetProperty("minimum", out var binaryMinimum) && binary64 < binaryMinimum.GetDouble()
                || schema.TryGetProperty("exclusiveMinimum", out var binaryExclusiveMinimum) && binary64 <= binaryExclusiveMinimum.GetDouble()
                || schema.TryGetProperty("maximum", out var binaryMaximum) && binary64 > binaryMaximum.GetDouble()
                || schema.TryGetProperty("exclusiveMaximum", out var binaryExclusiveMaximum) && binary64 >= binaryExclusiveMaximum.GetDouble())
            {
                error = $"{path} is outside its numeric bounds.";
                return false;
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

    private static bool LiteralEquals(JsonElement left, JsonElement right, bool isV2)
    {
        if (isV2
            && left.ValueKind == JsonValueKind.Number
            && right.ValueKind == JsonValueKind.Number
            && TryGetFiniteDouble(left, out var leftBinary64)
            && TryGetFiniteDouble(right, out var rightBinary64))
        {
            return leftBinary64 == rightBinary64;
        }
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number
            && decimal.TryParse(left.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber)
            && decimal.TryParse(right.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }
        return JsonElement.DeepEquals(left, right);
    }

    private sealed class AsciiCaseInsensitiveComparer : IEqualityComparer<string>
    {
        public static AsciiCaseInsensitiveComparer Instance { get; } = new();

        public bool Equals(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null || left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (Fold(left[index]) != Fold(right[index])) return false;
            }
            return true;
        }

        public int GetHashCode(string value)
        {
            var hash = new HashCode();
            foreach (var character in value) hash.Add(Fold(character));
            return hash.ToHashCode();
        }

        private static char Fold(char value)
            => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
    }

    private sealed class InstanceValidationState(SunderRpcContractDescriptor descriptor)
    {
        public SunderRpcContractDescriptor Descriptor { get; } = descriptor;
        public bool IsV2 { get; } = descriptor.DescriptorVersion == SunderRpcProtocol.DescriptorVersionV2;
    }
}
