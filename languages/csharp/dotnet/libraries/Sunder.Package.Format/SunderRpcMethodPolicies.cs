using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

// An ordinary call hint; the host still owns the effective deadline and clamps it.
// This metadata is not part of the RPC descriptor, ABI, or authorization policy.
internal sealed record SunderRpcMethodPolicy
{
    internal SunderRpcMethodPolicy(int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > SunderRpcMethodPolicies.MaximumTimeoutSeconds)
            throw new InvalidDataException("RPC method timeoutSeconds must be an integer in 1..2100; omit the policy to use the host default.");
        TimeoutSeconds = timeoutSeconds;
    }

    public int TimeoutSeconds { get; }

    internal JsonObject ToJson() => new() { ["timeoutSeconds"] = TimeoutSeconds };
}

internal sealed record SunderRpcMethodPolicies
{
    // Match the descriptor parser: 64 services, 512 methods across the whole contract.
    internal const int MaximumServices = 64;
    internal const int MaximumMethods = 512;
    internal const int MaximumTimeoutSeconds = 2100;

    internal SunderRpcMethodPolicies(IReadOnlyDictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Count > MaximumServices)
            throw new InvalidDataException("RPC method policies exceed the descriptor's 64-service limit.");
        var copy = new SortedDictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>>(StringComparer.Ordinal);
        var totalMethods = 0;
        foreach (var (serviceId, methods) in services)
        {
            RequireMemberId(serviceId);
            if (methods is null || methods.Count == 0 || methods.Count > MaximumMethods - totalMethods)
                throw new InvalidDataException("RPC method policies require nonempty service maps and at most 512 methods total.");
            totalMethods += methods.Count;
            var methodCopy = new SortedDictionary<string, SunderRpcMethodPolicy>(StringComparer.Ordinal);
            foreach (var (methodId, policy) in methods)
            {
                RequireMemberId(methodId);
                if (policy is null) throw new InvalidDataException("RPC method policy cannot be null.");
                methodCopy.Add(methodId, policy);
            }
            copy.Add(serviceId, new ReadOnlyDictionary<string, SunderRpcMethodPolicy>(methodCopy));
        }
        Services = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>>(copy);
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>> Services { get; }
    public bool IsEmpty => Services.Count == 0;
    internal static SunderRpcMethodPolicies Empty { get; } = new(
        new Dictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>>());

    // Called only when the optional member is present. Absence is represented by Empty,
    // not by an explicit empty object, null, or a zero timeout entry.
    internal static SunderRpcMethodPolicies Parse(JsonElement value)
    {
        var services = new Dictionary<string, IReadOnlyDictionary<string, SunderRpcMethodPolicy>>(StringComparer.Ordinal);
        var totalMethods = 0;
        foreach (var service in OrderedMap(value, MaximumServices))
        {
            var methods = new Dictionary<string, SunderRpcMethodPolicy>(StringComparer.Ordinal);
            foreach (var method in OrderedMap(service.Value, MaximumMethods - totalMethods))
            {
                SunderRpcAuthoringJson.RequireObject(method.Value, "timeoutSeconds");
                var timeout = method.Value.GetProperty("timeoutSeconds");
                if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var seconds)
                    || timeout.GetRawText() != seconds.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("RPC method timeoutSeconds must be a canonical integer.");
                methods.Add(method.Name, new(seconds));
            }
            totalMethods += methods.Count;
            services.Add(service.Name, methods);
        }
        return new(services);
    }

    internal JsonObject ToJson()
    {
        var services = new JsonObject();
        foreach (var (serviceId, policies) in Services)
        {
            var methods = new JsonObject();
            foreach (var (methodId, policy) in policies) methods.Add(methodId, policy.ToJson());
            services.Add(serviceId, methods);
        }
        return services;
    }

    internal byte[] ToCanonicalUtf8() => SunderRpcModelProjectionJson.Write(ToJson());

    internal void Validate(SunderRpcContractDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        foreach (var (serviceId, methods) in Services)
        {
            var service = descriptor.FindService(serviceId);
            if (service is null || methods.Keys.Any(methodId => service.FindMethod(methodId) is null))
                throw new InvalidDataException("RPC method policies must reference services and methods declared by the exact contract descriptor.");
        }
    }

    private static IEnumerable<JsonProperty> OrderedMap(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetPropertyCount() == 0 || value.GetPropertyCount() > maximum)
            throw new InvalidDataException("Present RPC method policy maps must be nonempty bounded objects; omit empty policies.");
        string? previous = null;
        foreach (var property in value.EnumerateObject())
        {
            RequireMemberId(property.Name);
            if (previous is not null && string.CompareOrdinal(previous, property.Name) >= 0)
                throw new InvalidDataException("RPC method policy service and method keys must be unique and ordinally ordered.");
            previous = property.Name;
            yield return property;
        }
    }

    private static void RequireMemberId(string value)
    {
        if (value is not { Length: > 0 and <= 128 } || !IsEdge(value[0]) || !IsEdge(value[^1])
            || value.Any(c => !IsEdge(c) && c is not ('.' or '-' or '_')))
            throw new InvalidDataException("RPC method policy keys must use the descriptor member-ID grammar (1..128 characters).");
    }

    private static bool IsEdge(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
