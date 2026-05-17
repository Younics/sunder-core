using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

internal static class MarketplacePackageSearchProjector
{
    public static IReadOnlyList<RegistryPackageSearchItemViewModel> Build(
        IReadOnlyList<RegistryPackageSummary> packages,
        IReadOnlyList<InstalledPackageDescriptor> installedPackages,
        IReadOnlyList<RegistryPackageUpdate> availableUpdates,
        Func<RegistryPackageSearchItemViewModel, Task> selectPackageAsync)
    {
        var installedById = installedPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var updatesById = availableUpdates.ToDictionary(update => update.PackageId, StringComparer.OrdinalIgnoreCase);

        return packages.Select(package =>
        {
            installedById.TryGetValue(package.PackageId, out var installedPackage);
            updatesById.TryGetValue(package.PackageId, out var update);
            return new RegistryPackageSearchItemViewModel(
                package,
                installedPackage?.Version,
                update,
                selectPackageAsync);
        }).ToArray();
    }
}
