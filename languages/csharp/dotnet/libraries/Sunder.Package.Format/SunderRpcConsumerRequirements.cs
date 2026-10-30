using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal sealed record SunderRpcRequiredMethod(string ServiceId, string MethodId);

// Selectors over an independently authenticated descriptor, not a replacement
// inventory or an authorization grant. Missing evidence must retain exact matching.
internal sealed class SunderRpcConsumerRequirements
{
    internal SunderRpcConsumerRequirements(SunderRpcContractDescriptor source,
        IReadOnlyList<SunderRpcRequiredMethod> methods,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? outputMembers = null,
        IReadOnlyList<string>? exactOutputDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count > 512 || methods.Any(method => method is null
                || source.FindService(method.ServiceId)?.FindMethod(method.MethodId) is null)
            || methods.Distinct().Count() != methods.Count)
            throw new InvalidDataException("Consumer requirements must select unique existing methods, at most 512 per contract.");
        Source = source;
        Methods = Array.AsReadOnly(methods.OrderBy(method => method.ServiceId, StringComparer.Ordinal)
            .ThenBy(method => method.MethodId, StringComparer.Ordinal).ToArray());
        var exact = (exactOutputDefinitions ?? []).ToArray();
        if (exact.Length > source.Definitions.Count || exact.Distinct(StringComparer.Ordinal).Count() != exact.Length
            || exact.Any(name => !source.Definitions.ContainsKey(name)))
            throw new InvalidDataException("Exact output definitions must be unique existing schemas.");
        ExactOutputDefinitions = Array.AsReadOnly(exact.Order(StringComparer.Ordinal).ToArray());
        var views = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (outputMembers is { Count: > 256 }) throw new InvalidDataException("Consumer output views exceed the definition limit.");
        var outputs = Closure(source, Roots(source, request: false), null);
        foreach (var (name, members) in outputMembers ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (!outputs.Contains(name) || !source.Definitions.TryGetValue(name, out var schema)
                || !schema.TryGetProperty("properties", out var properties)
                || !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False
                || schema.TryGetProperty("logicalType", out _) || schema.TryGetProperty("minProperties", out _)
                || schema.TryGetProperty("maxProperties", out _) || ExactOutputDefinitions.Contains(name, StringComparer.Ordinal)
                || members is null || members.Count > properties.GetPropertyCount()
                || members.Distinct(StringComparer.Ordinal).Count() != members.Count
                || members.Any(member => member is null || !properties.TryGetProperty(member, out _)))
                throw new InvalidDataException("Consumer output views must select existing members of a reachable, ordinary unbounded record; protocol/reference schemas stay exact.");
            views.Add(name, Array.AsReadOnly(members.Order(StringComparer.Ordinal).ToArray()));
        }
        OutputMembers = new ReadOnlyDictionary<string, IReadOnlyList<string>>(views);
        // Narrowing a parent cannot leave an orphaned child view in the evidence.
        var reachable = Closure(source, Roots(source, request: false), OutputMembers);
        if (views.Keys.Any(name => !reachable.Contains(name)))
            throw new InvalidDataException("Consumer output views must be reachable through the selected output view.");
    }

    internal SunderRpcContractDescriptor Source { get; }
    internal IReadOnlyList<SunderRpcRequiredMethod> Methods { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> OutputMembers { get; }
    internal IReadOnlyList<string> ExactOutputDefinitions { get; }

    internal IReadOnlySet<string> RequiredRequestDefinitions => Closure(Source, Roots(Source, request: true), null);
    internal IReadOnlyList<string> RequiredDefinitions => RequiredRequestDefinitions
        .Union(Closure(Source, Roots(Source, request: false), OutputMembers), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal JsonObject ToJson() => new()
    {
        ["schemaVersion"] = 1, ["contractId"] = Source.ContractId, ["contractVersion"] = Source.Version,
        ["descriptorSha256"] = Source.Sha256, ["abiSha256"] = Source.AbiSha256,
        ["methods"] = new JsonArray(Methods.Select(method => (JsonNode)new JsonObject
        { ["serviceId"] = method.ServiceId, ["methodId"] = method.MethodId }).ToArray()),
        ["outputMembers"] = new JsonObject(OutputMembers.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key,
            new JsonArray(pair.Value.Select(member => (JsonNode?)JsonValue.Create(member)).ToArray())))),
        ["exactOutputDefinitions"] = new JsonArray(ExactOutputDefinitions.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
    };

    internal byte[] ToCanonicalUtf8() => SunderRpcModelProjectionJson.Write(ToJson());

    // A value-reader schema only. It is never an authority/inventory descriptor.
    internal SunderRpcContractDescriptor OutputReader()
    {
        if (OutputMembers.Count == 0) return Source;
        var json = JsonNode.Parse(Source.GetCanonicalUtf8(), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
        foreach (var (name, members) in OutputMembers)
        {
            var schema = json["$defs"]![name]!.AsObject();
            var properties = schema["properties"]!.AsObject();
            foreach (var property in properties.ToArray()) if (!members.Contains(property.Key, StringComparer.Ordinal)) properties.Remove(property.Key);
            schema["required"] = new JsonArray(schema["required"]!.AsArray().Where(value => members.Contains(value!.GetValue<string>(), StringComparer.Ordinal))
                .Select(value => value!.DeepClone()).ToArray());
        }
        return SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { MaxDepth = 256 })));
    }

    internal static SunderRpcConsumerRequirements Parse(JsonElement value, SunderRpcContractDescriptor source)
    {
        SunderRpcAuthoringJson.RequireObject(value, "schemaVersion", "contractId", "contractVersion", "descriptorSha256", "abiSha256",
            "methods", "outputMembers", "exactOutputDefinitions");
        if (value.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number || value.GetProperty("schemaVersion").GetRawText() != "1"
            || Text(value, "contractId") != source.ContractId || Text(value, "contractVersion") != source.Version
            || Text(value, "descriptorSha256") != source.Sha256 || Text(value, "abiSha256") != source.AbiSha256)
            throw new InvalidDataException("Consumer requirement provenance differs from its exact imported descriptor.");
        var methods = value.GetProperty("methods");
        if (methods.ValueKind != JsonValueKind.Array || methods.GetArrayLength() > 512)
            throw new InvalidDataException("Consumer methods must be a bounded array.");
        var selected = methods.EnumerateArray().Select(method =>
        {
            SunderRpcAuthoringJson.RequireObject(method, "serviceId", "methodId");
            return new SunderRpcRequiredMethod(Text(method, "serviceId"), Text(method, "methodId"));
        }).ToArray();
        var output = value.GetProperty("outputMembers");
        if (output.ValueKind != JsonValueKind.Object || output.GetPropertyCount() > 256)
            throw new InvalidDataException("Consumer output views must be a bounded map.");
        var views = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in output.EnumerateObject())
            if (!views.TryAdd(entry.Name, Strings(entry.Value))) throw new InvalidDataException("Duplicate consumer output view.");
        var result = new SunderRpcConsumerRequirements(source, selected, views, Strings(value.GetProperty("exactOutputDefinitions")));
        if (!JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(result.ToJson())))
            throw new InvalidDataException("Consumer requirements must use canonical selector order.");
        return result;
    }

    // Projection only drops unrelated inventory. The full source/provider descriptors
    // have already been parsed; this is not a second schema parser.
    internal JsonObject Project(SunderRpcContractDescriptor descriptor)
    {
        var root = JsonNode.Parse(descriptor.GetCanonicalAbiProjectionUtf8(), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
        var services = root["services"]!.AsObject();
        foreach (var service in services.ToArray())
        {
            var methods = service.Value!["methods"]!.AsObject();
            foreach (var method in methods.ToArray())
                if (!Methods.Contains(new(service.Key, method.Key))) methods.Remove(method.Key);
            if (methods.Count == 0 && !Methods.Any(method => method.ServiceId == service.Key)) services.Remove(service.Key);
        }
        var errors = root["errors"]!.AsObject();
        var usedErrors = SelectedMethods(descriptor).SelectMany(method => method.Errors).ToHashSet(StringComparer.Ordinal);
        foreach (var error in errors.ToArray()) if (!usedErrors.Contains(error.Key)) errors.Remove(error.Key);
        var definitions = root["$defs"]!.AsObject();
        var required = Closure(descriptor, Roots(descriptor, request: true).Concat(Roots(descriptor, request: false)), null);
        foreach (var definition in definitions.ToArray()) if (!required.Contains(definition.Key)) definitions.Remove(definition.Key);
        return root;
    }

    private IEnumerable<SunderRpcMethodDescriptor> SelectedMethods(SunderRpcContractDescriptor descriptor)
        => Methods.Select(method => descriptor.FindService(method.ServiceId)?.FindMethod(method.MethodId)).OfType<SunderRpcMethodDescriptor>();

    private IEnumerable<string> Roots(SunderRpcContractDescriptor descriptor, bool request)
    {
        foreach (var method in SelectedMethods(descriptor))
        {
            yield return (request ? method.RequestSchemaReference : method.OutputSchemaReference)[8..];
            if (!request)
                foreach (var error in method.Errors)
                    if (descriptor.FindError(error)?.DetailsSchema is { } details) yield return details[8..];
        }
    }

    private static HashSet<string> Closure(SunderRpcContractDescriptor descriptor, IEnumerable<string> roots,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? views)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(roots);
        while (pending.TryDequeue(out var name))
        {
            if (!seen.Add(name)) continue;
            var schema = descriptor.Definitions[name];
            if (views is not null && views.TryGetValue(name, out var members))
                foreach (var member in members) Visit(schema.GetProperty("properties").GetProperty(member));
            else Visit(schema);
        }
        return seen;

        void Visit(JsonElement schema)
        {
            if (schema.TryGetProperty("$ref", out var reference)) { pending.Enqueue(reference.GetString()![8..]); return; }
            if (schema.TryGetProperty("properties", out var properties)) foreach (var property in properties.EnumerateObject()) Visit(property.Value);
            foreach (var keyword in new[] { "items", "additionalProperties", "propertyNames" })
                if (schema.TryGetProperty(keyword, out var child) && child.ValueKind == JsonValueKind.Object) Visit(child);
            if (schema.TryGetProperty("oneOf", out var variants)) foreach (var variant in variants.EnumerateArray()) Visit(variant);
        }
    }

    private static string Text(JsonElement value, string name) => SunderRpcModelProjectionJson.Text(value.GetProperty(name));
    private static string[] Strings(JsonElement value)
        => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256
            ? value.EnumerateArray().Select(SunderRpcModelProjectionJson.Text).ToArray()
            : throw new InvalidDataException("Consumer selector members must be bounded string arrays.");
}
