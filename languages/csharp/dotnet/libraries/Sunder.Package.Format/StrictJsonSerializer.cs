using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sunder.Package.Format;

internal static class StrictJsonSerializer
{
    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        RejectInvalidProperties(Encoding.UTF8.GetBytes(json), typeof(T) == typeof(SunderPackageManifest));
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    public static T? Deserialize<T>(string json, JsonSerializerOptions options)
        => Deserialize<T>(Encoding.UTF8.GetBytes(json), options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> json, JsonSerializerOptions options)
    {
        RejectInvalidProperties(json, typeof(T) == typeof(SunderPackageManifest));
        return JsonSerializer.Deserialize<T>(json, options);
    }

    internal static JsonDocument ParseDocument(string json)
    {
        RejectInvalidProperties(Encoding.UTF8.GetBytes(json));
        return JsonDocument.Parse(json);
    }

    private static void RejectInvalidProperties(ReadOnlySpan<byte> json, bool isPackageManifest = false)
    {
        var reader = new Utf8JsonReader(json);
        var objects = new Stack<HashSet<string>?>();
        string? rootProperty = null;
        string? propertyName = null;
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
                    propertyName = reader.GetString()!;
                    if (!properties.Add(propertyName))
                    {
                        throw new JsonException($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }
                    if (reader.CurrentDepth == 1)
                    {
                        rootProperty = propertyName;
                    }
                    break;
                case JsonTokenType.Null when isPackageManifest:
                    // Import groups forbid null recursively; older sections retain their existing null policy.
                    var isImportGroup = rootProperty is "packageArtifactImports" or "packageApiImports"
                        or "providerImports" or "platformImports";
                    var isBundleProvenance = rootProperty == "contractBundles" && reader.CurrentDepth == 3
                        && propertyName is "source" or "authorityKind" or "authorityId"
                            or "selectedAuthorityRelease" or "selectedAuthorityContentIdentity";
                    var isDependencyPresence = rootProperty == "dependsOn" && reader.CurrentDepth == 3
                        && propertyName == "presence";
                    if (isImportGroup || isBundleProvenance || isDependencyPresence)
                    {
                        throw new JsonException($"Explicit JSON null is not allowed in manifest '{rootProperty}' metadata.");
                    }
                    break;
            }
        }
    }
}
