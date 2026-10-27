namespace Sunder.Runtime.Contracts;

/// <summary>Selected-source declaration facts. Absence is unknown, not an empty declaration.</summary>
public sealed record PackageInformationDescriptor(
    string PackageId,
    string Version,
    string ContentIdentity,
    PackageSourceKind SourceKind,
    IReadOnlyList<PackageFeatureDescriptor> Features,
    IReadOnlyList<PackageRelationshipDescriptor> Dependencies,
    IReadOnlyList<PackagePublicBindingDescriptor> PublicBindings,
    IReadOnlyList<PackagePublicUseDescriptor> PublicUses)
{
    public string? Summary { get; init; }
    public IReadOnlyList<PackageFeatureDescriptor> Features { get; } = RuntimeContractCollections.FreezeBounded(Features, 4096);
    public IReadOnlyList<PackageRelationshipDescriptor> Dependencies { get; } = RuntimeContractCollections.FreezeBounded(Dependencies, 128);
    public IReadOnlyList<PackagePublicBindingDescriptor> PublicBindings { get; } = RuntimeContractCollections.FreezeBounded(PublicBindings, 128);
    public IReadOnlyList<PackagePublicUseDescriptor> PublicUses { get; } = RuntimeContractCollections.FreezeBounded(PublicUses, 128);
}

public sealed record PackageFeatureDescriptor(string Role, string Rid, string Capability);
public sealed record PackageIntegrationDescriptor(string Role, string ContractId, string Version, string Kind);
public sealed record PackageRelationshipDescriptor(string PackageId, string VersionRange, string Presence, IReadOnlyList<PackageIntegrationDescriptor> Bindings)
{
    public IReadOnlyList<PackageIntegrationDescriptor> Bindings { get; } = RuntimeContractCollections.FreezeBounded(Bindings, 256);
}
public sealed record PackagePublicProviderDescriptor(string Role, string ProviderId);
public sealed record PackagePublicBindingDescriptor(string ContractId, string Version, string Kind, IReadOnlyList<PackagePublicProviderDescriptor> Providers)
{
    public IReadOnlyList<PackagePublicProviderDescriptor> Providers { get; } = RuntimeContractCollections.FreezeBounded(Providers, 128);
}
public sealed record PackagePublicUseDescriptor(string Role, string ContractId, string Version, bool Required, IReadOnlyList<string> Actions)
{
    public IReadOnlyList<string> Actions { get; } = RuntimeContractCollections.FreezeBounded(Actions, 3);
}
