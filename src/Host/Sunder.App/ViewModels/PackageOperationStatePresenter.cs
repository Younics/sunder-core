using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal sealed class PackageOperationStatePresenter(IPackageOperationExecutor operationExecutor)
{
    public bool HasActivePackageStoreOperation
        => operationExecutor.GetActivePackageStoreOperation()?.IsActive == true;

    public bool CancelActiveOperationForPackage(string? packageId)
        => !string.IsNullOrWhiteSpace(packageId)
           && operationExecutor.CancelActiveOperationForPackage(packageId);

    public void RefreshPackageRows(
        IEnumerable<IPackageOperationStateViewModel> marketplacePackages,
        IEnumerable<IPackageOperationStateViewModel> installedPackages)
    {
        foreach (var package in marketplacePackages)
        {
            PackageOperationStateProjector.Apply(package, GetActiveOperationForPackage(package));
        }

        foreach (var package in installedPackages)
        {
            PackageOperationStateProjector.Apply(package, GetActiveOperationForPackage(package));
        }
    }

    public SelectedPackageOperationState GetSelectedPackageState(string? packageId)
        => SelectedPackageOperationState.FromSnapshot(GetActiveOperationForPackage(packageId));

    private BackgroundProcessSnapshot? GetActiveOperationForPackage(IPackageOperationStateViewModel package)
        => package switch
        {
            RegistryPackageSearchItemViewModel marketplacePackage => GetActiveOperationForPackage(marketplacePackage.PackageId),
            PackageCatalogItemViewModel installedPackage => GetActiveOperationForPackage(installedPackage.PackageId),
            _ => null,
        };

    private BackgroundProcessSnapshot? GetActiveOperationForPackage(string? packageId)
        => string.IsNullOrWhiteSpace(packageId)
            ? null
            : operationExecutor.GetActiveOperationForPackage(packageId);
}
