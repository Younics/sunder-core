using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Sdk.Rpc;

internal static class SunderRpcDescriptorParser
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const int MaximumDepth = 64;
    private const int MaximumServices = 64;
    private const int MaximumMethods = 512;
    private const int MaximumDefinitions = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SunderRpcContractDescriptor Parse(ReadOnlySpan<byte> descriptorUtf8)
    {
        if (descriptorUtf8.Length == 0 || descriptorUtf8.Length > SunderRpcProtocol.MaximumDescriptorBytes)
        {
            throw Error($"An RPC descriptor must be non-empty and no larger than {SunderRpcProtocol.MaximumDescriptorBytes} bytes.");
        }
        if (descriptorUtf8.StartsWith(Encoding.UTF8.Preamble))
        {
            throw Error("An RPC descriptor must be UTF-8 without a byte-order mark.");
        }

        try
        {
            _ = StrictUtf8.GetString(descriptorUtf8);
            ValidateTokenStream(descriptorUtf8);
        }
        catch (SunderRpcDescriptorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw Error($"The RPC descriptor is not strict UTF-8 JSON: {exception.Message}", exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(descriptorUtf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth,
            });
        }
        catch (JsonException exception)
        {
            throw Error($"The RPC descriptor is invalid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "$", "an object");
            RequireOnlyProperties(
                root,
                "$",
                "$schema",
                "descriptorVersion",
                "contractId",
                "version",
                "services",
                "$defs");

            if (root.TryGetProperty("$schema", out var dialect))
            {
                RequireKind(dialect, JsonValueKind.String, "$.$schema", "a string");
                if (!string.Equals(dialect.GetString(), SunderRpcProtocol.JsonSchemaDialect, StringComparison.Ordinal))
                {
                    throw Error($"$.$schema must be '{SunderRpcProtocol.JsonSchemaDialect}'.");
                }
            }

            var descriptorVersion = Required(root, "descriptorVersion", "$", JsonValueKind.Number);
            if (!descriptorVersion.TryGetInt32(out var descriptorVersionValue)
                || descriptorVersionValue != SunderRpcProtocol.DescriptorVersion)
            {
                throw Error($"$.descriptorVersion must be {SunderRpcProtocol.DescriptorVersion}.");
            }

            var contractId = RequiredString(root, "contractId", "$", 256);
            if (!PackageId.TryParse(contractId, out _))
            {
                throw Error("$.contractId must use lowercase dot-separated ASCII identifiers.");
            }

            var version = RequiredString(root, "version", "$", SemanticVersion.MaximumLength);
            if (!SemanticVersion.TryParse(version, out _))
            {
                throw Error("$.version must be an independent strict SemVer 2.0 version.");
            }

            var definitionsElement = Required(root, "$defs", "$", JsonValueKind.Object);
            if (definitionsElement.GetPropertyCount() == 0
                || definitionsElement.GetPropertyCount() > MaximumDefinitions)
            {
                throw Error($"$.$defs must contain between 1 and {MaximumDefinitions} schemas.");
            }
            var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var definition in definitionsElement.EnumerateObject())
            {
                if (!IsDefinitionName(definition.Name))
                {
                    throw Error($"$.$defs contains invalid schema name '{definition.Name}'.");
                }
                definitions.Add(definition.Name, definition.Value.Clone());
            }

            SunderRpcSchemaProfile.ValidateDefinitions(definitions);
            var servicesElement = Required(root, "services", "$", JsonValueKind.Array, JsonValueKind.Object);
            var services = ParseServices(servicesElement, definitions);
            var canonicalUtf8 = Canonicalize(root);
            var sha256 = Convert.ToHexString(SHA256.HashData(canonicalUtf8)).ToLowerInvariant();
            return new SunderRpcContractDescriptor(
                contractId,
                version,
                services,
                definitions,
                canonicalUtf8,
                sha256);
        }
    }

    private static IReadOnlyList<SunderRpcServiceDescriptor> ParseServices(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var services = new List<SunderRpcServiceDescriptor>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        var methodCount = 0;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var service in element.EnumerateArray())
            {
                services.Add(ParseService(service, null, definitions, ref methodCount));
            }
        }
        else
        {
            foreach (var service in element.EnumerateObject())
            {
                services.Add(ParseService(service.Value, service.Name, definitions, ref methodCount));
            }
        }

        if (services.Count == 0 || services.Count > MaximumServices)
        {
            throw Error($"$.services must contain between 1 and {MaximumServices} services.");
        }
        foreach (var service in services)
        {
            if (!serviceIds.Add(service.ServiceId))
            {
                throw Error($"$.services declares serviceId '{service.ServiceId}' more than once.");
            }
        }
        return services;
    }

    private static SunderRpcServiceDescriptor ParseService(
        JsonElement element,
        string? keyedServiceId,
        IReadOnlyDictionary<string, JsonElement> definitions,
        ref int totalMethodCount)
    {
        var path = keyedServiceId is null ? "$.services[]" : $"$.services.{keyedServiceId}";
        RequireKind(element, JsonValueKind.Object, path, "an object");
        RequireOnlyProperties(element, path, "serviceId", "methods");
        var serviceId = keyedServiceId ?? RequiredString(element, "serviceId", path, 128);
        if (element.TryGetProperty("serviceId", out var declaredServiceId))
        {
            RequireKind(declaredServiceId, JsonValueKind.String, path + ".serviceId", "a string");
            if (!string.Equals(serviceId, declaredServiceId.GetString(), StringComparison.Ordinal))
            {
                throw Error($"{path}.serviceId must match its services object key.");
            }
        }
        if (!IsMemberId(serviceId))
        {
            throw Error($"{path}.serviceId is not a stable lowercase ASCII identifier.");
        }

        var methodsElement = Required(element, "methods", path, JsonValueKind.Array, JsonValueKind.Object);
        var methods = new List<SunderRpcMethodDescriptor>();
        if (methodsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var method in methodsElement.EnumerateArray())
            {
                methods.Add(ParseMethod(method, null, path + ".methods[]", definitions));
            }
        }
        else
        {
            foreach (var method in methodsElement.EnumerateObject())
            {
                methods.Add(ParseMethod(method.Value, method.Name, path + ".methods." + method.Name, definitions));
            }
        }

        if (methods.Count == 0)
        {
            throw Error($"{path}.methods must contain at least one method.");
        }
        totalMethodCount = checked(totalMethodCount + methods.Count);
        if (totalMethodCount > MaximumMethods)
        {
            throw Error($"An RPC descriptor may declare at most {MaximumMethods} methods.");
        }
        var methodIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            if (!methodIds.Add(method.MethodId))
            {
                throw Error($"{path}.methods declares methodId '{method.MethodId}' more than once.");
            }
        }
        return new SunderRpcServiceDescriptor(serviceId, methods);
    }

    private static SunderRpcMethodDescriptor ParseMethod(
        JsonElement element,
        string? keyedMethodId,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        RequireKind(element, JsonValueKind.Object, path, "an object");
        RequireOnlyProperties(
            element,
            path,
            "methodId",
            "kind",
            "requestSchema",
            "responseSchema",
            "eventSchema");
        var methodId = keyedMethodId ?? RequiredString(element, "methodId", path, 128);
        if (element.TryGetProperty("methodId", out var declaredMethodId))
        {
            RequireKind(declaredMethodId, JsonValueKind.String, path + ".methodId", "a string");
            if (!string.Equals(methodId, declaredMethodId.GetString(), StringComparison.Ordinal))
            {
                throw Error($"{path}.methodId must match its methods object key.");
            }
        }
        if (!IsMemberId(methodId))
        {
            throw Error($"{path}.methodId is not a stable lowercase ASCII identifier.");
        }

        var kindValue = RequiredString(element, "kind", path, 32);
        var kind = kindValue switch
        {
            "unary" => SunderRpcMethodKind.Unary,
            "server-stream" => SunderRpcMethodKind.ServerStream,
            _ => throw Error($"{path}.kind must be 'unary' or 'server-stream'."),
        };
        var requestReference = ReadSchemaReference(Required(element, "requestSchema", path), path + ".requestSchema", definitions);
        var outputProperty = kind == SunderRpcMethodKind.Unary ? "responseSchema" : "eventSchema";
        var unexpectedProperty = kind == SunderRpcMethodKind.Unary ? "eventSchema" : "responseSchema";
        if (element.TryGetProperty(unexpectedProperty, out _))
        {
            throw Error($"{path}.{unexpectedProperty} is not valid for a {kindValue} method.");
        }
        var outputReference = ReadSchemaReference(Required(element, outputProperty, path), path + "." + outputProperty, definitions);
        return new SunderRpcMethodDescriptor(methodId, kind, requestReference, outputReference);
    }

    internal static string ReadSchemaReference(
        JsonElement element,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        string? reference;
        if (element.ValueKind == JsonValueKind.String)
        {
            reference = element.GetString();
        }
        else
        {
            RequireKind(element, JsonValueKind.Object, path, "a local $ref object");
            RequireOnlyProperties(element, path, "$ref");
            reference = RequiredString(element, "$ref", path, 256);
        }

        if (!TryGetReferenceName(reference, out var definitionName)
            || !definitions.ContainsKey(definitionName))
        {
            throw Error($"{path} must reference an existing local schema with '#/$defs/&lt;name&gt;'.");
        }
        return reference!;
    }

    internal static bool TryGetReferenceName(string? reference, out string definitionName)
    {
        const string prefix = "#/$defs/";
        if (reference is not null
            && reference.StartsWith(prefix, StringComparison.Ordinal)
            && reference.Length > prefix.Length)
        {
            definitionName = reference[prefix.Length..];
            return IsDefinitionName(definitionName);
        }

        definitionName = string.Empty;
        return false;
    }

    private static void ValidateTokenStream(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth,
        });
        var objects = new Stack<ObjectNames?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new ObjectNames());
                    break;
                case JsonTokenType.StartArray:
                    objects.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var names = objects.Peek()
                                ?? throw Error("A JSON property appeared outside an object.");
                    var propertyName = reader.GetString()!;
                    if (!names.Exact.Add(propertyName))
                    {
                        throw Error($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }
                    if (!names.IgnoreCase.Add(propertyName))
                    {
                        throw Error($"Case-colliding JSON property '{propertyName}' is not allowed.");
                    }
                    break;
                case JsonTokenType.Number:
                    var token = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
                    var text = Encoding.ASCII.GetString(token);
                    if (!IsCanonicalInteger(text)
                        || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                        || value is < -MaximumSafeInteger or > MaximumSafeInteger)
                    {
                        throw Error(
                            $"Descriptor number '{text}' is unsupported; descriptor numbers must be canonical IEEE-754 safe integers.");
                    }
                    break;
            }
        }
    }

    private static bool IsCanonicalInteger(string text)
    {
        if (text.Length == 0) return false;
        var offset = text[0] == '-' ? 1 : 0;
        if (offset == text.Length || text[offset] == '0' && text.Length - offset != 1) return false;
        return text.AsSpan(offset).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static byte[] Canonicalize(JsonElement root)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        });
        WriteCanonical(writer, root);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetInt64());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Error("An RPC descriptor contains an unsupported JSON token.");
        }
    }

    internal static JsonElement Required(JsonElement element, string name, string path, params JsonValueKind[] kinds)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Error($"{path} is missing required property '{name}'.");
        }
        if (kinds.Length > 0 && !kinds.Contains(value.ValueKind))
        {
            throw Error($"{path}.{name} has unsupported JSON kind '{value.ValueKind}'.");
        }
        return value;
    }

    internal static string RequiredString(JsonElement element, string name, string path, int maximumLength)
    {
        var value = Required(element, name, path, JsonValueKind.String).GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            throw Error($"{path}.{name} must be non-empty and at most {maximumLength} characters.");
        }
        return value;
    }

    internal static void RequireOnlyProperties(JsonElement element, string path, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
            {
                throw Error($"{path} contains unsupported property '{property.Name}'.");
            }
        }
    }

    internal static void RequireKind(JsonElement element, JsonValueKind kind, string path, string description)
    {
        if (element.ValueKind != kind)
        {
            throw Error($"{path} must be {description}.");
        }
    }

    internal static SunderRpcDescriptorException Error(string message)
        => new(message.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal));

    private static SunderRpcDescriptorException Error(string message, Exception innerException)
        => new(message, innerException);

    private static bool IsMemberId(string value)
        => value.Length is > 0 and <= 128
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value.All(static character => character is >= 'a' and <= 'z'
               or >= '0' and <= '9'
               or '.' or '-' or '_');

    private static bool IsDefinitionName(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
               || character is '.' or '-' or '_');

    private sealed class ObjectNames
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoreCase { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
