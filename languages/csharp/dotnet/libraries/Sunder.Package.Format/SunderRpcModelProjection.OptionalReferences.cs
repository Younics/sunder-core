using System.Collections.ObjectModel;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal sealed partial record SunderRpcModelProjection
{
    internal const int MaximumOptionalServiceReferenceOwners = 256;
    internal const int MaximumOptionalServiceReferenceMembersPerOwner = 256;
    internal const int MaximumOptionalServiceReferenceMembers = 4096;

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotOptionalMembers(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? input)
    {
        if (input is { Count: > MaximumOptionalServiceReferenceOwners })
            throw new InvalidDataException("Optional service-reference policy exceeds 256 owner definitions.");
        var owners = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var ownerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var (owner, members) in input ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            // This map is independent of models/serviceReferences: borrowed record definitions
            // may carry occurrence policy without claiming ownership of an imported model.
            RequireKey(owner, ownerNames);
            if (members is null || members.Count is < 1 or > MaximumOptionalServiceReferenceMembersPerOwner
                || members.Count > MaximumOptionalServiceReferenceMembers - count)
                throw new InvalidDataException("Optional service-reference policy requires 1..256 members per owner and at most 4096 members total.");
            count += members.Count;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                RequireWireMemberName(member);
                if (!names.Add(FoldAscii(member)))
                    throw new InvalidDataException("Optional service-reference member names cannot duplicate or collide under ASCII case folding.");
            }
            owners.Add(owner, Array.AsReadOnly(members.Order(StringComparer.Ordinal).ToArray()));
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(owners);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseOptionalMembers(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetPropertyCount() == 0)
            throw new InvalidDataException("Optional service-reference policy must be a nonempty map when present; omit an empty policy.");
        var owners = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var owner in SunderRpcModelProjectionJson.OrderedMap(value))
        {
            if (owner.Value.ValueKind != JsonValueKind.Array
                || owner.Value.GetArrayLength() is < 1 or > MaximumOptionalServiceReferenceMembersPerOwner
                || owner.Value.GetArrayLength() > MaximumOptionalServiceReferenceMembers - count)
                throw new InvalidDataException("Optional service-reference policy members must be bounded nonempty arrays (4096 members total).");
            count += owner.Value.GetArrayLength();
            var members = new List<string>();
            string? previous = null;
            foreach (var item in owner.Value.EnumerateArray())
            {
                var member = SunderRpcModelProjectionJson.Text(item);
                RequireWireMemberName(member);
                if (previous is not null && string.CompareOrdinal(previous, member) >= 0)
                    throw new InvalidDataException("Optional service-reference member arrays must be unique and ordinally ordered.");
                previous = member;
                members.Add(member);
            }
            owners.Add(owner.Name, members);
        }
        return owners;
    }

    private void ValidateOptionalMembers(SunderRpcContractDescriptor descriptor)
    {
        foreach (var (owner, members) in OptionalServiceReferenceMembers)
        {
            if (!descriptor.Definitions.TryGetValue(owner, out var record)
                || !record.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object"
                || !record.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Optional service-reference policy owner must be a concrete record definition with direct properties.");
            var requiredNames = required.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!properties.TryGetProperty(member, out var property) || !requiredNames.Contains(member))
                    throw new InvalidDataException("Optional service-reference policy must name an existing direct wire-required property.");
                var nullable = ResolveAliases(property, descriptor, out _);
                if (!nullable.TryGetProperty("oneOf", out var alternatives) || alternatives.ValueKind != JsonValueKind.Array
                    || alternatives.GetArrayLength() != 2)
                    throw new InvalidDataException("Optional service-reference policy requires exactly null plus a projected service reference.");
                var nulls = 0;
                var references = 0;
                foreach (var alternative in alternatives.EnumerateArray())
                {
                    var resolved = ResolveAliases(alternative, descriptor, out var projectedReference);
                    if (resolved.TryGetProperty("type", out var branchType) && branchType.ValueKind == JsonValueKind.String
                        && branchType.GetString() == "null") nulls++;
                    else if (projectedReference is not null) references++;
                }
                if (nulls != 1 || references != 1)
                    throw new InvalidDataException("Optional service-reference property must resolve to a known serviceReferences definition, not a nullable DTO, container or locator lookalike.");
            }
        }
    }

    private JsonElement ResolveAliases(JsonElement value, SunderRpcContractDescriptor descriptor, out string? projectedReference)
    {
        projectedReference = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (value.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!["#/$defs/".Length..];
            if (!seen.Add(name) || !descriptor.Definitions.TryGetValue(name, out value))
                throw new InvalidDataException("Optional service-reference schema alias is cyclic or unresolved.");
            if (ServiceReferences.ContainsKey(name)) projectedReference = name;
        }
        return value;
    }

    private static void RequireWireMemberName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256)
            throw new InvalidDataException("Optional service-reference wire member names must contain 1..128 Unicode scalars.");
        var scalars = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (++scalars > 128) throw new InvalidDataException("Optional service-reference wire member names exceed 128 Unicode scalars.");
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length || !char.IsLowSurrogate(value[++index]))
                throw new InvalidDataException("Optional service-reference wire member names must contain Unicode scalar values.");
        }
    }

    private static string FoldAscii(string value) => string.Create(value.Length, value, static (result, source) =>
    {
        for (var index = 0; index < source.Length; index++)
            result[index] = source[index] is >= 'A' and <= 'Z' ? (char)(source[index] + ('a' - 'A')) : source[index];
    });
}
