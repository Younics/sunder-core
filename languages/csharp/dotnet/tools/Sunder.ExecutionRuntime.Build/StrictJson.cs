using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.ExecutionRuntime.Build;

internal static class StrictJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 64,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes, string label)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            throw new InvalidDataException($"{label} must be UTF-8 without a byte-order mark.");
        }

        RejectDuplicateProperties(bytes, label);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options)
                   ?? throw new InvalidDataException($"{label} must contain one JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is not strict JSON: {exception.Message}", exception);
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes, string label)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            var objects = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        objects.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (objects.Count == 0)
                        {
                            throw new JsonException("A JSON container ended without a matching start token.");
                        }
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var properties = objects.Count > 0 ? objects.Peek() : null;
                        properties = properties
                                         ?? throw new JsonException("A JSON property appeared outside an object.");
                        var property = reader.GetString()!;
                        if (!properties.Add(property))
                        {
                            throw new JsonException($"Duplicate JSON property '{property}' is not allowed.");
                        }
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is not strict JSON: {exception.Message}", exception);
        }
    }
}
