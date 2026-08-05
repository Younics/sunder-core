using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal static class InstalledPackageCatalogProjector
{
    public static IReadOnlyList<PackageCatalogItemState> Build(
        IReadOnlyList<SessionPackageDescriptor> sessionPackages,
        IReadOnlyList<InstalledPackageDescriptor> installedPackages,
        IReadOnlyList<RegistryPackageUpdate> availableUpdates,
        string searchText,
        Func<string, PackageIconDescriptor?, Uri?> createPackageIconUri)
    {
        var sessionById = sessionPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var installationIndex = new PackageCatalogInstallationIndex(installedPackages, availableUpdates);
        var packageIds = sessionById.Keys.Concat(installedPackages.Select(package => package.PackageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var states = packageIds.Select(packageId =>
            {
                sessionById.TryGetValue(packageId, out var sessionPackage);
                var installedPackage = installationIndex.GetInstalledPackage(packageId);
                var update = installationIndex.GetUpdate(packageId);
                return PackageCatalogItemViewModel.CreateState(
                    sessionPackage,
                    installedPackage,
                    update,
                    createPackageIconUri(packageId, sessionPackage?.Icon ?? installedPackage?.Icon));
            }).ToArray();

        return PackageCatalogProjection.Project(
            states,
            state => new PackageCatalogSearchDocument(
                state.PackageId,
                state.DisplayName,
                state.Version,
                SourceLabel: state.SourceLabel),
            state => state,
            searchText,
            PackageCatalogSort.DisplayName);
    }
}
