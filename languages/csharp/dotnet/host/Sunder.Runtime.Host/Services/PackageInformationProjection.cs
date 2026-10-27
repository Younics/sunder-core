using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class PackageInformationProjection
{
    internal static PackageInformationDescriptor? FromSource(RuntimePackageSource source)
    {
        if (source.Manifest is null || source.ContentIdentity is null) return null;
        var facts = SunderPackageInformation.FromManifest(source.Manifest);
        return new(source.PackageId, source.Manifest.Version!, source.ContentIdentity, source.Kind,
            facts.Features.Where(feature => InstalledPackageProjection.Includes(source.AcquiredTargets, feature.Role, feature.Rid))
                .Select(value => new PackageFeatureDescriptor(value.Role, value.Rid, value.Capability)).ToArray(),
            facts.Dependencies.Select(value => new PackageRelationshipDescriptor(value.PackageId, value.VersionRange, value.Presence,
                value.Bindings.Select(binding => new PackageIntegrationDescriptor(binding.Role, binding.ContractId, binding.Version, binding.Kind)).ToArray())).ToArray(),
            facts.PublicBindings.Select(value => new PackagePublicBindingDescriptor(value.ContractId, value.Version, value.Kind,
                value.Providers.Select(provider => new PackagePublicProviderDescriptor(provider.Role, provider.ProviderId)).ToArray())).ToArray(),
            facts.PublicUses.Select(value => new PackagePublicUseDescriptor(value.Role, value.ContractId, value.Version, value.Required, value.Actions)).ToArray())
        { Summary = source.Manifest.Summary };
    }
}

internal sealed class PackageInformationService(RuntimeSessionOwner sessions, InstalledPackageStore installedPackages)
{
    public async Task<PackageInformationDescriptor?> GetAsync(string packageId, CancellationToken cancellationToken)
    {
        // Capture the exact current source under the session lock, including
        // disabled/faulted overlays. Never substitute installed metadata for it.
        var current = sessions.State.GetPackageInformation(packageId);
        if (current.HasSource) return current.Information;
        var installed = await installedPackages.GetAsync(packageId, cancellationToken);
        if (installed is null) return null;
        _ = installedPackages.ToDescriptor(installed); // retain normal installed-content validation
        var source = await PackageCompatibilityAuthority.ReadSourceAsync(installed, cancellationToken);
        return PackageInformationProjection.FromSource(source with { AcquiredTargets = InstalledPackageProjection.Keys(installed) });
    }
}
