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
    private async Task RefreshInstalledAsync(
        string? preferredPackageId = null,
        PackageOperationResult? operationResult = null,
        bool updateSelection = true)
    {
        IsBusy = true;
        try
        {
            await _installedPackages.RefreshAsync(AddWarningLine);
            NotifyUpdateStateChanged();
            StatusText = PackageOperationMessageFormatter.BuildInstalledStatusText(
                operationResult,
                InstalledPackageCount,
                ActivePackageCount,
                DisabledPackageCount,
                FailedPackageCount,
                AvailableUpdateCount);
            RebuildInstalledPackageList(preferredPackageId, updateSelection);
            RefreshMarketplaceInstalledBadges();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshMarketplaceAsync(CancellationToken cancellationToken = default)
    {
        var searchVersion = ++_marketplaceSearchVersion;
        IsBusy = true;
        try
        {
            StatusText = "Searching marketplace...";
            await RefreshInstalledPackageStateOnlyAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var searchResult = await _marketplace.Catalog.SearchAsync(
                _marketplaceSearchText,
                SelectedMarketplaceSortOption?.Sort ?? RegistrySearchSort.Downloads,
                _installedPackages.Catalog,
                item => SelectMarketplacePackageAsync(item),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (searchVersion != _marketplaceSearchVersion)
            {
                return;
            }

            if (!searchResult.Success)
            {
                StatusText = searchResult.ErrorMessage ?? "Marketplace search failed.";
                return;
            }

            ObserveSelectedMarketplacePackage(null);
            _marketplace.ReplacePackages(searchResult.Packages);
            RefreshPackageOperationState();
            OnPropertyChanged(nameof(HasMarketplacePackages));
            OnPropertyChanged(nameof(ShowNoMarketplacePackages));

            var selected = _marketplace.ResolvePackageSelection(_selectedMarketplacePackage?.PackageId);
            if (selected is null)
            {
                ClearMarketplaceSelection();
                StatusText = "No marketplace packages matched the search.";
                return;
            }

            await SelectMarketplacePackageAsync(selected, cancellationToken);
            StatusText = $"Found {MarketplacePackages.Count} marketplace package(s).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (searchVersion == _marketplaceSearchVersion)
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (searchVersion == _marketplaceSearchVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task RefreshInstalledPackageStateOnlyAsync(CancellationToken cancellationToken = default)
    {
        await _installedPackages.RefreshInstalledPackageStateOnlyAsync(AddWarningLine, cancellationToken);
        NotifyUpdateStateChanged();
    }

    private void RebuildInstalledPackageList(string? preferredPackageId = null, bool updateSelection = true)
    {
        _installedPackages.RebuildList(_installedSearchText);
        RefreshPackageOperationState();
        NotifyListVisibilityChanged();
        NotifyPackageCountsChanged();

        var selectedItem = _installedPackages.ResolveSelection(preferredPackageId, _selectedInstalledPackage?.PackageId);

        if (!updateSelection)
        {
            _selectedInstalledPackage = selectedItem;
            OnPropertyChanged(nameof(SelectedInstalledPackage));
            return;
        }

        if (selectedItem is null)
        {
            ClearInstalledSelection();
            return;
        }

        SelectInstalledPackage(selectedItem);
    }

    private void SelectInstalledPackage(PackageCatalogItemViewModel item)
    {
        InvalidateMarketplaceSelectionLoad();
        foreach (var package in InstalledPackages)
        {
            package.IsSelected = ReferenceEquals(package, item);
        }

        _selectedInstalledPackage = item;
        OnPropertyChanged(nameof(SelectedInstalledPackage));
        ObserveSelectedMarketplacePackage(null);
        ObserveSelectedInstalledPackage(item);
        ApplySelectedPackageDetails(PackageSelectionDetails.FromInstalled(item));
        RefreshSelectedPackageOperationState();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ClearInstalledSelection()
    {
        InvalidateMarketplaceSelectionLoad();
        _selectedInstalledPackage = null;
        OnPropertyChanged(nameof(SelectedInstalledPackage));
        ObserveSelectedMarketplacePackage(null);
        ObserveSelectedInstalledPackage(null);
        ApplySelectedPackageDetails(PackageSelectionDetails.NoInstalledMatch());
        RefreshSelectedPackageOperationState();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task SelectMarketplacePackageAsync(RegistryPackageSearchItemViewModel item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectionVersion = _marketplace.SelectionLoader.StartSelection();
        foreach (var package in MarketplacePackages)
        {
            package.IsSelected = ReferenceEquals(package, item);
        }

        _selectedMarketplacePackage = item;
        OnPropertyChanged(nameof(SelectedMarketplacePackage));
        ObserveSelectedInstalledPackage(null);
        ObserveSelectedMarketplacePackage(item);
        BeginMarketplacePackageDetailsLoad(item, selectionVersion);
        ClearWarnings();
        RefreshSelectedPackageOperationState();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();

        try
        {
            StatusText = $"Loading {item.PackageId}...";
            var details = await _marketplace.SelectionLoader.LoadDetailsAsync(
                selectionVersion,
                item.PackageId,
                SelectMarketplaceVersion,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (details is null)
            {
                return;
            }

            if (!IsCurrentMarketplaceSelection(item, selectionVersion))
            {
                return;
            }

            if (!details.Success)
            {
                CompleteMarketplacePackageDetailsLoad(details.ErrorMessage ?? "Package details failed to load.");
                StatusText = MarketplacePackageDetailsError;
                return;
            }

            if (!details.PackageFound || details.Package is null)
            {
                CompleteMarketplacePackageDetailsLoad($"Package '{item.PackageId}' was not found.");
                StatusText = MarketplacePackageDetailsError;
                return;
            }

            ApplySelectedPackageDetails(PackageSelectionDetails.FromMarketplaceDetails(item, details.Package));
            ApplyMarketplaceProfile(details.Profile);
            item.Stats = details.Stats;
            ApplyMarketplacePackageStats(details.Stats);
            ApplyMarketplaceAttributions(details.Creator, details.Maintainers);
            _marketplace.ReplaceVersions(details.Versions);
            SelectMarketplaceVersion(MarketplaceVersions.FirstOrDefault(version => string.Equals(version.Version, details.Package.LatestVersion, StringComparison.OrdinalIgnoreCase))
                ?? MarketplaceVersions.FirstOrDefault());
            CompleteMarketplacePackageDetailsLoad();
            StatusText = $"Loaded {details.Versions.Count} version(s) for {item.PackageId}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsCurrentMarketplaceSelection(item, selectionVersion))
            {
                CompleteMarketplacePackageDetailsLoad(ex.Message);
                StatusText = ex.Message;
            }
        }
    }

    private void BeginMarketplacePackageDetailsLoad(RegistryPackageSearchItemViewModel item, int selectionVersion)
    {
        CancelMarketplacePackageDetailsSpinnerDelay();
        ShowMarketplacePackageDetailsSpinner = false;
        _selectedMarketplaceVersion = null;
        OnPropertyChanged(nameof(SelectedMarketplaceVersion));
        _marketplace.ClearVersions();
        ApplyMarketplaceProfile(null);
        ClearMarketplacePackageStats();
        ApplyMarketplaceAttributions(null, []);
        ApplySelectedPackageDetails(PackageSelectionDetails.FromMarketplaceLoading(item));
        MarketplacePackageDetailsError = string.Empty;
        MarketplacePackageDetailsLoaded = false;
        IsMarketplacePackageDetailsLoading = true;
        QueueMarketplacePackageDetailsSpinner(item, selectionVersion);
        NotifyMarketplacePackageDetailsStateChanged();
    }

    private void CompleteMarketplacePackageDetailsLoad(string? errorMessage = null)
    {
        CancelMarketplacePackageDetailsSpinnerDelay();
        ShowMarketplacePackageDetailsSpinner = false;
        MarketplacePackageDetailsError = errorMessage ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            SelectedPackageSummary = "Package details could not be loaded.";
        }

        MarketplacePackageDetailsLoaded = string.IsNullOrWhiteSpace(errorMessage);
        IsMarketplacePackageDetailsLoading = false;
        NotifyMarketplacePackageDetailsStateChanged();
    }

    private void QueueMarketplacePackageDetailsSpinner(RegistryPackageSearchItemViewModel item, int selectionVersion)
    {
        var spinnerCancellation = new CancellationTokenSource();
        _marketplacePackageDetailsSpinnerCancellation = spinnerCancellation;
        _tasks.Observe(
            ShowMarketplacePackageDetailsSpinnerAfterDelayAsync(item, selectionVersion, spinnerCancellation),
            "delaying marketplace details spinner");
    }

    private async Task ShowMarketplacePackageDetailsSpinnerAfterDelayAsync(
        RegistryPackageSearchItemViewModel item,
        int selectionVersion,
        CancellationTokenSource spinnerCancellation)
    {
        try
        {
            await Task.Delay(_marketplaceDetailSpinnerDelay, spinnerCancellation.Token);
            if (IsCurrentMarketplaceSelection(item, selectionVersion)
                && IsMarketplacePackageDetailsLoading
                && !MarketplacePackageDetailsLoaded)
            {
                ShowMarketplacePackageDetailsSpinner = true;
                OnPropertyChanged(nameof(ShowMarketplacePackageDetailsLoading));
            }
        }
        catch (OperationCanceledException) when (spinnerCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_marketplacePackageDetailsSpinnerCancellation, spinnerCancellation))
            {
                _marketplacePackageDetailsSpinnerCancellation = null;
            }

            spinnerCancellation.Dispose();
        }
    }

    private void CancelMarketplacePackageDetailsSpinnerDelay()
    {
        var spinnerCancellation = _marketplacePackageDetailsSpinnerCancellation;
        if (spinnerCancellation is null)
        {
            return;
        }

        _marketplacePackageDetailsSpinnerCancellation = null;
        spinnerCancellation.Cancel();
    }

    private void SelectMarketplaceVersion(RegistryPackageVersionItemViewModel? item)
    {
        foreach (var version in MarketplaceVersions)
        {
            version.IsSelected = ReferenceEquals(version, item);
        }

        _selectedMarketplaceVersion = item;
        OnPropertyChanged(nameof(SelectedMarketplaceVersion));
        MarketplaceSelectedVersion = item?.Version ?? "Latest";
        ClearWarnings();
        if (item is { IsYanked: true })
        {
            AddWarningLine("Selected version is yanked and cannot be installed from the registry.");
        }

        if (!string.IsNullOrWhiteSpace(item?.DeprecatedMessage))
        {
            AddWarningLine($"Selected version is deprecated: {item.DeprecatedMessage}");
        }

        NotifyCommandStateChanged();
    }

    private void ClearMarketplaceSelection()
    {
        InvalidateMarketplaceSelectionLoad();
        _selectedMarketplacePackage = null;
        OnPropertyChanged(nameof(SelectedMarketplacePackage));
        _selectedMarketplaceVersion = null;
        OnPropertyChanged(nameof(SelectedMarketplaceVersion));
        ObserveSelectedInstalledPackage(null);
        ObserveSelectedMarketplacePackage(null);
        _marketplace.ClearVersions();
        ApplyMarketplaceProfile(null);
        ClearMarketplacePackageStats();
        ApplyMarketplaceAttributions(null, []);
        MarketplacePackageDetailsError = string.Empty;
        MarketplacePackageDetailsLoaded = false;
        IsMarketplacePackageDetailsLoading = false;
        ShowMarketplacePackageDetailsSpinner = false;
        ApplySelectedPackageDetails(PackageSelectionDetails.NoMarketplaceMatch());
        ClearWarnings();
        RefreshSelectedPackageOperationState();
        NotifyMarketplacePackageDetailsStateChanged();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void QueueMarketplaceSearch(TimeSpan? delay = null)
        => _marketplaceSearchScheduler.Queue(delay);

}
