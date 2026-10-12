using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

// These snapshots are tooling evidence, not source identity inference or Registry DTOs.
// In particular an empty projection must be explicit; it is never a missing-metadata fallback.
internal sealed record SunderRpcServiceReferenceProjection
{
    internal SunderRpcServiceReferenceProjection(string contractId, string serviceId)
    {
        SunderRpcModelProjectionJson.RequireId(contractId);
        if (serviceId is not { Length: > 0 and <= 128 }
            || !IsEdge(serviceId[0]) || !IsEdge(serviceId[^1])
            || serviceId.Any(c => !IsEdge(c) && c is not ('.' or '-' or '_')))
            throw new InvalidDataException("Model projection serviceId must use the descriptor member-ID grammar.");
        ContractId = contractId;
        ServiceId = serviceId;
    }

    public string ContractId { get; }
    public string ServiceId { get; }

    internal JsonObject ToJson() => new() { ["contractId"] = ContractId, ["serviceId"] = ServiceId };
    private static bool IsEdge(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}

internal sealed partial record SunderRpcModelProjection
{
    // A projection is already a public identifier, so neither backend may repair it
    // by escaping a keyword or silently choosing another spelling.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "abstract", "any", "as", "await", "base", "bigint", "bool", "boolean", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "constructor", "continue", "debugger", "decimal", "declare", "default",
        "delegate", "delete", "do", "double", "else", "enum", "event", "explicit", "export", "extends", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "function", "global", "goto", "if", "implements",
        "implicit", "import", "in", "instanceof", "int", "interface", "internal", "is", "let", "lock", "long",
        "module", "namespace", "never", "new", "null", "number", "object", "operator", "out", "override", "package",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "super", "switch", "symbol", "this", "throw", "true", "try",
        "type", "typeof", "uint", "ulong", "unchecked", "undefined", "unknown", "unsafe", "ushort", "using", "var",
        "virtual", "void", "volatile", "while", "with", "yield",
    };

