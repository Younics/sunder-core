using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal static class SunderRpcModelProjectionGraph
{
    // Also used on complete compiler owned/imported sets, before role filtering.
    // Authority keys must be supplied from validated selection evidence, never guessed from IDs.
    internal static void Validate(IReadOnlyList<SunderRpcModelProjectionContract> contracts,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptorsByContractId,
        IReadOnlyDictionary<string, string> authoritiesByContractId)
    {
        if (contracts.Count > 128) throw new InvalidDataException("Model projection graph exceeds the contract limit.");
        var entries = new Dictionary<string, SunderRpcModelProjectionContract>(StringComparer.Ordinal);
        foreach (var entry in contracts)
        {
            if (!entries.TryAdd(entry.ContractId, entry)
                || !descriptorsByContractId.TryGetValue(entry.ContractId, out var descriptor)
                || descriptor.ContractId != entry.ContractId || descriptor.Version != entry.ContractVersion
                || descriptor.Sha256 != entry.DescriptorSha256 || descriptor.AbiSha256 != entry.AbiSha256
                || !authoritiesByContractId.TryGetValue(entry.ContractId, out var authority) || string.IsNullOrEmpty(authority))
                throw new InvalidDataException("Model projection graph requires exact, unique descriptor and authority association.");
            entry.ModelProjection.Validate(descriptor);
            entry.MethodPolicies.Validate(descriptor);
        }
        foreach (var entry in contracts)
            foreach (var reference in entry.ModelProjection.ServiceReferences.Values)
                if (!entries.ContainsKey(reference.ContractId)
                    || descriptorsByContractId[reference.ContractId].FindService(reference.ServiceId) is null)
                    throw new InvalidDataException("Model projection service target must resolve exactly once in the complete validated projection set (public-only for a sidecar).");

        var models = new Dictionary<(string Authority, string Name), (Context Context, string Key)>();
        var portableNames = new Dictionary<(string Authority, string FoldedName), string>();
        var contexts = new List<Context>();
        var compared = new HashSet<(string LeftContract, string LeftKey, string RightContract, string RightKey)>();
        var steps = 0;
        long descriptorBytes = 0;
        try
        {
            foreach (var entry in contracts)
            {
                var bytes = descriptorsByContractId[entry.ContractId].GetCanonicalAbiProjectionUtf8();
                descriptorBytes += bytes.Length;
                if (descriptorBytes > 67_108_864)
                    throw new InvalidDataException("Model projection graph exceeds the generated-document materializability bound.");
                var context = new Context(entry, authoritiesByContractId[entry.ContractId],
                    JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 256 }));
                contexts.Add(context);
                foreach (var (key, name) in entry.ModelProjection.Models)
                {
                    var portableKey = (context.Authority, name.ToLowerInvariant());
                    if (portableNames.TryGetValue(portableKey, out var spelling) && spelling != name)
                        throw new InvalidDataException("Model projection has a cross-contract public-name case collision.");
                    portableNames[portableKey] = name;
                    if (models.TryGetValue((context.Authority, name), out var existing))
                    {
                        if (!Definition(existing.Context, existing.Key, context, key, 0))
                            throw new InvalidDataException($"Model projection identity '{context.Authority}/{name}' has incompatible normalized schema closures.");
                    }
                    else models.Add((context.Authority, name), (context, key));
                }
            }
        }
        finally { foreach (var context in contexts) context.Abi.Dispose(); }

        bool Definition(Context left, string leftKey, Context right, string rightKey, int depth)
        {
            // Coinductive comparison bounds shared/recursive closures without expanding them.
            if (!compared.Add((left.Entry.ContractId, leftKey, right.Entry.ContractId, rightKey))) return true;
            return Same(left, left.Definition(leftKey), right, right.Definition(rightKey), depth + 1);
        }

        bool Same(Context left, JsonElement a, Context right, JsonElement b, int depth)
        {
            if (++steps > 4_194_304 || depth > 256)
                throw new InvalidDataException("Model projection schema closure exceeds the analysis bound.");
            var ar = ResolveReference(left, a); var br = ResolveReference(right, b);
            if (ar is not null || br is not null)
            {
                var ai = Identity(left, ar); var bi = Identity(right, br);
                if (ai is not null || bi is not null) return ai == bi;
                if (ar is not null && br is not null) return Definition(left, ar, right, br, depth);
                return Same(left, ar is null ? a : left.Definitions.GetProperty(ar),
                    right, br is null ? b : right.Definitions.GetProperty(br), depth + 1);
            }
            // Match occurrence semantics as well as wire shape. Alias resolution above
            // preserves the defining record's policy, including borrowed nested records.
            // A projected child reference is compared by identity here and by its complete
            // definition in the model loop, so neither recursion nor aliasing drops policy.
            if (!left.OptionalMembers(a).SequenceEqual(right.OptionalMembers(b), StringComparer.Ordinal)) return false;
            if (a.ValueKind != b.ValueKind) return false;
            // ABI JSON normalizes annotations, enum/required sets, nullability and union tags.
            if (a.ValueKind == JsonValueKind.Object)
            {
                var ap = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                var bp = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                return ap.Length == bp.Length && ap.Zip(bp).All(pair => pair.First.Name == pair.Second.Name
                    && Same(left, pair.First.Value, right, pair.Second.Value, depth + 1));
            }
            if (a.ValueKind == JsonValueKind.Array)
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray())
                    .All(pair => Same(left, pair.First, right, pair.Second, depth + 1));
            return a.GetRawText() == b.GetRawText();
        }

        string? Identity(Context context, string? key)
        {
            if (key is null) return null;
            if (context.Entry.ModelProjection.Models.TryGetValue(key, out var model))
                return "model\0" + context.Authority + "\0" + model;
            if (context.Entry.ModelProjection.ServiceReferences.TryGetValue(key, out var service))
                return "service\0" + authoritiesByContractId[service.ContractId] + "\0" + service.ContractId + "\0" + service.ServiceId;
            return null;
        }

        string? ResolveReference(Context context, JsonElement schema)
        {
            var key = Reference(schema);
            if (key is null) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal) { key };
            while (Identity(context, key) is null && Reference(context.Definitions.GetProperty(key)) is { } alias)
            {
                if (++steps > 4_194_304 || !seen.Add(alias))
                    throw new InvalidDataException("Model projection schema aliases exceed the analysis bound or form a cycle.");
                key = alias;
            }
            return key;
        }
    }

    private static string? Reference(JsonElement value)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out var reference)
            && reference.ValueKind == JsonValueKind.String ? reference.GetString()!["#/$defs/".Length..] : null;

    private sealed class Context
    {
        private readonly Dictionary<JsonElement, IReadOnlyList<string>> _optionalMembers = new();

        internal Context(SunderRpcModelProjectionContract entry, string authority, JsonDocument abi)
        {
            Entry = entry; Authority = authority; Abi = abi;
            // JsonElement keys identify nodes in this retained ABI document, not structural
            // schema equality. Policy on one occurrence must never mark a lookalike record.
            foreach (var (owner, members) in entry.ModelProjection.OptionalServiceReferenceMembers)
                _optionalMembers.Add(Definitions.GetProperty(owner), members);
        }

        internal SunderRpcModelProjectionContract Entry { get; }
        internal string Authority { get; }
        internal JsonDocument Abi { get; }
        internal JsonElement Definitions => Abi.RootElement.GetProperty("$defs");
        internal IReadOnlyList<string> OptionalMembers(JsonElement schema)
            => _optionalMembers.TryGetValue(schema, out var members) ? members : Array.Empty<string>();

        internal JsonElement Definition(string key)
        {
            var schema = Definitions.GetProperty(key);
            Entry.ModelProjection.Models.TryGetValue(key, out var identity);
            var seen = new HashSet<string>(StringComparer.Ordinal) { key };
            while (Reference(schema) is { } target)
            {
                // Aliases of this same model are not a second occurrence identity. Keep
                // references to different projected models/services symbolic as before.
                if (Entry.ModelProjection.ServiceReferences.ContainsKey(target)
                    || Entry.ModelProjection.Models.TryGetValue(target, out var targetIdentity) && targetIdentity != identity) break;
                if (!seen.Add(target)) throw new InvalidDataException("Model projection schema alias is cyclic.");
                schema = Definitions.GetProperty(target);
            }
            return schema;
        }
    }
}
