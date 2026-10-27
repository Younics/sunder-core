namespace Sunder.Registry.Contracts;

/// <summary>Archive-derived declaration facts for an immutable package version, not observed usage.</summary>
public sealed record RegistryPackageInformation(
    string PackageContentIdentity,
    IReadOnlyList<RegistryPackageFeature> Features,
    IReadOnlyList<RegistryPackageRelationship> Dependencies,
    IReadOnlyList<RegistryPackagePublicBinding> PublicBindings,
    IReadOnlyList<RegistryPackagePublicUse> PublicUses)
{
    public IReadOnlyList<RegistryPackageFeature> Features { get; } = PolicyCollections.FreezeBounded(Features, 4096);
    public IReadOnlyList<RegistryPackageRelationship> Dependencies { get; } = PolicyCollections.FreezeBounded(Dependencies, 128);
    public IReadOnlyList<RegistryPackagePublicBinding> PublicBindings { get; } = PolicyCollections.FreezeBounded(PublicBindings, 128);
    public IReadOnlyList<RegistryPackagePublicUse> PublicUses { get; } = PolicyCollections.FreezeBounded(PublicUses, 128);
}
public sealed record RegistryPackageFeature(string Role, string Rid, string Capability);
public sealed record RegistryPackageIntegration(string Role, string ContractId, string Version, string Kind);
public sealed record RegistryPackageRelationship(string PackageId, string VersionRange, string Presence, IReadOnlyList<RegistryPackageIntegration> Bindings)
{
    public IReadOnlyList<RegistryPackageIntegration> Bindings { get; } = PolicyCollections.FreezeBounded(Bindings, 256);
}
public sealed record RegistryPackagePublicProvider(string Role, string ProviderId);
public sealed record RegistryPackagePublicBinding(string ContractId, string Version, string Kind, IReadOnlyList<RegistryPackagePublicProvider> Providers)
{
    public IReadOnlyList<RegistryPackagePublicProvider> Providers { get; } = PolicyCollections.FreezeBounded(Providers, 128);
}
public sealed record RegistryPackagePublicUse(string Role, string ContractId, string Version, bool Required, IReadOnlyList<string> Actions)
{
    public IReadOnlyList<string> Actions { get; } = PolicyCollections.FreezeBounded(Actions, 3);
}
