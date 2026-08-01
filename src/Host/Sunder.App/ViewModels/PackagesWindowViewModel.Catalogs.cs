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
        if (_disposed)
        {
            return;
        }

        await RefreshInstalledCoreAsync(preferredPackageId, operationResult, updateSelection, _tasks.Token);
    }

    private async Task RefreshInstalledCoreAsync(
        string? preferredPackageId,
        PackageOperationResult? operationResult,
        bool updateSelection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return;
        }

        using var busy = Operations.EnterBusy();
        try
        {
            await _installedPackages.RefreshAsync(AddWarningLine, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            NotifyUpdateStateChanged();
            Operations.StatusText = PackageOperationMessageFormatter.BuildInstalledStatusText(
                operationResult,
                InstalledPackageCount,
                ActivePackageCount,
                DisabledPackageCount,
                FailedPackageCount,
                AvailableUpdateCount);
            RebuildInstalledPackageList(preferredPackageId, updateSelection);
            RefreshMarketplaceInstalledBadges();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Operations.StatusText = ex.Message;
            }
        }
    }

    private async Task RefreshMarketplaceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var searchVersion = ++_marketplaceSearchVersion;
        using var busy = Operations.EnterBusy();
        try
        {
            Operations.StatusText = "Searching marketplace...";
            await RefreshInstalledPackageStateOnlyAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var searchResult = await _marketplace.Catalog.SearchAsync(
                Marketplace.SearchText,
                SelectedMarketplaceSortOption?.Sort ?? RegistrySearchSort.Downloads,
                _installedPackages.Catalog,
                item => SelectMarketplacePackageAsync(item),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || searchVersion != _marketplaceSearchVersion)
            {
                return;
            }

            if (!searchResult.Success)
            {
                Operations.StatusText = searchResult.ErrorMessage ?? "Marketplace search failed.";
                return;
            }

            ObserveSelectedMarketplacePackage(null);
            _marketplace.ReplacePackages(searchResult.Packages);
            RefreshPackageOperationState();
            OnPropertyChanged(nameof(HasMarketplacePackages));
            OnPropertyChanged(nameof(ShowNoMarketplacePackages));

            var selected = _marketplace.ResolvePackageSelection(Marketplace.SelectedPackage?.PackageId);
            if (selected is null)
            {
                ClearMarketplaceSelection();
                Operations.StatusText = "No marketplace packages matched the search.";
                return;
            }

            await SelectMarketplacePackageAsync(selected, cancellationToken);
            Operations.StatusText = $"Found {Marketplace.Packages.Count} marketplace package(s).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed && searchVersion == _marketplaceSearchVersion)
            {
                Operations.StatusText = ex.Message;
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
        _installedPackages.RebuildList(Installed.SearchText);
        RefreshPackageOperationState();
        NotifyListVisibilityChanged();
        NotifyPackageCountsChanged();

        var selectedItem = _installedPackages.ResolveSelection(preferredPackageId, Installed.SelectedPackage?.PackageId);

        if (!updateSelection)
        {
            Installed.SelectedPackage = selectedItem;
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
        foreach (var package in Installed.Packages)
        {
            package.IsSelected = ReferenceEquals(package, item);
        }

        Installed.SelectedPackage = item;
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
        Installed.SelectedPackage = null;
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
        foreach (var package in Marketplace.Packages)
        {
            package.IsSelected = ReferenceEquals(package, item);
        }

        Marketplace.SelectedPackage = item;
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
            Operations.StatusText = $"Loading {item.PackageId}...";
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
                Operations.StatusText = MarketplacePackageDetailsError;
                return;
            }

            if (!details.PackageFound || details.Package is null)
            {
                CompleteMarketplacePackageDetailsLoad($"Package '{item.PackageId}' was not found.");
                Operations.StatusText = MarketplacePackageDetailsError;
                return;
            }

            ApplySelectedPackageDetails(PackageSelectionDetails.FromMarketplaceDetails(item, details.Package));
            ApplyMarketplaceProfile(details.Profile);
            item.Stats = details.Stats;
            ApplyMarketplacePackageStats(details.Stats);
            ApplyMarketplaceAttributions(details.Creator, details.Maintainers);
            _marketplace.ReplaceVersions(details.Versions);
            SelectMarketplaceVersion(Marketplace.Versions.FirstOrDefault(version => string.Equals(version.Version, details.Package.LatestVersion, StringComparison.Ordinal))
                ?? Marketplace.Versions.FirstOrDefault());
            CompleteMarketplacePackageDetailsLoad();
            Operations.StatusText = $"Loaded {details.Versions.Count} version(s) for {item.PackageId}.";
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
                Operations.StatusText = ex.Message;
            }
        }
    }

    private void BeginMarketplacePackageDetailsLoad(RegistryPackageSearchItemViewModel item, int selectionVersion)
    {
        CancelMarketplacePackageDetailsSpinnerDelay();
        ShowMarketplacePackageDetailsSpinner = false;
        Marketplace.SelectedVersion = null;
        OnPropertyChanged(nameof(SelectedMarketplaceVersion));
        _marketplace.ClearVersions();
        _marketplaceVersionDetailsRequest.Invalidate();
        Marketplace.ClearRpcContractUses();
        ApplyMarketplaceProfile(null);
        ClearMarketplacePackageStats();
        ApplyMarketplaceAttributions(null, []);
        ApplySelectedPackageDetails(PackageSelectionDetails.FromMarketplaceLoading(item));
        SetMarketplacePackageDetailsState(PresentationOperationState.Running);
        QueueMarketplacePackageDetailsSpinner(item, selectionVersion);
        NotifyMarketplacePackageDetailsStateChanged();
    }

    private void CompleteMarketplacePackageDetailsLoad(string? errorMessage = null)
    {
        CancelMarketplacePackageDetailsSpinnerDelay();
        ShowMarketplacePackageDetailsSpinner = false;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            SelectedPackageSummary = "Package details could not be loaded.";
        }

        SetMarketplacePackageDetailsState(string.IsNullOrWhiteSpace(errorMessage)
            ? PresentationOperationState.Succeeded
            : PresentationOperationState.Failed(errorMessage));
        NotifyMarketplacePackageDetailsStateChanged();
    }

    private void QueueMarketplacePackageDetailsSpinner(RegistryPackageSearchItemViewModel item, int selectionVersion)
    {
        var spinnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_tasks.Token);
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
        foreach (var version in Marketplace.Versions)
        {
            version.IsSelected = ReferenceEquals(version, item);
        }

        Marketplace.SelectedVersion = item;
        OnPropertyChanged(nameof(SelectedMarketplaceVersion));
        MarketplaceSelectedVersion = item?.Version ?? "Latest";
        if (item is null || Marketplace.SelectedPackage is null)
        {
            _marketplaceVersionDetailsRequest.Invalidate();
            Marketplace.ClearRpcContractUses();
        }
        else
        {
            Marketplace.BeginRpcContractUseLoad();
            _tasks.Observe(
                LoadSelectedMarketplaceVersionDetailsAsync(Marketplace.SelectedPackage, item),
                "loading selected marketplace package version details");
        }

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

    private async Task LoadSelectedMarketplaceVersionDetailsAsync(
        RegistryPackageSearchItemViewModel package,
        RegistryPackageVersionItemViewModel version)
    {
        using var request = _marketplaceVersionDetailsRequest.Start(_tasks.Token);
        try
        {
            var details = await Marketplace.Catalog.LoadVersionDetailsAsync(
                package.PackageId,
                version.Version,
                request.Token);
            if (!request.IsCurrent
                || !ReferenceEquals(Marketplace.SelectedPackage, package)
                || !ReferenceEquals(Marketplace.SelectedVersion, version))
            {
                return;
            }

            if (details is null
                || !string.Equals(details.PackageId, package.PackageId, StringComparison.Ordinal)
                || !string.Equals(details.Version, version.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Registry details for '{package.PackageId}' version '{version.Version}' were unavailable or mismatched.");
            }

            Marketplace.CompleteRpcContractUseLoad(
                PackageRpcAccessProjection.FromRegistry(details.UsesContracts));
            request.Complete();
            NotifyCommandStateChanged();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            request.Fail(ex);
            if (request.IsCurrent
                && ReferenceEquals(Marketplace.SelectedPackage, package)
                && ReferenceEquals(Marketplace.SelectedVersion, version))
            {
                Marketplace.FailRpcContractUseLoad(ex.Message);
                NotifyCommandStateChanged();
            }
            AppSessionLog.WriteError(
                $"Failed to load RPC access for marketplace package '{package.PackageId}' version '{version.Version}'.",
                ex);
        }
    }

    private void ClearMarketplaceSelection()
    {
        InvalidateMarketplaceSelectionLoad();
        Marketplace.SelectedPackage = null;
        OnPropertyChanged(nameof(SelectedMarketplacePackage));
        Marketplace.SelectedVersion = null;
        OnPropertyChanged(nameof(SelectedMarketplaceVersion));
        ObserveSelectedInstalledPackage(null);
        ObserveSelectedMarketplacePackage(null);
        _marketplace.ClearVersions();
        _marketplaceVersionDetailsRequest.Invalidate();
        Marketplace.ClearRpcContractUses();
        ApplyMarketplaceProfile(null);
        ClearMarketplacePackageStats();
        ApplyMarketplaceAttributions(null, []);
        SetMarketplacePackageDetailsState(PresentationOperationState.Idle);
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

    private void SetMarketplacePackageDetailsState(PresentationOperationState state)
    {
        if (_marketplacePackageDetailsState == state)
        {
            return;
        }

        _marketplacePackageDetailsState = state;
        OnPropertyChanged(nameof(IsMarketplacePackageDetailsLoading));
        OnPropertyChanged(nameof(MarketplacePackageDetailsLoaded));
        OnPropertyChanged(nameof(MarketplacePackageDetailsError));
    }

}
