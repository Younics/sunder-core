using System.Buffers;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

// Provider payloads are validated in full before this reader runs. Unknown fields
// are ignored only on ordinary closed records, never on maps or protocol values.
internal static class SunderRpcConsumerOutput
{
    internal static JsonElement Read(SunderRpcContractDescriptor source, string reference, JsonElement value,
        IReadOnlyList<string>? exactDefinitions = null)
    {
        var steps = 0;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, source.Definitions[reference[8..]], value, 0, reference[8..]);
        using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = 128 });
        var result = document.RootElement.Clone();
        if (!source.IsValid(reference, result, out var error))
            throw new InvalidDataException("Provider output does not satisfy the consumer schema: " + error);
        return result;

        void Write(Utf8JsonWriter writer, JsonElement schema, JsonElement item, int depth, string? definition = null)
        {
            if (++steps > 65_536 || depth > 128) throw new InvalidDataException("Consumer output exceeds the projection budget.");
            if (schema.TryGetProperty("logicalType", out _) || definition is not null && exactDefinitions?.Contains(definition, StringComparer.Ordinal) == true)
            { item.WriteTo(writer); return; }
            if (schema.TryGetProperty("$ref", out var target))
            { var name = target.GetString()![8..]; Write(writer, source.Definitions[name], item, depth + 1, name); return; }
            if (schema.TryGetProperty("oneOf", out var variants))
            {
                // Nullable branches and tagged unions have disjoint type/tag constraints.
                var matches = variants.EnumerateArray().Where(variant => Selects(variant, item)).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Consumer output has no unique union branch.");
                Write(writer, matches[0], item, depth + 1); return;
            }
            if (item.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties)
                && schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
            {
                writer.WriteStartObject();
                foreach (var property in item.EnumerateObject())
                    if (properties.TryGetProperty(property.Name, out var member))
                    { writer.WritePropertyName(property.Name); Write(writer, member, property.Value, depth + 1); }
                writer.WriteEndObject(); return;
            }
            if (item.ValueKind == JsonValueKind.Object && schema.TryGetProperty("additionalProperties", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in item.EnumerateObject())
                { writer.WritePropertyName(property.Name); Write(writer, values, property.Value, depth + 1); }
                writer.WriteEndObject(); return;
            }
            if (item.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var elements))
            {
                writer.WriteStartArray(); foreach (var element in item.EnumerateArray()) Write(writer, elements, element, depth + 1);
                writer.WriteEndArray(); return;
            }
            item.WriteTo(writer);
        }

        bool Selects(JsonElement schema, JsonElement item)
        {
            if (++steps > 65_536) throw new InvalidDataException("Consumer output exceeds the projection budget.");
            if (schema.TryGetProperty("$ref", out var target)) return Selects(source.Definitions[target.GetString()![8..]], item);
            if (schema.TryGetProperty("type", out var type))
            {
                var valid = type.GetString() switch
                {
                    "null" => item.ValueKind == JsonValueKind.Null, "object" => item.ValueKind == JsonValueKind.Object,
                    "array" => item.ValueKind == JsonValueKind.Array, "string" => item.ValueKind == JsonValueKind.String,
                    "number" or "integer" => item.ValueKind == JsonValueKind.Number,
                    "boolean" => item.ValueKind is JsonValueKind.True or JsonValueKind.False, _ => false,
                };
                if (!valid) return false;
            }
            if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, item)) return false;
            if (item.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (property.Value.TryGetProperty("const", out var tag)
                        && (!item.TryGetProperty(property.Name, out var actual) || !JsonElement.DeepEquals(actual, tag))) return false;
            return true;
        }
    }
}
