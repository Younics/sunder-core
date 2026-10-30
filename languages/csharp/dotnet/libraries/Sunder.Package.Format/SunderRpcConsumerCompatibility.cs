using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal static class SunderRpcConsumerCompatibility
{
    internal static bool Matches(SunderRpcContractDescriptor source, SunderRpcContractDescriptor provider,
        JsonElement? selectors)
    {
        if (source.ContractId != provider.ContractId) return false;
        if (selectors is null) return source.Sha256 == provider.Sha256 && source.AbiSha256 == provider.AbiSha256;
        var requirements = SunderRpcConsumerRequirements.Parse(selectors.Value, source);
        return SunderRpcContractCompatibility.Satisfies(requirements, provider).Kind != SunderRpcContractChangeKind.Major;
    }

    internal static bool AllowsMethod(SunderPackageContractUseManifest? use, SunderRpcContractDescriptor source,
        string serviceId, string methodId)
        => use?.ConsumerRequirements is not { } selectors
            || SunderRpcConsumerRequirements.Parse(selectors, source).Methods.Contains(new(serviceId, methodId));

    internal static bool MatchesProjection(SunderRpcConsumerRequirements requirements,
        SunderRpcModelProjection source, SunderRpcModelProjection provider)
    {
        var requests = requirements.RequiredRequestDefinitions;
        foreach (var name in requirements.RequiredDefinitions)
        {
            source.ServiceReferences.TryGetValue(name, out var expected);
            provider.ServiceReferences.TryGetValue(name, out var actual);
            if (expected != actual) return false;
            var oldMembers = source.OptionalServiceReferenceMembers.GetValueOrDefault(name) ?? [];
            var newMembers = provider.OptionalServiceReferenceMembers.GetValueOrDefault(name) ?? [];
            var known = requirements.OutputMembers.TryGetValue(name, out var view) && !requests.Contains(name) ? view
                : requirements.Source.Definitions[name].TryGetProperty("properties", out var properties)
                    ? properties.EnumerateObject().Select(property => property.Name).ToArray() : [];
            if (!oldMembers.Where(member => known.Contains(member, StringComparer.Ordinal))
                .SequenceEqual(newMembers.Where(member => known.Contains(member, StringComparer.Ordinal)), StringComparer.Ordinal)) return false;
        }
        return true;
    }

    internal static IReadOnlyList<SunderRpcConsumerRequirements> CloseServiceReferences(
        IReadOnlyList<SunderRpcConsumerRequirements> requirements,
        IReadOnlyDictionary<string, SunderRpcModelProjection> projections)
    {
        var result = requirements.ToDictionary(value => value.Source.ContractId, StringComparer.Ordinal);
        var pending = new Queue<string>(result.Keys);
        while (pending.TryDequeue(out var id))
        {
            var requirement = result[id];
            foreach (var name in requirement.RequiredDefinitions)
            {
                if (!projections[id].ServiceReferences.TryGetValue(name, out var reference) || !result.TryGetValue(reference.ContractId, out var target)) continue;
                if (target.OutputMembers.Count != 0) throw new InvalidDataException("A referenced typed service requires full response models, not a raw view.");
                var service = target.Source.FindService(reference.ServiceId) ?? throw new InvalidDataException("Required service reference has no selected target service.");
                var methods = target.Methods.Union(service.Methods.Select(method => new SunderRpcRequiredMethod(reference.ServiceId, method.MethodId))).ToArray();
                if (methods.Length == target.Methods.Count) continue;
                result[reference.ContractId] = new(target.Source, methods, target.OutputMembers, target.ExactOutputDefinitions);
                pending.Enqueue(reference.ContractId);
            }
        }
        return result.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
    }
}
