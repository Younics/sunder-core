using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcDescriptorParser
{
    private static Dictionary<string, SunderRpcErrorDeclaration> ParseErrors(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        if (element.GetPropertyCount() > MaximumErrors)
        {
            throw Error($"$.errors may contain at most {MaximumErrors} declarations.");
        }

        var errors = new Dictionary<string, SunderRpcErrorDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in element.EnumerateObject())
        {
            var path = "$.errors." + declaration.Name;
            if (!IsMemberId(declaration.Name))
            {
                throw Error($"{path} has an invalid domain error code.");
            }
            RequireKind(declaration.Value, JsonValueKind.Object, path, "an object");
            RequireOnlyProperties(declaration.Value, path, "description", "detailsSchema");
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
            string? detailsSchemaReference = null;
            if (declaration.Value.TryGetProperty("detailsSchema", out var detailsSchema))
            {
                detailsSchemaReference = ReadSchemaReference(
                    detailsSchema,
                    path + ".detailsSchema",
                    definitions);
            }
            errors.Add(
                declaration.Name,
                new SunderRpcErrorDeclaration(
                    declaration.Name,
                    description,
                    detailsSchemaReference));
        }
        return errors;
    }

    private static IReadOnlyList<SunderRpcServiceDescriptor> ParseServices(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> definitions,
        IReadOnlySet<string> declaredErrors)
    {
        var services = new List<SunderRpcServiceDescriptor>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        var methodCount = 0;
        foreach (var service in element.EnumerateObject())
        {
            services.Add(ParseService(
                service.Value,
                service.Name,
                definitions,
                declaredErrors,
                ref methodCount));
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
        string serviceId,
        IReadOnlyDictionary<string, JsonElement> definitions,
        IReadOnlySet<string> declaredErrors,
        ref int totalMethodCount)
    {
        var path = $"$.services.{serviceId}";
        RequireKind(element, JsonValueKind.Object, path, "an object");
        RequireOnlyProperties(element, path, "methods");
        if (!IsMemberId(serviceId))
        {
            throw Error($"{path}.serviceId is not a stable lowercase ASCII identifier.");
        }

        var methodsElement = Required(element, "methods", path, JsonValueKind.Object);
        var methods = new List<SunderRpcMethodDescriptor>();
        foreach (var method in methodsElement.EnumerateObject())
        {
            methods.Add(ParseMethod(
                method.Value,
                method.Name,
                path + ".methods." + method.Name,
                definitions,
                declaredErrors));
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
        string methodId,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions,
        IReadOnlySet<string> declaredErrors)
    {
        RequireKind(element, JsonValueKind.Object, path, "an object");
        RequireOnlyProperties(
            element,
            path,
            "kind",
            "idempotency",
            "errors",
            "requestSchema",
            "responseSchema",
            "eventSchema");
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
        var idempotency = RequiredString(element, "idempotency", path, 32) switch
        {
            "none" => SunderRpcMethodIdempotency.None,
            "idempotent" => SunderRpcMethodIdempotency.Idempotent,
            _ => throw Error($"{path}.idempotency must be 'none' or 'idempotent'."),
        };
        var errors = ParseMethodErrors(
            Required(element, "errors", path, JsonValueKind.Array),
            path + ".errors",
            declaredErrors);

        var requestReference = ReadSchemaReference(
            Required(element, "requestSchema", path),
            path + ".requestSchema",
            definitions);
        var outputProperty = kind == SunderRpcMethodKind.Unary ? "responseSchema" : "eventSchema";
        var unexpectedProperty = kind == SunderRpcMethodKind.Unary ? "eventSchema" : "responseSchema";
        if (element.TryGetProperty(unexpectedProperty, out _))
        {
            throw Error(
                $"{path}.{unexpectedProperty} is not valid for a {kindValue} method.",
                "method-shape");
        }
        var outputReference = ReadSchemaReference(
            Required(element, outputProperty, path),
            path + "." + outputProperty,
            definitions);
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
                throw Error(
                    $"{path} references undeclared domain error '{code}'.",
                    "domain-error-reference");
            }
            errors.Add(code);
        }
        return errors;
    }

    internal static string ReadSchemaReference(
        JsonElement element,
        string path,
        IReadOnlyDictionary<string, JsonElement> definitions)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error($"{path} must be a local $ref object.", "schema-reference-shape");
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name != "$ref")
            {
                throw Error(
                    $"{path} contains unsupported property '{property.Name}'.",
                    "schema-reference-shape");
            }
        }
        if (!element.TryGetProperty("$ref", out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(referenceElement.GetString())
            || referenceElement.GetString()!.Length > 256)
        {
            throw Error($"{path} must contain one string $ref.", "schema-reference-shape");
        }
        var reference = referenceElement.GetString();

        if (!TryGetReferenceName(reference, out var definitionName)
            || !definitions.ContainsKey(definitionName))
        {
            throw Error(
                $"{path} must reference an existing local schema with '#/$defs/&lt;name&gt;'.",
                "schema-reference-shape");
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
}
