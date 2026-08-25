using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcSchemaProfile
{
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
}
