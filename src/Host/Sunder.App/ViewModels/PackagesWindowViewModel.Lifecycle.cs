using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class PackagesWindowViewModel
{
    private void PackageOperationService_OnOperationChanged(object? sender, PackageOperationChangedEventArgs e)
        => _tasks.Observe(_uiDispatcher.InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            RefreshPackageOperationState();
            if (!e.Snapshot.IsTerminal)
            {
                return;
            }

            await RefreshAfterPackageOperationAsync(e.Snapshot);
        }), "refreshing a completed package operation");

    private async Task RefreshAfterPackageOperationAsync(BackgroundProcessSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var preferredPackageId = GetPreferredPackageIdForCompletedOperation(snapshot);
            await RefreshInstalledAsync(preferredPackageId, updateSelection: IsInstalledMode);

            Operations.StatusText = snapshot.State switch
            {
                BackgroundProcessState.Completed => snapshot.StatusText,
                BackgroundProcessState.Cancelled => $"Cancelled {snapshot.Title}.",
                BackgroundProcessState.Failed => snapshot.ErrorMessage ?? snapshot.StatusText,
                _ => Operations.StatusText,
            };
        }
        catch (Exception ex)
        {
            Operations.StatusText = ex.Message;
        }
    }

    private string? GetPreferredPackageIdForCompletedOperation(BackgroundProcessSnapshot snapshot)
    {
        if (PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
            && metadata.PackageId is { Length: > 0 } packageId)
        {
            return packageId;
        }

        return IsInstalledMode ? Installed.SelectedPackage?.PackageId : Marketplace.SelectedPackage?.PackageId;
    }

    private void CancelQueuedMarketplaceSearch()
        => _marketplaceSearchScheduler.Cancel();

    private void RefreshMarketplaceInstalledBadges()
    {
        foreach (var package in Marketplace.Packages)
        {
            package.InstalledVersion = GetInstalledPackage(package.PackageId)?.Version;
            package.Update = GetPackageUpdate(package.PackageId);
        }

        if (Marketplace.SelectedPackage is not null)
        {
            MarketplaceInstalledVersion = GetInstalledPackage(Marketplace.SelectedPackage.PackageId)?.Version ?? "Not installed";
        }

        RefreshPackageOperationState();
        NotifyCommandStateChanged();
    }

    private void InvalidateMarketplaceSelectionLoad()
    {
        _marketplace.SelectionLoader.Invalidate();
        CancelMarketplacePackageDetailsSpinnerDelay();
        ShowMarketplacePackageDetailsSpinner = false;
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsLoading));
    }

    private bool IsCurrentMarketplaceSelection(RegistryPackageSearchItemViewModel item, int selectionVersion)
        => !_disposed
           && _marketplace.SelectionLoader.IsCurrent(selectionVersion)
           && ReferenceEquals(Marketplace.SelectedPackage, item);

    private void RefreshPackageOperationState()
    {
        Operations.RefreshRows(Marketplace.Packages, Installed.Packages);
        RefreshSelectedPackageOperationState();
        NotifyCommandStateChanged();
    }

    private void RefreshSelectedPackageOperationState()
    {
        Operations.ApplySelectedPackage(GetSelectedPackageOperationPackageId());
    }

    private string? GetSelectedPackageOperationPackageId()
        => IsMarketplaceMode ? Marketplace.SelectedPackage?.PackageId : Installed.SelectedPackage?.PackageId;

    private void ApplySelectedPackageDetails(PackageSelectionDetails details)
    {
        SelectedPackageTitle = details.Title;
        SelectedPackageSubtitle = details.Subtitle;
        SelectedPackageStatus = details.Status;
        SelectedPackageSummary = details.Summary;
        SelectedPackageGlyph = details.Glyph;
        SelectedPackageIconImage = details.IconImage;
        SelectedPackageIconLoadError = details.IconLoadError;
        SelectedPackageHasError = details.HasError;
        SelectedPackageError = details.Error;
        SelectedPackageOperationHint = details.OperationHint;
        MarketplaceLatestVersion = details.MarketplaceLatestVersion;
        MarketplaceInstalledVersion = details.MarketplaceInstalledVersion;
        MarketplaceSelectedVersion = details.MarketplaceSelectedVersion;
    }

    private void ApplySelectedPackageIconState(SelectedPackageIconState state)
    {
        SelectedPackageGlyph = state.Glyph;
        SelectedPackageIconImage = state.IconImage;
        SelectedPackageIconLoadError = state.IconLoadError;
    }

    private InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedPackages.GetInstalledPackage(packageId);

    private Uri? CreatePackageIconUri(string packageId, PackageIconDescriptor? icon)
        => PackageIconUriResolver.Resolve(packageId, icon, _runtimeApiClient.CreatePackageAssetUri);

    private RegistryPackageUpdate? GetSelectedInstalledPackageUpdate()
        => Installed.SelectedPackage is null ? null : GetPackageUpdate(Installed.SelectedPackage.PackageId);

    private RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _installedPackages.GetPackageUpdate(packageId);

    private void ClearWarnings()
    {
        Operations.ClearWarnings();
    }

    private void AddWarningLine(string warning)
    {
        Operations.AddWarning(warning);
    }

    private void NotifyListVisibilityChanged()
    {
        OnPropertyChanged(nameof(HasInstalledPackages));
        OnPropertyChanged(nameof(ShowNoInstalledPackages));
        OnPropertyChanged(nameof(HasMarketplacePackages));
        OnPropertyChanged(nameof(ShowNoMarketplacePackages));
    }

    private void NotifyDetailsChanged()
    {
        OnPropertyChanged(nameof(ShowInstalledDetails));
        OnPropertyChanged(nameof(ShowMarketplaceDetails));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(ShowSelectedPackageIcon));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsLoading));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsContent));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsError));
        OnPropertyChanged(nameof(ShowMarketplacePackageStats));
        OnPropertyChanged(nameof(ShowMarketplacePackageStarAction));
        NotifyMarketplaceProfileChanged();
    }

    private void NotifyMarketplacePackageDetailsStateChanged()
    {
        OnPropertyChanged(nameof(HasMarketplacePackageDetailsError));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsSpinner));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsLoading));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsContent));
        OnPropertyChanged(nameof(ShowMarketplacePackageDetailsError));
        OnPropertyChanged(nameof(HasMarketplaceVersions));
        OnPropertyChanged(nameof(ShowNoMarketplaceVersions));
        OnPropertyChanged(nameof(ShowMarketplacePackageStats));
        OnPropertyChanged(nameof(ShowMarketplacePackageStarAction));
        NotifyMarketplaceProfileChanged();
        NotifyCommandStateChanged();
    }

    private void NotifyMarketplaceProfileChanged()
    {
        OnPropertyChanged(nameof(HasMarketplaceReadme));
        OnPropertyChanged(nameof(HasMarketplaceProfileLinks));
        OnPropertyChanged(nameof(HasMarketplaceProfileMetadata));
        OnPropertyChanged(nameof(HasMarketplaceProfileTags));
        OnPropertyChanged(nameof(HasMarketplaceProfile));
        OnPropertyChanged(nameof(HasMarketplaceProfileMedia));
        OnPropertyChanged(nameof(HasMarketplaceAttributions));
        OnPropertyChanged(nameof(HasMarketplaceCreators));
        OnPropertyChanged(nameof(HasMarketplaceMaintainers));
    }

    private void NotifyPackageCountsChanged()
    {
        OnPropertyChanged(nameof(InstalledPackageCount));
        OnPropertyChanged(nameof(ActivePackageCount));
        OnPropertyChanged(nameof(DisabledPackageCount));
        OnPropertyChanged(nameof(FailedPackageCount));
    }

    private void NotifyUpdateStateChanged()
    {
        OnPropertyChanged(nameof(AvailableUpdateCount));
        OnPropertyChanged(nameof(CanUpdateAllPackages));
        OnPropertyChanged(nameof(ShowUpdateAllPackages));
        OnPropertyChanged(nameof(ShowHeaderUpdateAllPackages));
        NotifyCommandStateChanged();
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanInstallPackage));
        OnPropertyChanged(nameof(CanEnableSelectedPackage));
        OnPropertyChanged(nameof(CanDisableSelectedPackage));
        OnPropertyChanged(nameof(ShowEnableSelectedPackage));
        OnPropertyChanged(nameof(ShowDisableSelectedPackage));
        OnPropertyChanged(nameof(CanUninstallSelectedPackage));
        OnPropertyChanged(nameof(CanUpdateSelectedInstalledPackage));
        OnPropertyChanged(nameof(ShowUpdateSelectedInstalledPackage));
        OnPropertyChanged(nameof(IsSelectedMarketplacePackageInstalled));
        OnPropertyChanged(nameof(ShowMarketplaceInstallAction));
        OnPropertyChanged(nameof(ShowMarketplaceInstalledActions));
        OnPropertyChanged(nameof(ShowMarketplaceInstallButton));
        OnPropertyChanged(nameof(ShowMarketplaceUninstallButton));
        OnPropertyChanged(nameof(ShowMarketplaceUpdateButton));
        OnPropertyChanged(nameof(CanInstallSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUninstallSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUpdateSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanToggleSelectedMarketplacePackageStar));
        OnPropertyChanged(nameof(CanUpdateAllPackages));
        RefreshCommand.NotifyCanExecuteChanged();
        InstallPackageCommand.NotifyCanExecuteChanged();
        EnableSelectedPackageCommand.NotifyCanExecuteChanged();
        DisableSelectedPackageCommand.NotifyCanExecuteChanged();
        UninstallSelectedPackageCommand.NotifyCanExecuteChanged();
        InstallSelectedMarketplacePackageCommand.NotifyCanExecuteChanged();
        UpdateSelectedInstalledPackageCommand.NotifyCanExecuteChanged();
        UpdateSelectedMarketplacePackageCommand.NotifyCanExecuteChanged();
        ToggleSelectedMarketplacePackageStarCommand.NotifyCanExecuteChanged();
        UninstallSelectedMarketplacePackageCommand.NotifyCanExecuteChanged();
        UpdateAllPackagesCommand.NotifyCanExecuteChanged();
    }

    private void ApplyMarketplaceProfile(RegistryPackageProfile? profile)
    {
        var shortDescription = _marketplace.ApplyProfile(profile);
        if (!string.IsNullOrWhiteSpace(shortDescription))
        {
            SelectedPackageSummary = shortDescription;
        }

        NotifyMarketplaceProfileChanged();
    }

    private void ApplyMarketplacePackageStats(RegistryPackageStats? stats)
    {
        if (stats is null)
        {
            ClearMarketplacePackageStats();
            return;
        }

        HasMarketplacePackageStats = true;
        MarketplacePackageStatsText = $"{stats.TotalDownloads:N0} downloads · {stats.Stars:N0} stars";
        SelectedMarketplacePackageIsStarred = stats.IsStarred;
        MarketplacePackageStarActionText = stats.IsStarred ? "Unstar" : "Star";
        OnPropertyChanged(nameof(ShowMarketplacePackageStats));
        NotifyCommandStateChanged();
    }

    private void ClearMarketplacePackageStats()
    {
        HasMarketplacePackageStats = false;
        MarketplacePackageStatsText = string.Empty;
        SelectedMarketplacePackageIsStarred = false;
        MarketplacePackageStarActionText = "Star";
        OnPropertyChanged(nameof(ShowMarketplacePackageStats));
        NotifyCommandStateChanged();
    }

    private void ApplyMarketplaceAttributions(
        RegistryUserAttribution? creator,
        IReadOnlyList<RegistryUserAttribution> maintainers)
    {
        var creators = creator is null
            ? Array.Empty<RegistryUserAttributionViewModel>()
            : new[] { new RegistryUserAttributionViewModel(creator) };
        var maintainerAttributions = maintainers
            .Where(maintainer => creator is null || !maintainer.IsOwner)
            .Select(maintainer => new RegistryUserAttributionViewModel(maintainer))
            .ToArray();

        Marketplace.Creators.ReplaceWith(creators);
        Marketplace.Maintainers.ReplaceWith(maintainerAttributions);
        Marketplace.Attributions.ReplaceWith(creators.Concat(maintainerAttributions));
        OnPropertyChanged(nameof(HasMarketplaceAttributions));
        OnPropertyChanged(nameof(HasMarketplaceCreators));
        OnPropertyChanged(nameof(HasMarketplaceMaintainers));
    }

    private bool TryResolveRegistryUrl(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(RegistryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        Operations.StatusText = "Enter a valid HTTP Registry URL before using this action.";
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packageOperationExecutor.OperationChanged -= PackageOperationService_OnOperationChanged;
        Operations.PropertyChanged -= Operations_OnPropertyChanged;

        _marketplaceSearchScheduler.Dispose();
        _tasks.Dispose();
        CancelMarketplacePackageDetailsSpinnerDelay();
        ObserveSelectedInstalledPackage(null);
        ObserveSelectedMarketplacePackage(null);
        _selectedPackageIconObserver.Dispose();
        _marketplace.Dispose();
        _installedPackages.Dispose();
        if (!ReferenceEquals(PackageProcesses, BackgroundProcessMonitorViewModel.Empty))
        {
            PackageProcesses.Dispose();
        }
        _runtimeApiClient.Dispose();
    }

    private void ObserveSelectedInstalledPackage(PackageCatalogItemViewModel? item)
        => _selectedPackageIconObserver.ObserveInstalled(item);

    private void ObserveSelectedMarketplacePackage(RegistryPackageSearchItemViewModel? item)
        => _selectedPackageIconObserver.ObserveMarketplace(item);

}
