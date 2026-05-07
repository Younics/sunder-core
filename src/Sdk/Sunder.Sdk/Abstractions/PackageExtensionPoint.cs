namespace Sunder.Sdk.Abstractions;

public sealed record PackageExtensionPoint<TContribution>(string Id)
{
    public override string ToString() => Id;
}
