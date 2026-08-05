using System.Collections.ObjectModel;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed class InstalledPackagesPaneViewModel : IDisposable
{
    private readonly PackagesInstalledCatalog _catalog;
    private readonly Func<string, PackageIconDescriptor?, Uri?> _createPackageIconUri;
    private readonly Action<PackageCatalogItemViewModel> _selectPackage;

    internal InstalledPackagesPaneViewModel(
        PackagesInstalledCatalog catalog,
        Func<string, PackageIconDescriptor?, Uri?> createPackageIconUri,
        Action<PackageCatalogItemViewModel> selectPackage)
    {
        _catalog = catalog;
        _createPackageIconUri = createPackageIconUri;
        _selectPackage = selectPackage;
    }

    public ObservableCollection<PackageCatalogItemViewModel> Packages { get; } = [];

    public string SearchText { get; set; } = string.Empty;

    public PackageCatalogItemViewModel? SelectedPackage { get; internal set; }

    internal PackagesInstalledCatalog Catalog => _catalog;

    public bool IsDirty { get; set; }

    public bool HasPackages => Packages.Count > 0;

    public bool IsEmpty => _catalog.IsEmpty;

    public int InstalledPackageCount => _catalog.InstalledPackageCount;

    public int ActivePackageCount => _catalog.ActivePackageCount;

    public int DisabledPackageCount => _catalog.DisabledPackageCount;

    public int FailedPackageCount => _catalog.FailedPackageCount;

    public int AvailableUpdateCount => _catalog.AvailableUpdateCount;

    public async Task RefreshAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        await _catalog.RefreshAsync(addWarning, cancellationToken).ConfigureAwait(false);
        IsDirty = false;
    }

    public async Task RefreshInstalledPackageStateOnlyAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        await _catalog.RefreshInstalledPackageStateOnlyAsync(addWarning, cancellationToken).ConfigureAwait(false);
    }

    public void RebuildList(string searchText)
    {
        var packageStates = InstalledPackageCatalogProjector.Build(
            _catalog.SessionPackages,
            _catalog.InstalledPackages,
            _catalog.AvailableUpdates,
            searchText,
            _createPackageIconUri);
        var existingById = Packages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var filteredPackages = packageStates
            .Select(state => existingById.TryGetValue(state.PackageId, out var existingPackage) && existingPackage.HasState(state)
                ? existingPackage
                : new PackageCatalogItemViewModel(state, _selectPackage))
            .ToArray();

        Packages.SyncWith(filteredPackages, removeItem: package => package.Dispose());
    }

    public PackageCatalogItemViewModel? ResolveSelection(string? preferredPackageId, string? currentPackageId)
        => Packages
               .FirstOrDefault(item => string.Equals(item.PackageId, preferredPackageId, StringComparison.OrdinalIgnoreCase))
           ?? Packages.FirstOrDefault(item => string.Equals(item.PackageId, currentPackageId, StringComparison.OrdinalIgnoreCase))
           ?? Packages.FirstOrDefault();

    public InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _catalog.GetInstalledPackage(packageId);

    public RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _catalog.GetPackageUpdate(packageId);

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
