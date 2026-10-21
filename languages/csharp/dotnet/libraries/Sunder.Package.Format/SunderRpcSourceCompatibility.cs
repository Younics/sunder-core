using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

// Exported-source policy over validated descriptor/projection snapshots. Callers own
// evidence authentication and version allocation. This is never a wire compatibility
// proof, a source-symbol inference rule, or a runtime substitution rule.
internal static class SunderRpcSourceCompatibility
{
    internal static SunderRpcContractChangeKind Classify(
        SunderRpcContractDescriptor previous, SunderRpcModelProjection previousProjection, SunderRpcMethodPolicies previousPolicies,
        SunderRpcContractDescriptor candidate, SunderRpcModelProjection projection, SunderRpcMethodPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(previousProjection);
        ArgumentNullException.ThrowIfNull(previousPolicies);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(policies);
        var shape = ClassifyDescriptorSourceShape(previous, candidate);
        if (shape == SunderRpcContractChangeKind.Major) return shape;
        if (previousProjection.ToCanonicalUtf8().AsSpan().SequenceEqual(projection.ToCanonicalUtf8())
            && previousPolicies.ToCanonicalUtf8().AsSpan().SequenceEqual(policies.ToCanonicalUtf8()))
            return shape;
        if (previousProjection.Models.Any(pair => !projection.Models.TryGetValue(pair.Key, out var name) || name != pair.Value)
            || previousProjection.ServiceReferences.Any(pair => !projection.ServiceReferences.TryGetValue(pair.Key, out var target) || target != pair.Value)
            || projection.Models.Keys.Any(key => !previousProjection.Models.ContainsKey(key) && previous.Definitions.ContainsKey(key))
            || projection.ServiceReferences.Keys.Any(key => !previousProjection.ServiceReferences.ContainsKey(key) && previous.Definitions.ContainsKey(key))
            || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(previousProjection.OptionalServiceReferenceMembers), JsonSerializer.SerializeToElement(projection.OptionalServiceReferenceMembers)))
            return SunderRpcContractChangeKind.Major;
        foreach (var service in previous.Services)
            foreach (var method in service.Methods)
            {
                previousPolicies.Services.TryGetValue(service.ServiceId, out var oldMethods);
                policies.Services.TryGetValue(service.ServiceId, out var newMethods);
                var oldPolicy = oldMethods?.GetValueOrDefault(method.MethodId);
                var newPolicy = newMethods?.GetValueOrDefault(method.MethodId);
                if (oldPolicy != newPolicy) return SunderRpcContractChangeKind.Major;
            }
        return SunderRpcContractChangeKind.Minor; // Only additive declarations remain; existing provider obligations are unchanged.
    }

    private static SunderRpcContractChangeKind ClassifyDescriptorSourceShape(SunderRpcContractDescriptor previous, SunderRpcContractDescriptor candidate)
    {
        // Wire containment is not source assignability. A new method/service adds
        // mandatory provider members; optional output members can still be read in
        // source. Use exact normalized shapes until a narrower two-sided proof exists.
        var beforeBytes = previous.GetCanonicalAbiProjectionUtf8();
        var afterBytes = candidate.GetCanonicalAbiProjectionUtf8();
        if (beforeBytes.AsSpan().SequenceEqual(afterBytes)) return SunderRpcContractChangeKind.Unchanged;
        // Parser-bounded; compare definitions once without expanding recursive refs.
        // This includes indirect, anonymous and inline generated value shapes.
        using var beforeDocument = JsonDocument.Parse(beforeBytes, new JsonDocumentOptions { MaxDepth = 256 });
        using var afterDocument = JsonDocument.Parse(afterBytes, new JsonDocumentOptions { MaxDepth = 256 });
        var before = beforeDocument.RootElement;
        var after = afterDocument.RootElement;
        if (previous.ContractId != candidate.ContractId
            || previous.DescriptorVersion != candidate.DescriptorVersion || previous.SchemaProfile != candidate.SchemaProfile
            || !JsonElement.DeepEquals(before.GetProperty("services"), after.GetProperty("services"))
            || !PreservesEntries(before.GetProperty("$defs"), after.GetProperty("$defs"))
            || !PreservesEntries(before.GetProperty("errors"), after.GetProperty("errors")))
            return SunderRpcContractChangeKind.Major;
        return SunderRpcContractChangeKind.Minor; // New definitions/error declarations without changing existing shapes.

        static bool PreservesEntries(JsonElement oldMap, JsonElement newMap)
            => oldMap.EnumerateObject().All(property => newMap.TryGetProperty(property.Name, out var value)
                && JsonElement.DeepEquals(property.Value, value));
    }
}
