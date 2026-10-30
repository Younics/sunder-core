using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Services;

/// <summary>Declares imported RPC methods reached through raw RPC, reflection or dynamic code.</summary>
/// <remarks>Uses wire IDs, not generated C# names. Raw output schemas remain exact; this is not an authorization grant.</remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[SunderSdkCapability(SunderSdkCapabilities.RpcConsumerRequirementsV1)]
public sealed class SunderRpcDynamicUseAttribute(string contractId, string serviceId, params string[] methodIds) : Attribute
{
    /// <summary>Gets the selected imported contract ID.</summary>
    public string ContractId { get; } = contractId;
    /// <summary>Gets the service's wire ID.</summary>
    public string ServiceId { get; } = serviceId;
    /// <summary>Gets the nonempty set of method wire IDs.</summary>
    public string[] MethodIds { get; } = methodIds;
}
