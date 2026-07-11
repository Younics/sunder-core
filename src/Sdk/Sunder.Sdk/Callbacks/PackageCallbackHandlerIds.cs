using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Callbacks;

/// <summary>Defines host-reserved callback handler identifiers.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public static class PackageCallbackHandlerIds
{
    /// <summary>Identifies the standard package authorization callback handler.</summary>
    public const string Authentication = "auth";
}
