namespace Sunder.Sdk.Compatibility;

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
    public string Capability { get; } = capability;
}
