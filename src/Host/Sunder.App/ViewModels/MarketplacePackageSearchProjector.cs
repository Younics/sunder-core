using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal static class MarketplacePackageSearchProjector
{
    public static IReadOnlyList<RegistryPackageSearchItemViewModel> Build(
        IReadOnlyList<RegistryPackageSummary> packages,
        IReadOnlyList<InstalledPackageDescriptor> installedPackages,
        IReadOnlyList<RegistryPackageUpdate> availableUpdates,
        Func<RegistryPackageSearchItemViewModel, Task> selectPackageAsync)
    {
        var installationIndex = new PackageCatalogInstallationIndex(installedPackages, availableUpdates);

        return PackageCatalogProjection.Project(
            packages,
            package => new PackageCatalogSearchDocument(
                package.PackageId,
                package.Name,
                package.LatestVersion,
                package.Summary,
                "Marketplace package"),
            package => new RegistryPackageSearchItemViewModel(
                package,
                installationIndex.GetInstalledPackage(package.PackageId)?.Version,
                installationIndex.GetUpdate(package.PackageId),
                selectPackageAsync));
    }
}
