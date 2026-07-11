using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Identifies a typed package-to-package contribution contract.</summary>
/// <param name="Id">Stable globally unique id, conventionally scoped by the host package id.</param>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public sealed record PackageExtensionPoint<TContribution>(string Id)
{
    /// <summary>Returns the stable extension-point id.</summary>
    public override string ToString() => Id;
}
