using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class PackageCatalogInstallationIndex(
    IReadOnlyList<InstalledPackageDescriptor> installedPackages,
    IReadOnlyList<RegistryPackageUpdate> availableUpdates)
{
    private readonly IReadOnlyDictionary<string, InstalledPackageDescriptor> _installedById =
        installedPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, RegistryPackageUpdate> _updatesById =
        availableUpdates.ToDictionary(update => update.PackageId, StringComparer.OrdinalIgnoreCase);

    public InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedById.GetValueOrDefault(packageId);

    public RegistryPackageUpdate? GetUpdate(string packageId)
        => _updatesById.GetValueOrDefault(packageId);
}
