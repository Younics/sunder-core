namespace Sunder.Package.Format;

/// <summary>Read-only declaration facts from an already validated manifest; not grants or live usage.</summary>
public sealed record SunderPackageInformation(
    IReadOnlyList<SunderPackageFeature> Features,
    IReadOnlyList<SunderPackageRelationship> Dependencies,
    IReadOnlyList<SunderPackagePublicBinding> PublicBindings,
    IReadOnlyList<SunderPackagePublicUse> PublicUses)
{
    public static SunderPackageInformation FromManifest(SunderPackageManifest manifest)
    {
        var bundles = (manifest.ContractBundles ?? []).OfType<SunderPackageContractBundleManifest>().ToArray();
        var providers = (manifest.Provides ?? []).OfType<SunderPackageProviderManifest>().ToArray();
        var uses = (manifest.UsesContracts ?? []).OfType<SunderPackageContractUseManifest>().ToArray();
        bool IsPublic(string? id, string? version) => bundles.Any(bundle => bundle.ContractId == id && bundle.Version == version && bundle.Exposure == "public");
        var publicUses = uses.Where(use => IsPublic(use.ContractId, use.ContractVersion)).Select(use =>
            new SunderPackagePublicUse(use.Role!, use.ContractId!, use.ContractVersion!, use.Required!.Value,
                (use.Actions ?? []).OfType<string>().Order(StringComparer.Ordinal).ToArray())).ToArray();
        var dependencies = (manifest.DependsOn ?? []).Select(dependency =>
        {
            var relationships = new List<SunderPackageIntegration>();
            foreach (var api in (manifest.PackageApiImports ?? []).OfType<SunderPackageApiImportManifest>().Where(api => api.PackageId == dependency.PackageId))
                foreach (var reference in (api.UseRefs ?? []).OfType<SunderPackageContractUseReferenceManifest>())
                    if (IsPublic(reference.ContractId, reference.ContractVersion)
                        && uses.Any(use => use.Role == reference.Role && use.ContractId == reference.ContractId && use.ContractVersion == reference.ContractVersion))
                        relationships.Add(new(reference.Role!, reference.ContractId!, reference.ContractVersion!, "uses"));
            foreach (var import in (manifest.ProviderImports ?? []).OfType<SunderPackageProviderImportManifest>().Where(import => import.PackageId == dependency.PackageId))
                foreach (var reference in (import.ProviderRefs ?? []).OfType<SunderPackageProviderReferenceManifest>())
                    if (IsPublic(reference.ContractId, reference.ContractVersion)
                        && providers.Any(provider => provider.Role == reference.Role && provider.ProviderId == reference.ProviderId
                            && provider.ContractId == reference.ContractId && provider.ContractVersion == reference.ContractVersion && provider.Audience == "public"))
                        relationships.Add(new(reference.Role!, reference.ContractId!, reference.ContractVersion!, "implements"));
            return new SunderPackageRelationship(dependency.PackageId!, dependency.VersionRange!, dependency.Presence!,
                relationships.Distinct().OrderBy(value => value.Kind, StringComparer.Ordinal).ThenBy(value => value.ContractId, StringComparer.Ordinal)
                    .ThenBy(value => value.Role, StringComparer.Ordinal).ToArray());
        }).OrderBy(value => value.Presence == "required" ? 0 : 1).ThenBy(value => value.PackageId, StringComparer.Ordinal).ToArray();
        var bindings = bundles.Where(bundle => bundle.Exposure == "public").Select(bundle =>
        {
            var implementations = providers.Where(provider => provider.ContractId == bundle.ContractId && provider.ContractVersion == bundle.Version).ToArray();
            var exposed = implementations.Where(provider => provider.Audience == "public")
                .Select(provider => new SunderPackagePublicProvider(provider.Role!, provider.ProviderId!))
                .OrderBy(value => value.Role, StringComparer.Ordinal).ThenBy(value => value.ProviderId, StringComparer.Ordinal).ToArray();
            // Imported definitions are not implementations. A public definition
            // narrowed to owner-only providers is not an external service either.
            return exposed.Length > 0 ? new SunderPackagePublicBinding(bundle.ContractId!, bundle.Version!, "service", exposed)
                : bundle.Source == "owned" && implementations.Length == 0
                    ? new SunderPackagePublicBinding(bundle.ContractId!, bundle.Version!, "extension", []) : null;
        }).OfType<SunderPackagePublicBinding>().OrderBy(value => value.ContractId, StringComparer.Ordinal).ThenBy(value => value.Version, StringComparer.Ordinal).ToArray();
        var features = (manifest.Targets ?? []).OfType<SunderPackageTargetManifest>()
            .SelectMany(target => (target.RequiredHostCapabilities ?? []).OfType<string>()
                .Select(capability => new SunderPackageFeature(target.Role!, target.Rid!, capability)))
            .Distinct().OrderBy(value => value.Capability, StringComparer.Ordinal).ThenBy(value => value.Role, StringComparer.Ordinal)
            .ThenBy(value => value.Rid, StringComparer.Ordinal).ToArray();
        return new(features, dependencies, bindings, publicUses);
    }
}

public sealed record SunderPackageFeature(string Role, string Rid, string Capability);
public sealed record SunderPackageIntegration(string Role, string ContractId, string Version, string Kind);
public sealed record SunderPackageRelationship(string PackageId, string VersionRange, string Presence, IReadOnlyList<SunderPackageIntegration> Bindings);
public sealed record SunderPackagePublicProvider(string Role, string ProviderId);
public sealed record SunderPackagePublicBinding(string ContractId, string Version, string Kind, IReadOnlyList<SunderPackagePublicProvider> Providers);
public sealed record SunderPackagePublicUse(string Role, string ContractId, string Version, bool Required, IReadOnlyList<string> Actions);
