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

    public static void ValidateDefinitions(IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var validated = new HashSet<string>(StringComparer.Ordinal);
        var nodes = 0;
        foreach (var definition in definitions)
        {
            ValidateSchema(
                definition.Value,
                "$.$defs." + definition.Key,
                definitions,
                new HashSet<string>(StringComparer.Ordinal) { definition.Key },
                validated,
                ref nodes,
                depth: 1);
            validated.Add(definition.Key);
        }
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
        int depth)
    {
        if (depth > MaximumSchemaDepth || ++nodes > MaximumSchemaNodes)
        {
            throw Error($"{path} exceeds the RPC schema depth or node-count limit.");
        }
        RequireObject(schema, path);
        ValidateAnnotations(schema, path);

        if (schema.TryGetProperty("$ref", out var referenceElement))
        {
            SunderRpcDescriptorParser.RequireOnlyProperties(schema, path, "$ref", "title", "description");
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
                    depth + 1);
                validatedDefinitions.Add(referenceName);
            }
            referenceStack.Remove(referenceName);
            return;
        }

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            SunderRpcDescriptorParser.RequireOnlyProperties(schema, path, "oneOf", "title", "description");
            if (IsNullableUnion(oneOf, definitions))
            {
                ValidateNullableUnion(
                    oneOf,
                    path + ".oneOf",
                    definitions,
                    referenceStack,
                    validatedDefinitions,
                    ref nodes,
                    depth + 1);
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
                    depth + 1);
            }
            return;
        }

        var typeElement = SunderRpcDescriptorParser.Required(schema, "type", path, JsonValueKind.String);
        var type = typeElement.GetString();
        switch (type)
        {
            case "object":
                ValidateObjectSchema(schema, path, definitions, referenceStack, validatedDefinitions, ref nodes, depth);
                break;
            case "array":
                ValidateArraySchema(schema, path, definitions, referenceStack, validatedDefinitions, ref nodes, depth);
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
                if (property.Name.Length is 0 or > 128)
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
                depth + 1);
            if (!propertyNames.TryGetProperty("type", out var keyType)
                || keyType.GetString() != "string")
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
            "enum",
            "const",
            "title",
            "description");
        var minimum = RequiredBound(schema, "minItems", path, 0, MaximumCollectionItems);
        var maximum = RequiredBound(schema, "maxItems", path, 0, MaximumCollectionItems);
        if (minimum > maximum)
        {
            throw Error($"{path}.minItems must not exceed maxItems.");
        }
        var items = SunderRpcDescriptorParser.Required(schema, "items", path, JsonValueKind.Object);
        ValidateSchema(items, path + ".items", definitions, referenceStack, validatedDefinitions, ref nodes, depth + 1);
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

    private static void ValidateTaggedUnion(
        JsonElement oneOf,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth)
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
                depth + 1);
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
        IReadOnlyDictionary<string, JsonElement> definitions)
        => oneOf.ValueKind == JsonValueKind.Array
           && oneOf.GetArrayLength() == 2
           && oneOf.EnumerateArray().Count(variant => IsNullSchema(ResolveSchema(variant, definitions))) == 1;

    private static void ValidateNullableUnion(
        JsonElement oneOf,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        HashSet<string> referenceStack,
        HashSet<string> validatedDefinitions,
        ref int nodes,
        int depth)
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
                depth + 1);
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
        while (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            schema = definitions[name];
        }
        return schema;
    }

    private static bool IsClosedObjectSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type)
           && type.GetString() == "object"
           && schema.TryGetProperty("additionalProperties", out var additional)
           && additional.ValueKind == JsonValueKind.False;

    private static void ValidateEnumAndConst(JsonElement schema, string path, string type)
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
                if (!values.Add(item.GetRawText()))
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
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 2048)
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
        if (depth > MaximumSchemaDepth || ++state.Nodes > 100_000)
        {
            error = $"{path} exceeds the RPC payload depth or value-count limit.";
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
                var branchState = state.Fork();
                if (ValidateInstance(variant, value, path, branchState, depth + 1, out var branchError))
                {
                    matches++;
                    state.Nodes = Math.Max(state.Nodes, branchState.Nodes);
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
        if (!MatchesType(value, type))
        {
            error = $"{path} must be of type '{type}'.";
            return false;
        }
        if (schema.TryGetProperty("const", out var constant) && !LiteralEquals(value, constant))
        {
            error = $"{path} does not match its const value.";
            return false;
        }
        if (schema.TryGetProperty("enum", out var enumElement)
            && !enumElement.EnumerateArray().Any(item => LiteralEquals(value, item)))
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
                var length = value.GetString()!.EnumerateRunes().Count();
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
                if (!ValidateNumberInstance(schema, value, path, out error)) return false;
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
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (!exact.Add(property.Name) || !caseInsensitive.Add(property.Name))
            {
                error = $"{path} contains duplicate or case-colliding property '{property.Name}'.";
                return false;
            }
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

    private static bool ValidateNumberInstance(JsonElement schema, JsonElement value, string path, out string? error)
    {
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

    private static bool MatchesType(JsonElement value, string type)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number
                         && decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var integer)
                         && decimal.Truncate(integer) == integer,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };

    private static bool LiteralEquals(JsonElement left, JsonElement right)
    {
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
        public int Nodes { get; set; }

        public InstanceValidationState Fork() => new(Descriptor) { Nodes = Nodes };
    }
}
