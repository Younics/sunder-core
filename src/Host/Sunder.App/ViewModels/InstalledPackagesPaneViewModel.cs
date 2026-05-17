using System.Collections.ObjectModel;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

internal sealed class InstalledPackagesPaneViewModel(
    PackagesInstalledCatalog catalog,
    Func<string, PackageIconDescriptor?, Uri?> createPackageIconUri,
    Action<PackageCatalogItemViewModel> selectPackage)
    : IDisposable
{
    public ObservableCollection<PackageCatalogItemViewModel> Packages { get; } = [];

    public PackagesInstalledCatalog Catalog => catalog;

    public bool IsDirty { get; set; }

    public bool HasPackages => Packages.Count > 0;

    public bool IsEmpty => catalog.IsEmpty;

    public int InstalledPackageCount => catalog.InstalledPackageCount;

    public int ActivePackageCount => catalog.ActivePackageCount;

    public int DisabledPackageCount => catalog.DisabledPackageCount;

    public int FailedPackageCount => catalog.FailedPackageCount;

    public int AvailableUpdateCount => catalog.AvailableUpdateCount;

    public async Task RefreshAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        await catalog.RefreshAsync(addWarning, cancellationToken).ConfigureAwait(false);
        IsDirty = false;
    }

    public async Task RefreshInstalledPackageStateOnlyAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        await catalog.RefreshInstalledPackageStateOnlyAsync(addWarning, cancellationToken).ConfigureAwait(false);
    }

    public void RebuildList(string searchText)
    {
        var filteredPackages = InstalledPackageCatalogProjector.Build(
            catalog.SessionPackages,
            catalog.InstalledPackages,
            catalog.AvailableUpdates,
            searchText,
            createPackageIconUri,
            selectPackage);

        DisposeItems();
        Packages.ReplaceWith(filteredPackages);
    }

    public PackageCatalogItemViewModel? ResolveSelection(string? preferredPackageId, string? currentPackageId)
        => Packages
               .FirstOrDefault(item => string.Equals(item.PackageId, preferredPackageId, StringComparison.OrdinalIgnoreCase))
           ?? Packages.FirstOrDefault(item => string.Equals(item.PackageId, currentPackageId, StringComparison.OrdinalIgnoreCase))
           ?? Packages.FirstOrDefault();

    public InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => catalog.GetInstalledPackage(packageId);

    public RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => catalog.GetPackageUpdate(packageId);

    public void Dispose()
    {
        DisposeItems();
    }

    private void DisposeItems()
    {
        foreach (var package in Packages)
        {
            package.Dispose();
        }
    }
}