    internal SunderRpcModelProjection(IReadOnlyDictionary<string, string> models,
        IReadOnlyDictionary<string, SunderRpcServiceReferenceProjection> serviceReferences,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? optionalServiceReferenceMembers = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(serviceReferences);
        if (models.Count > 256 || serviceReferences.Count > 256 || models.Count + serviceReferences.Count > 256)
            throw new InvalidDataException("Model projection maps exceed the definition limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modelCopy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, name) in models)
        {
            RequireKey(key, names);
            if (!IsPortableModelName(name))
                throw new InvalidDataException("Model projection names must be portable identifiers.");
            if (modelNames.TryGetValue(name, out var existing) && name != existing)
                throw new InvalidDataException("Model projection names cannot collide under ASCII case folding.");
            modelNames[name] = name;
            modelCopy.Add(key, name);
        }
        var referenceCopy = new SortedDictionary<string, SunderRpcServiceReferenceProjection>(StringComparer.Ordinal);
        foreach (var (key, reference) in serviceReferences)
        {
            RequireKey(key, names);
            if (reference is null) throw new InvalidDataException("Model projection service references cannot be null.");
            referenceCopy.Add(key, reference);
        }
        Models = new ReadOnlyDictionary<string, string>(modelCopy);
        ServiceReferences = new ReadOnlyDictionary<string, SunderRpcServiceReferenceProjection>(referenceCopy);
        OptionalServiceReferenceMembers = SnapshotOptionalMembers(optionalServiceReferenceMembers);
    }

    public IReadOnlyDictionary<string, string> Models { get; }
    public IReadOnlyDictionary<string, SunderRpcServiceReferenceProjection> ServiceReferences { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> OptionalServiceReferenceMembers { get; }
    internal static SunderRpcModelProjection Empty { get; } = new(
        new Dictionary<string, string>(), new Dictionary<string, SunderRpcServiceReferenceProjection>());

    internal static bool IsPortableModelName(string? name)
        => name is { Length: > 0 and <= 128 } && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !ReservedNames.Contains(name);

    internal static SunderRpcModelProjection Parse(JsonElement value)
    {
        var hasOptionalMembers = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("optionalServiceReferenceMembers", out _);
        SunderRpcAuthoringJson.RequireObject(value, hasOptionalMembers
            ? ["models", "serviceReferences", "optionalServiceReferenceMembers"]
            : ["models", "serviceReferences"]);
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in SunderRpcModelProjectionJson.OrderedMap(value.GetProperty("models")))
            models.Add(property.Name, SunderRpcModelProjectionJson.Text(property.Value));
        var references = new Dictionary<string, SunderRpcServiceReferenceProjection>(StringComparer.Ordinal);
        foreach (var property in SunderRpcModelProjectionJson.OrderedMap(value.GetProperty("serviceReferences")))
        {
            SunderRpcAuthoringJson.RequireObject(property.Value, "contractId", "serviceId");
            references.Add(property.Name, new(SunderRpcModelProjectionJson.Text(property.Value.GetProperty("contractId")),
                SunderRpcModelProjectionJson.Text(property.Value.GetProperty("serviceId"))));
        }
        return new(models, references, hasOptionalMembers ? ParseOptionalMembers(value.GetProperty("optionalServiceReferenceMembers")) : null);
    }

    internal JsonObject ToJson()
    {
        var models = new JsonObject();
        foreach (var (key, name) in Models) models.Add(key, JsonValue.Create(name));
        var references = new JsonObject();
        foreach (var (key, reference) in ServiceReferences) references.Add(key, reference.ToJson());
        var result = new JsonObject { ["models"] = models, ["serviceReferences"] = references };
        if (OptionalServiceReferenceMembers.Count != 0)
        {
            var owners = new JsonObject();
            foreach (var (owner, members) in OptionalServiceReferenceMembers)
                owners.Add(owner, new JsonArray(members.Select(member => (JsonNode?)JsonValue.Create(member)).ToArray()));
            result.Add("optionalServiceReferenceMembers", owners);
        }
        return result;
    }

    internal byte[] ToCanonicalUtf8() => SunderRpcModelProjectionJson.Write(ToJson());

    internal void Validate(SunderRpcContractDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Models.Count + ServiceReferences.Count > descriptor.Definitions.Count
            || Models.Keys.Concat(ServiceReferences.Keys).Any(key => !descriptor.Definitions.ContainsKey(key)))
            throw new InvalidDataException("Model projection keys must name disjoint definitions in the exact descriptor.");
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(descriptor.Services.SelectMany(s => s.Methods)
            .SelectMany(m => new[] { m.RequestSchemaReference, m.OutputSchemaReference })
            .Concat(descriptor.Errors.Values.Select(e => e.DetailsSchema).OfType<string>()));
        while (pending.TryDequeue(out var reference))
        {
            var key = reference["#/$defs/".Length..];
            if (reachable.Add(key)) Visit(descriptor.Definitions[key]);
        }
        if (Models.Keys.Concat(ServiceReferences.Keys).Concat(OptionalServiceReferenceMembers.Keys).Any(key => !reachable.Contains(key)))
            throw new InvalidDataException("Model projection cannot expose definitions outside the contract's reachable value closure.");
        foreach (var key in ServiceReferences.Keys)
            SunderRpcServiceReferenceSchema.Validate(descriptor.Definitions[key]);
        ValidateOptionalMembers(descriptor);

        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out var target) && target.ValueKind == JsonValueKind.String)
                    pending.Enqueue(target.GetString()!);
                else foreach (var property in value.EnumerateObject()) Visit(property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item);
        }
    }

    private static void RequireKey(string key, HashSet<string> names)
    {
        if (key is not { Length: > 0 and <= 128 }
            || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || !names.Add(key))
            throw new InvalidDataException("Model projection keys must be bare, disjoint, non-case-colliding definition names.");
    }
}
