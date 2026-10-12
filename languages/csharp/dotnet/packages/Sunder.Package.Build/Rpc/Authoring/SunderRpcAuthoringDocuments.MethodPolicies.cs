using System.Text;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    internal static bool SameMethodPolicies(SunderRpcMethodPolicies left, SunderRpcMethodPolicies right)
        => left.ToCanonicalUtf8().AsSpan().SequenceEqual(right.ToCanonicalUtf8());

    private static SunderRpcMethodPolicies ReadMethodPolicies(JsonElement contract, SunderRpcContractDescriptor descriptor)
    {
        if (!contract.TryGetProperty("methodPolicies", out var value)) return SunderRpcMethodPolicies.Empty;
        var policies = SunderRpcMethodPolicies.Parse(value);
        if (policies.IsEmpty) throw new InvalidDataException("rpc-authoring.method-policies-invalid: Empty methodPolicies must be omitted.");
        policies.Validate(descriptor);
        return policies;
    }

    private static void MethodPoliciesProperty(StringBuilder output, SunderRpcMethodPolicies policies, bool comma)
    {
        if (policies.IsEmpty) return;
        var lines = Utf8NoBom.GetString(policies.ToCanonicalUtf8()).TrimEnd('\n').Split('\n');
        Line(output, 3, "\"methodPolicies\": " + lines[0]);
        for (var index = 1; index < lines.Length; index++)
            Line(output, 3, lines[index] + (index == lines.Length - 1 && comma ? "," : string.Empty));
    }
}
