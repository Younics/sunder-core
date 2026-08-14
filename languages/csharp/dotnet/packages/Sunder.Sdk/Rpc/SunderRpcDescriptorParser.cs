using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Sdk.Rpc;

internal static class SunderRpcDescriptorParser
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const int MaximumJsonDepth = 256;
    private const int MaximumServices = 64;
    private const int MaximumMethods = 512;
    private const int MaximumDefinitions = 256;
    private const int MaximumErrors = 256;
    private const int MaximumAnnotationCharacters = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] AbiV2DomainSeparator = "sunder.rpc.abi.v2\0"u8.ToArray();

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
        }
        catch (DecoderFallbackException exception)
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
                MaxDepth = MaximumJsonDepth,
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

            var descriptorVersion = Required(root, "descriptorVersion", "$", JsonValueKind.Number);
            if (!descriptorVersion.TryGetInt32(out var descriptorVersionValue)
                || descriptorVersionValue is not (SunderRpcProtocol.DescriptorVersion or SunderRpcProtocol.DescriptorVersionV2))
            {
                throw Error(
                    $"$.descriptorVersion must be {SunderRpcProtocol.DescriptorVersion} or {SunderRpcProtocol.DescriptorVersionV2}.");
            }

            try
            {
                ValidateTokenStream(descriptorUtf8, descriptorVersionValue);
            }
            catch (SunderRpcDescriptorException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw Error($"The RPC descriptor is not strict UTF-8 JSON: {exception.Message}", exception);
            }

            var isV2 = descriptorVersionValue == SunderRpcProtocol.DescriptorVersionV2;
            if (isV2)
            {
                RequireOnlyProperties(
                    root,
                    "$",
                    "$schema",
                    "descriptorVersion",
                    "contractId",
                    "version",
                    "errors",
                    "services",
                    "$defs");
            }
            else
            {
                RequireOnlyProperties(
                    root,
                    "$",
                    "$schema",
                    "descriptorVersion",
                    "contractId",
                    "version",
                    "services",
                    "$defs");
            }

            if (root.TryGetProperty("$schema", out var dialect))
            {
                RequireKind(dialect, JsonValueKind.String, "$.$schema", "a string");
                if (!string.Equals(dialect.GetString(), SunderRpcProtocol.JsonSchemaDialect, StringComparison.Ordinal))
                {
                    throw Error($"$.$schema must be '{SunderRpcProtocol.JsonSchemaDialect}'.");
                }
            }

            var contractId = RequiredString(root, "contractId", "$", isV2 ? 128 : 256);
            if (isV2 ? !IsContractIdV2(contractId) : !PackageId.TryParse(contractId, out _))
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

            SunderRpcSchemaProfile.ValidateDefinitions(definitions, descriptorVersionValue);
            var errorDescriptions = isV2
                ? ParseErrors(Required(root, "errors", "$", JsonValueKind.Object))
                : new Dictionary<string, string?>(StringComparer.Ordinal);
            var servicesElement = isV2
                ? Required(root, "services", "$", JsonValueKind.Object)
                : Required(root, "services", "$", JsonValueKind.Array, JsonValueKind.Object);
            var services = ParseServices(
                servicesElement,
                definitions,
                errorDescriptions.Keys.ToHashSet(StringComparer.Ordinal),
                descriptorVersionValue);
            var canonicalUtf8 = Canonicalize(root);
            var sha256 = Convert.ToHexString(SHA256.HashData(canonicalUtf8)).ToLowerInvariant();
            var canonicalAbiProjectionUtf8 = isV2
                ? CanonicalizeAbiProjection(root, definitions)
                : null;
            var abiSha256 = canonicalAbiProjectionUtf8 is null
                ? null
                : HashAbiV2(canonicalAbiProjectionUtf8);
            return new SunderRpcContractDescriptor(
                descriptorVersionValue,
                contractId,
                version,
                errorDescriptions,
                services,
                definitions,
                canonicalUtf8,
                sha256,
                canonicalAbiProjectionUtf8,
                abiSha256);
        }
    }

    private static Dictionary<string, string?> ParseErrors(JsonElement element)
    {
        if (element.GetPropertyCount() > MaximumErrors)
        {
            throw Error($"$.errors may contain at most {MaximumErrors} declarations.");
        }

        var errors = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var declaration in element.EnumerateObject())
        {
            var path = "$.errors." + declaration.Name;
            if (!IsMemberId(declaration.Name))
            {
                throw Error($"{path} has an invalid domain error code.");
            }
            RequireKind(declaration.Value, JsonValueKind.Object, path, "an object");
            RequireOnlyProperties(declaration.Value, path, "description");
            string? description = null;
            if (declaration.Value.TryGetProperty("description", out var descriptionElement))
            {
                RequireKind(descriptionElement, JsonValueKind.String, path + ".description", "a string");
                description = descriptionElement.GetString();
                if (CountUnicodeScalars(description!) > MaximumAnnotationCharacters)
                {
                    throw Error($"{path}.description may contain at most {MaximumAnnotationCharacters} characters.");
                }
            }
            errors.Add(declaration.Name, description);
        }
        return errors;
    }

    private static IReadOnlyList<SunderRpcServiceDescriptor> ParseServices(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> definitions,
        IReadOnlySet<string> declaredErrors,
        int descriptorVersion)
    {
        var services = new List<SunderRpcServiceDescriptor>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        var methodCount = 0;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var service in element.EnumerateArray())
            {
                services.Add(ParseService(
                    service,
                    null,
                    definitions,
                    declaredErrors,
                    descriptorVersion,
                    ref methodCount));
            }
        }
        else
        {
            foreach (var service in element.EnumerateObject())
            {
                services.Add(ParseService(
                    service.Value,
                    service.Name,
                    definitions,
                    declaredErrors,
                    descriptorVersion,
                    ref methodCount));
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
        IReadOnlySet<string> declaredErrors,
        int descriptorVersion,
        ref int totalMethodCount)
    {
        var isV2 = descriptorVersion == SunderRpcProtocol.DescriptorVersionV2;
        var path = keyedServiceId is null ? "$.services[]" : $"$.services.{keyedServiceId}";
        RequireKind(element, JsonValueKind.Object, path, "an object");
        if (isV2)
        {
            RequireOnlyProperties(element, path, "methods");
        }
        else
        {
            RequireOnlyProperties(element, path, "serviceId", "methods");
        }
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

        var methodsElement = isV2
            ? Required(element, "methods", path, JsonValueKind.Object)
            : Required(element, "methods", path, JsonValueKind.Array, JsonValueKind.Object);
        var methods = new List<SunderRpcMethodDescriptor>();
        if (methodsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var method in methodsElement.EnumerateArray())
            {
                methods.Add(ParseMethod(
                    method,
                    null,
                    path + ".methods[]",
                    definitions,
                    declaredErrors,
                    descriptorVersion));
            }
        }
        else
        {
            foreach (var method in methodsElement.EnumerateObject())
            {
                methods.Add(ParseMethod(
                    method.Value,
                    method.Name,
                    path + ".methods." + method.Name,
                    definitions,
                    declaredErrors,
                    descriptorVersion));
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
        IReadOnlyDictionary<string, JsonElement> definitions,
        IReadOnlySet<string> declaredErrors,
        int descriptorVersion)
    {
        var isV2 = descriptorVersion == SunderRpcProtocol.DescriptorVersionV2;
        RequireKind(element, JsonValueKind.Object, path, "an object");
        if (isV2)
        {
            RequireOnlyProperties(
                element,
                path,
                "kind",
                "idempotency",
                "errors",
                "requestSchema",
                "responseSchema",
                "eventSchema");
        }
        else
        {
            RequireOnlyProperties(
                element,
                path,
                "methodId",
                "kind",
                "requestSchema",
                "responseSchema",
                "eventSchema");
        }
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
        var idempotency = SunderRpcMethodIdempotency.None;
        IReadOnlyList<string> errors = [];
        if (isV2)
        {
            idempotency = RequiredString(element, "idempotency", path, 32) switch
            {
                "none" => SunderRpcMethodIdempotency.None,
                "idempotent" => SunderRpcMethodIdempotency.Idempotent,
                _ => throw Error($"{path}.idempotency must be 'none' or 'idempotent'."),
            };
            errors = ParseMethodErrors(
                Required(element, "errors", path, JsonValueKind.Array),
                path + ".errors",
                declaredErrors);
        }

        var requestReference = ReadSchemaReference(
            Required(element, "requestSchema", path),
            path + ".requestSchema",
            definitions,
            descriptorVersion);
        var outputProperty = kind == SunderRpcMethodKind.Unary ? "responseSchema" : "eventSchema";
        var unexpectedProperty = kind == SunderRpcMethodKind.Unary ? "eventSchema" : "responseSchema";
        if (element.TryGetProperty(unexpectedProperty, out _))
        {
            throw Error($"{path}.{unexpectedProperty} is not valid for a {kindValue} method.");
        }
        var outputReference = ReadSchemaReference(
            Required(element, outputProperty, path),
            path + "." + outputProperty,
            definitions,
            descriptorVersion);
        return new SunderRpcMethodDescriptor(methodId, kind, requestReference, outputReference, idempotency, errors);
    }

    private static IReadOnlyList<string> ParseMethodErrors(
        JsonElement element,
        string path,
        IReadOnlySet<string> declaredErrors)
    {
        if (element.GetArrayLength() > MaximumErrors)
        {
            throw Error($"{path} may contain at most {MaximumErrors} error codes.");
        }

        var errors = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { } code
                || !IsMemberId(code)
                || !unique.Add(code))
            {
                throw Error($"{path} must contain distinct domain error codes.");
            }
            if (!declaredErrors.Contains(code))
            {
                throw Error($"{path} references undeclared domain error '{code}'.");
            }
            errors.Add(code);
        }
        return errors;
    }

    internal static string ReadSchemaReference(
        JsonElement element,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        int descriptorVersion)
    {
        string? reference;
        if (descriptorVersion == SunderRpcProtocol.DescriptorVersion && element.ValueKind == JsonValueKind.String)
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

    private static void ValidateTokenStream(ReadOnlySpan<byte> utf8, int descriptorVersion)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        });
        IEqualityComparer<string> caseComparer = descriptorVersion == SunderRpcProtocol.DescriptorVersionV2
            ? AsciiCaseInsensitiveComparer.Instance
            : StringComparer.OrdinalIgnoreCase;
        var objects = new Stack<ObjectNames?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new ObjectNames(caseComparer));
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
                    ValidateUnicodeScalars(propertyName);
                    if (!names.Exact.Add(propertyName))
                    {
                        throw Error($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }
                    if (!names.IgnoreCase.Add(propertyName))
                    {
                        throw Error($"Case-colliding JSON property '{propertyName}' is not allowed.");
                    }
                    break;
                case JsonTokenType.String:
                    ValidateUnicodeScalars(reader.GetString()!);
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
        if (text.Length == 0 || text == "-0") return false;
        var offset = text[0] == '-' ? 1 : 0;
        if (offset == text.Length || text[offset] == '0' && text.Length - offset != 1) return false;
        return text.AsSpan(offset).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static void ValidateUnicodeScalars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index]))
            {
                throw Error("JSON strings must contain only Unicode scalar values.");
            }
        }
    }

    internal static int CountUnicodeScalars(string value)
        => value.EnumerateRunes().Count();

    private static byte[] Canonicalize(JsonElement root)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteCanonical(output, root);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(ArrayBufferWriter<byte> output, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteByte(output, (byte)'{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) WriteByte(output, (byte)',');
                    firstProperty = false;
                    WriteJsonString(output, property.Name);
                    WriteByte(output, (byte)':');
                    WriteCanonical(output, property.Value);
                }
                WriteByte(output, (byte)'}');
                break;
            case JsonValueKind.Array:
                WriteByte(output, (byte)'[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) WriteByte(output, (byte)',');
                    firstItem = false;
                    WriteCanonical(output, item);
                }
                WriteByte(output, (byte)']');
                break;
            case JsonValueKind.String:
                WriteJsonString(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                WriteAscii(output, element.GetRawText());
                break;
            case JsonValueKind.True:
                WriteBytes(output, "true"u8);
                break;
            case JsonValueKind.False:
                WriteBytes(output, "false"u8);
                break;
            case JsonValueKind.Null:
                WriteBytes(output, "null"u8);
                break;
            default:
                throw Error("An RPC descriptor contains an unsupported JSON token.");
        }
    }

    private static byte[] CanonicalizeAbiProjection(
        JsonElement root,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteAbiElement(output, root, AbiContext.Root, definitions);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteAbiElement(
        ArrayBufferWriter<byte> output,
        JsonElement element,
        AbiContext context,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            WriteAbiObject(output, element, context, definitions);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            WriteAbiArray(output, element.EnumerateArray(), AbiContext.Generic, definitions);
            return;
        }
        WriteCanonical(output, element);
    }

    private static void WriteAbiObject(
        ArrayBufferWriter<byte> output,
        JsonElement element,
        AbiContext context,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var isReference = context == AbiContext.Schema && element.TryGetProperty("$ref", out _);
        var properties = element.EnumerateObject()
            .Where(property => context switch
            {
                AbiContext.Root => property.Name is not "$schema" and not "version",
                AbiContext.ErrorDeclaration => property.Name != "description",
                AbiContext.Schema when !isReference => property.Name is not "title" and not "description",
                _ => true,
            })
            .OrderBy(static property => property.Name, StringComparer.Ordinal);

        WriteByte(output, (byte)'{');
        var first = true;
        foreach (var property in properties)
        {
            if (!first) WriteByte(output, (byte)',');
            first = false;
            WriteJsonString(output, property.Name);
            WriteByte(output, (byte)':');
            WriteAbiPropertyValue(output, property, context, definitions);
        }
        WriteByte(output, (byte)'}');
    }

    private static void WriteAbiPropertyValue(
        ArrayBufferWriter<byte> output,
        JsonProperty property,
        AbiContext parentContext,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var childContext = parentContext switch
        {
            AbiContext.Root when property.Name == "errors" => AbiContext.ErrorMap,
            AbiContext.Root when property.Name == "services" => AbiContext.Services,
            AbiContext.Root when property.Name == "$defs" => AbiContext.Definitions,
            AbiContext.ErrorMap => AbiContext.ErrorDeclaration,
            AbiContext.Services => AbiContext.Service,
            AbiContext.Service when property.Name == "methods" => AbiContext.Methods,
            AbiContext.Methods => AbiContext.Method,
            AbiContext.Definitions => AbiContext.Schema,
            AbiContext.RecordProperties => AbiContext.Schema,
            AbiContext.Method when property.Name is "requestSchema" or "responseSchema" or "eventSchema" => AbiContext.Schema,
            AbiContext.Schema when property.Name == "properties" => AbiContext.RecordProperties,
            AbiContext.Schema when property.Name is "items" or "additionalProperties" or "propertyNames" => AbiContext.Schema,
            _ => AbiContext.Generic,
        };

        if (parentContext == AbiContext.Method && property.Name == "errors")
        {
            WriteSortedStringArray(output, property.Value, definitions);
        }
        else if (parentContext == AbiContext.Schema && property.Name == "required")
        {
            WriteSortedStringArray(output, property.Value, definitions);
        }
        else if (parentContext == AbiContext.Schema && property.Name == "enum")
        {
            WriteSortedEnum(output, property.Value, definitions);
        }
        else if (parentContext == AbiContext.Schema && property.Name == "oneOf")
        {
            WriteNormalizedOneOf(output, property.Value, definitions);
        }
        else
        {
            WriteAbiElement(output, property.Value, childContext, definitions);
        }
    }

    private static void WriteSortedStringArray(
        ArrayBufferWriter<byte> output,
        JsonElement array,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var items = array.EnumerateArray()
            .OrderBy(static item => item.GetString(), StringComparer.Ordinal)
            .ToArray();
        WriteAbiArray(output, items, AbiContext.Generic, definitions);
    }

    private static void WriteSortedEnum(
        ArrayBufferWriter<byte> output,
        JsonElement array,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var items = array.EnumerateArray()
            .Select(static item => (Value: item, Canonical: Canonicalize(item)))
            .OrderBy(static item => item.Canonical, ByteArrayComparer.Instance)
            .Select(static item => item.Value)
            .ToArray();
        WriteAbiArray(output, items, AbiContext.Generic, definitions);
    }

    private static void WriteNormalizedOneOf(
        ArrayBufferWriter<byte> output,
        JsonElement oneOf,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var variants = oneOf.EnumerateArray().ToArray();
        if (variants.Length == 2
            && variants.Count(variant => IsNullSchema(ResolveSchema(variant, definitions))) == 1)
        {
            variants = variants
                .OrderBy(variant => IsNullSchema(ResolveSchema(variant, definitions)) ? 0 : 1)
                .ToArray();
        }
        else
        {
            var discriminator = FindTaggedUnionDiscriminator(variants, definitions);
            variants = variants
                .OrderBy(
                    variant => GetDiscriminatorValue(ResolveSchema(variant, definitions), discriminator),
                    StringComparer.Ordinal)
                .ToArray();
        }
        WriteAbiArray(output, variants, AbiContext.Schema, definitions);
    }

    private static void WriteAbiArray(
        ArrayBufferWriter<byte> output,
        IEnumerable<JsonElement> elements,
        AbiContext itemContext,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        WriteByte(output, (byte)'[');
        var first = true;
        foreach (var element in elements)
        {
            if (!first) WriteByte(output, (byte)',');
            first = false;
            WriteAbiElement(output, element, itemContext, definitions);
        }
        WriteByte(output, (byte)']');
    }

    private static string FindTaggedUnionDiscriminator(
        IReadOnlyList<JsonElement> variants,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        var resolved = variants.Select(variant => ResolveSchema(variant, definitions)).ToArray();
        var firstRequired = resolved[0].GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in resolved[0].GetProperty("properties").EnumerateObject())
        {
            if (!firstRequired.Contains(property.Name)) continue;
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (resolved.All(variant => TryGetDiscriminatorValue(variant, property.Name, out var value)
                                        && values.Add(value)))
            {
                return property.Name;
            }
        }
        throw Error("A validated tagged union has no discriminator.");
    }

    private static bool TryGetDiscriminatorValue(JsonElement schema, string propertyName, out string value)
    {
        var required = schema.GetProperty("required").EnumerateArray()
            .Any(item => string.Equals(item.GetString(), propertyName, StringComparison.Ordinal));
        if (required
            && schema.GetProperty("properties").TryGetProperty(propertyName, out var property)
            && property.TryGetProperty("type", out var type)
            && type.GetString() == "string"
            && property.TryGetProperty("const", out var constant)
            && constant.ValueKind == JsonValueKind.String)
        {
            value = constant.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string GetDiscriminatorValue(JsonElement schema, string propertyName)
    {
        _ = TryGetDiscriminatorValue(schema, propertyName, out var value);
        return value;
    }

    private static JsonElement ResolveSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            TryGetReferenceName(reference.GetString(), out var name);
            schema = definitions[name];
        }
        return schema;
    }

    private static bool IsNullSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type) && type.GetString() == "null";

    private static string HashAbiV2(byte[] canonicalProjection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(AbiV2DomainSeparator);
        hash.AppendData(canonicalProjection);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteJsonString(ArrayBufferWriter<byte> output, string value)
    {
        WriteByte(output, (byte)'"');
        Span<byte> encoded = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    WriteBytes(output, "\\\""u8);
                    break;
                case '\\':
                    WriteBytes(output, "\\\\"u8);
                    break;
                case '\b':
                    WriteBytes(output, "\\b"u8);
                    break;
                case '\t':
                    WriteBytes(output, "\\t"u8);
                    break;
                case '\n':
                    WriteBytes(output, "\\n"u8);
                    break;
                case '\f':
                    WriteBytes(output, "\\f"u8);
                    break;
                case '\r':
                    WriteBytes(output, "\\r"u8);
                    break;
                case < 0x20:
                    WriteBytes(output, "\\u00"u8);
                    WriteByte(output, ToLowerHex(rune.Value >> 4));
                    WriteByte(output, ToLowerHex(rune.Value));
                    break;
                default:
                    var length = rune.EncodeToUtf8(encoded);
                    WriteBytes(output, encoded[..length]);
                    break;
            }
        }
        WriteByte(output, (byte)'"');
    }

    private static byte ToLowerHex(int value)
        => (byte)((value & 0xf) is < 10 ? '0' + (value & 0xf) : 'a' + (value & 0xf) - 10);

    private static void WriteAscii(ArrayBufferWriter<byte> output, string value)
    {
        var target = output.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++) target[index] = (byte)value[index];
        output.Advance(value.Length);
    }

    private static void WriteByte(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
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

    private static bool IsContractIdV2(string value)
        => value.Length is > 0 and <= 128
           && value[0] != '.'
           && value[^1] != '.'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.All(static character => character is >= 'a' and <= 'z'
               or >= '0' and <= '9'
               or '.');

    private static bool IsDefinitionName(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
               || character is '.' or '-' or '_');

    private enum AbiContext
    {
        Generic,
        Root,
        ErrorMap,
        ErrorDeclaration,
        Services,
        Service,
        Methods,
        Method,
        Definitions,
        Schema,
        RecordProperties,
    }

    private sealed class ObjectNames(IEqualityComparer<string> caseComparer)
    {
        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoreCase { get; } = new(caseComparer);
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

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var length = Math.Min(left.Length, right.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
