using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Services;

/// <summary>Declares the wire members read by a deliberately narrow raw-RPC response model.</summary>
/// <remarks>Requires an explicit dynamic-use declaration. Generated full-model calls cannot use a narrowed definition. Nested views are declared separately.</remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[SunderSdkCapability(SunderSdkCapabilities.RpcConsumerRequirementsV1)]
public sealed class SunderRpcConsumerViewAttribute(string contractId, string definition, params string[] members) : Attribute
{
    /// <summary>Gets the imported contract ID.</summary>
    public string ContractId { get; } = contractId;
    /// <summary>Gets the response definition's wire name.</summary>
    public string Definition { get; } = definition;
    /// <summary>Gets the complete set of wire members the local model may read.</summary>
    public string[] Members { get; } = members;
}
