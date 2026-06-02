using Sunder.Protocol;
using Sunder.Registry.Shared;

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
        var installedById = installedPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var updatesById = availableUpdates.ToDictionary(update => update.PackageId, StringComparer.OrdinalIgnoreCase);
        var packageIds = sessionById.Keys.Concat(installedById.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase);

        return packageIds
            .Select(packageId =>
            {
                sessionById.TryGetValue(packageId, out var sessionPackage);
                installedById.TryGetValue(packageId, out var installedPackage);
                updatesById.TryGetValue(packageId, out var update);
                return PackageCatalogItemViewModel.CreateState(
                    sessionPackage,
                    installedPackage,
                    update,
                    createPackageIconUri(packageId, sessionPackage?.Icon ?? installedPackage?.Icon));
            })
            .Where(package => MatchesSearch(package, searchText))
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesSearch(PackageCatalogItemState package, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return package.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || package.PackageId.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || package.Version.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || package.SourceLabel.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
}
