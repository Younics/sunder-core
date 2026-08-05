using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sunder.Package.Format;

internal static class StrictJsonSerializer
{
    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        RejectDuplicateProperties(json);
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    private static void RejectDuplicateProperties(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
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
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var properties = objects.Peek()
                                     ?? throw new JsonException("A JSON property appeared outside an object.");
                    var propertyName = reader.GetString()!;
                    if (!properties.Add(propertyName))
                    {
                        throw new JsonException($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }
                    break;
            }
        }
    }
}
