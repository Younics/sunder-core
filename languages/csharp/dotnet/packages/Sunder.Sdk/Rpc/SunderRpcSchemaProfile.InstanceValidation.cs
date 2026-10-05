using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcSchemaProfile
{
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

        if (schema.EnumerateObject().All(static property =>
                property.Name is "title" or "description"))
        {
            return ValidateUnconstrainedJsonValue(value, path, state, out error);
        }

        var type = schema.GetProperty("type").GetString()!;
        if (schema.TryGetProperty("logicalType", out var logicalType))
        {
            return ValidateLogicalInstance(logicalType.GetString()!, value, path, state, out error);
        }
        if (!MatchesType(value, type))
        {
            return state.Reject("type-mismatch", path, $"{path} must be of type '{type}'.", out error);
        }
        string? scalarText = null;
        if (type == "string" && !TryGetScalarString(value, out scalarText))
        {
            return state.Reject("invalid-unicode", path, $"{path} must contain Unicode scalar values.", out error);
        }
        if (schema.TryGetProperty("const", out var constant)
            && !LiteralEquals(value, constant, schema))
        {
            return state.Reject("const-mismatch", path, $"{path} does not match its const value.", out error);
        }
        if (schema.TryGetProperty("enum", out var enumElement)
            && !enumElement.EnumerateArray().Any(item => LiteralEquals(value, item, schema)))
        {
            return state.Reject("enum-mismatch", path, $"{path} is not an allowed enum value.", out error);
        }

        switch (type)
        {
            case "object":
                if (!schema.TryGetProperty("additionalProperties", out _))
                {
                    return ValidateDynamicObjectInstance(schema, value, path, state, out error);
                }
                return ValidateObjectInstance(schema, value, path, state, pending, out error);
            case "array":
                return ValidateArrayInstance(schema, value, path, state, pending, out error);
            case "string":
                if (schema.TryGetProperty("contentEncoding", out _))
                {
                    return ValidateBase64Instance(schema, scalarText!, path, state, out error);
                }
                if (schema.TryGetProperty("format", out var format))
                {
                    if (!ValidateFormattedStringInstance(schema, format.GetString()!, scalarText!, path, state, out error))
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
                    return state.Reject(
                        "string-length",
                        path,
                        $"{path} has {length} characters outside its declared length bounds.",
                        out error);
                }
                break;
            case "integer":
            case "number":
                if (!ValidateNumberInstance(schema, value, path, state, out error)) return false;
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
            return state.Reject(
                "union-mismatch",
                path,
                $"{path} does not match a tagged union variant.",
                out error);
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
            return state.Reject(
                "union-mismatch",
                path,
                $"{path} is missing the tagged union discriminator '{discriminator}'.",
                out error);
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
        return state.Reject(
            "union-mismatch",
            path,
            $"{path} has an unknown tagged union discriminator value.",
            out error);
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
            return state.Reject(
                "object-size",
                path,
                $"{path} has {propertyCount} properties; expected at most {maximum}.",
                out error);
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
                    if (!ValidateObjectPropertyNames(work.Value.EnumerateObject(), work.Path, state, out error))
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
                        return state.Reject(
                            "invalid-unicode",
                            work.Path,
                            $"{work.Path} is not scalar Unicode.",
                            out error);
                    }
                    break;
                case JsonValueKind.Number:
                    if (!TryGetFiniteDouble(work.Value, out _))
                    {
                        return state.Reject(
                            "number-invalid",
                            work.Path,
                            $"{work.Path} is not a finite JSON number.",
                            out error);
                    }
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    break;
                default:
                    return state.Reject(
                        "schema-invalid",
                        work.Path,
                        $"{work.Path} is not a JSON value.",
                        out error);
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
        if (!ValidateObjectPropertyNames(value.EnumerateObject(), path, state, out error))
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
            return state.Reject(
                "object-size",
                path,
                $"{path} has {propertyCount} properties outside its declared property-count bounds.",
                out error);
        }

        var additional = schema.GetProperty("additionalProperties");
        if (additional.ValueKind == JsonValueKind.False)
        {
            var declared = schema.GetProperty("properties");
            foreach (var required in schema.GetProperty("required").EnumerateArray())
            {
                if (!value.TryGetProperty(required.GetString()!, out _))
                {
                    return state.Reject(
                        "required-property",
                        path,
                        $"{path} is missing required property '{required.GetString()}'.",
                        out error);
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                if (!declared.TryGetProperty(property.Name, out var propertySchema))
                {
                    return state.Reject(
                        "undeclared-property",
                        path,
                        $"{path} contains undeclared property '{property.Name}'.",
                        out error);
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
        InstanceValidationState state,
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
            return state.Reject(
                "array-size",
                path,
                $"{path} has {length} items outside its declared item-count bounds.",
                out error);
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
                return ValidateContentReference(value, path, state, out error);
            default:
                return state.Reject(
                    "schema-invalid",
                    path,
                    $"{path} uses an unsupported logical type.",
                    out error);
        }
    }

    private static bool ValidateBase64Instance(
        JsonElement schema,
        string value,
        string path,
        InstanceValidationState state,
        out string? error)
    {
        var maximum = schema.TryGetProperty("maxDecodedBytes", out var maximumElement)
            ? ReadQuantitativeBound(maximumElement)
            : long.MaxValue;
        if (value.Length % 4 != 0)
        {
            return state.Reject(
                "base64-invalid",
                path,
                $"{path} must be canonical padded RFC 4648 base64.",
                out error);
        }
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2
            : value.EndsWith('=') ? 1
            : 0;
        var contentLength = value.Length - padding;
        for (var index = 0; index < contentLength; index++)
        {
            if (Base64Sextet(value[index]) < 0)
            {
                return state.Reject(
                    "base64-invalid",
                    path,
                    $"{path} must be canonical padded RFC 4648 base64.",
                    out error);
            }
        }
        for (var index = contentLength; index < value.Length; index++)
        {
            if (value[index] != '=')
            {
                return state.Reject(
                    "base64-invalid",
                    path,
                    $"{path} must be canonical padded RFC 4648 base64.",
                    out error);
            }
        }
        if (padding == 2 && Base64Sextet(value[^3]) % 16 != 0
            || padding == 1 && Base64Sextet(value[^2]) % 4 != 0)
        {
            return state.Reject(
                "base64-invalid",
                path,
                $"{path} must be canonical padded RFC 4648 base64.",
                out error);
        }

        var decodedLength = (long)value.Length / 4 * 3 - padding;
        if (decodedLength > maximum)
        {
            return state.Reject(
                "base64-invalid",
                path,
                $"{path} decodes to {decodedLength} bytes; expected at most {maximum}.",
                out error);
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
        InstanceValidationState state,
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
            return state.Reject(
                "string-length",
                path,
                $"{path} has {length} characters outside its declared length bounds.",
                out error);
        }
        if (format is "big-integer" or "decimal")
        {
            if (!TryParseExactNumber(value, format == "decimal", out var exact)
                || !ExactNumberFitsSchema(schema, exact, format)
                || !ExactNumberSatisfiesBounds(schema, exact, format))
            {
                return state.Reject(
                    "format-invalid",
                    path,
                    $"{path} is not a canonical {format} value satisfying its limits and bounds.",
                    out error);
            }
        }
        else if (!IsFormattedString(format, value))
        {
            return state.Reject(
                "format-invalid",
                path,
                $"{path} does not satisfy asserted format '{format}'.",
                out error);
        }
        error = null;
        return true;
    }
}
