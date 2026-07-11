using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Callbacks;

/// <summary>Specifies how a callback flow is presented.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public enum PackageCallbackFlowKind
{
    /// <summary>The host opens an external browser and receives a callback.</summary>
    Browser = 0,
}
