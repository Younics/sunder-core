using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sunder.Package.Format;

// Ordinary V1 value schema. Identity grammar and current binding/visibility are checked
// by the receiver's provider APIs; the wire value itself confers no authority.
internal static class SunderRpcServiceReferenceSchema
{
    internal static JsonObject Create()
    {
        var properties = new JsonObject
        {
            ["packageId"] = Text(1, 128), ["packageVersion"] = Text(5, 256),
            ["providerId"] = Text(1, 128), ["contractId"] = Text(1, 128), ["contractVersion"] = Text(5, 256),
            ["descriptorSha256"] = Text(64, 64), ["abiSha256"] = Text(64, 64),
            ["activationId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["activationEpoch"] = SafeInteger(), ["sessionGeneration"] = SafeInteger(), ["endpoint"] = Text(1, 256),
        };
        return new()
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    internal static void Validate(JsonElement schema)
    {
        var expected = Create();
        var actual = JsonNode.Parse(schema.GetRawText())!.AsObject();
        StripAnnotations(actual);
        // Descriptor canonical order differs from source order; required is a set.
        if (actual["required"] is JsonArray required)
            actual["required"] = new JsonArray(required.Select(v => v!.GetValue<string>()).Order(StringComparer.Ordinal)
                .Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        expected["required"] = new JsonArray(expected["required"]!.AsArray().Select(v => v!.GetValue<string>()).Order(StringComparer.Ordinal)
            .Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        if (!JsonNode.DeepEquals(expected, actual))
            throw new InvalidDataException("Service-reference projection requires the complete closed eleven-field provider locator schema, including activationEpoch and sessionGeneration.");
    }

    private static JsonObject Text(int minimum, int maximum) => new() { ["type"] = "string", ["minLength"] = minimum, ["maxLength"] = maximum };
    private static JsonObject SafeInteger() => new() { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 9_007_199_254_740_991L };

    private static void StripAnnotations(JsonObject schema)
    {
        schema.Remove("title"); schema.Remove("description");
        if (schema["properties"] is JsonObject properties)
            foreach (var property in properties) if (property.Value is JsonObject child) StripAnnotations(child);
    }
}
