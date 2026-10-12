using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcOwnedUsePolicy(bool Required, IReadOnlyList<string> Actions);

internal static partial class SunderRpcAuthoringDocuments
{
    internal static SunderRpcOwnedUsePolicy? ResolveAppUse(SunderServiceExposure exposure, SunderRpcContractDescriptor descriptor, SunderRpcOwnedUsePolicy? policy)
        => policy ?? (exposure == SunderServiceExposure.Package ? new(true, ReceiptActions(descriptor).ToArray()) : null);

    internal static SunderRpcOwnedUsePolicy ValidateUsePolicy(SunderRpcContractDescriptor descriptor, bool required, IReadOnlyList<string>? actions, string role)
    {
        var applicable = ReceiptActions(descriptor).ToArray();
        if (actions is null) return new(required, applicable);
        if (actions.Count is < 1 or > 3 || actions.Distinct(StringComparer.Ordinal).Count() != actions.Count
            || actions.Any(action => !applicable.Contains(action, StringComparer.Ordinal)))
            throw new InvalidDataException($"rpc-authoring.{role}-use-invalid: {role} actions must be a nonempty, distinct subset of the descriptor's applicable discover/invoke/subscribe actions.");
        return new(required, applicable.Where(action => actions.Contains(action, StringComparer.Ordinal)).ToArray());
    }

    internal static bool SameUsePolicy(SunderRpcOwnedUsePolicy? left, SunderRpcOwnedUsePolicy? right)
        => left is null ? right is null : right is not null && left.Required == right.Required && left.Actions.SequenceEqual(right.Actions, StringComparer.Ordinal);

    private static SunderRpcOwnedUsePolicy? ReadUsePolicy(JsonElement value, SunderRpcContractDescriptor descriptor, string role)
    {
        if (!value.TryGetProperty(role + "Use", out var policy)) return null;
        var label = role + " use policy";
        RequireObject(policy, label, ["required", "actions"], ["required", "actions"], []);
        var required = RequireBoolean(policy, "required", label);
        var actions = RequireProperty(policy, "actions", JsonValueKind.Array, label).EnumerateArray()
            .Select(action => action.ValueKind == JsonValueKind.String ? action.GetString()! : throw new InvalidDataException($"rpc-authoring.{role}-use-invalid: Actions must be strings.")).ToArray();
        var result = ValidateUsePolicy(descriptor, required, actions, role);
        if (!actions.SequenceEqual(result.Actions, StringComparer.Ordinal)) throw new InvalidDataException($"rpc-authoring.{role}-use-invalid: Actions must use canonical order.");
        return result;
    }

    private static void UsePolicyProperty(StringBuilder output, int indent, string role, SunderRpcOwnedUsePolicy? policy, bool comma = false)
    {
        if (policy is null) return;
        Line(output, indent, "\"" + role + "Use\": {");
        Line(output, indent + 1, "\"required\": " + (policy.Required ? "true," : "false,"));
        Line(output, indent + 1, "\"actions\": [");
        for (var index = 0; index < policy.Actions.Count; index++)
            Line(output, indent + 2, JsonSerializer.Serialize(policy.Actions[index]) + (index + 1 == policy.Actions.Count ? "" : ","));
        Line(output, indent + 1, "]");
        Line(output, indent, comma ? "}," : "}");
    }
}
