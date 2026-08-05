namespace Sunder.Sdk.Compatibility;

/// <summary>Associates a public contract or member with a required V1 host capability.</summary>
[AttributeUsage(
    AttributeTargets.Class
    | AttributeTargets.Interface
    | AttributeTargets.Struct
    | AttributeTargets.Enum
    | AttributeTargets.Delegate
    | AttributeTargets.Method
    | AttributeTargets.Property
    | AttributeTargets.Field
    | AttributeTargets.Event,
    AllowMultiple = true,
    Inherited = false)]
public sealed class SunderSdkCapabilityAttribute(string capability) : Attribute
{
    /// <summary>Gets the lowercase capability identifier.</summary>
    public string Capability { get; } = capability;
}
