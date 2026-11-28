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
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return;
        }

        if (IsMarketplaceMode)
        {
            var search = Marketplace.Packages.Count == 0 ? RefreshMarketplaceAsync(cancellationToken) : Task.CompletedTask;
            await Task.WhenAll(search, RefreshMarketplaceBadgesAsync(cancellationToken));
        }
        else if (_installedPackages.IsEmpty || _installedPackages.IsDirty)
        {
            await RefreshInstalledAsync(cancellationToken: cancellationToken);
        }
    }

    public async Task ApplyLaunchRequestAsync(AppLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind is not (AppLaunchRequestKind.PackageDetails or AppLaunchRequestKind.PackageInstall)
            || string.IsNullOrWhiteSpace(request.PackageId))
        {
            return;
        }

        ClearWarnings();
        if (request.RegistryUrl is not null)
        {
            RegistryUrlText = request.RegistryUrl.ToString();
        }

        Mode = PackageWindowMode.Marketplace;
        Marketplace.SearchText = request.PackageId;
        ApplySearchTextForCurrentMode();
        CancelQueuedMarketplaceSearch();
        var selectedPackage = await SearchAndSelectMarketplaceAsync(request.PackageId, true, cancellationToken);
        if (selectedPackage is null) return;
        Operations.StatusText = request.Kind == AppLaunchRequestKind.PackageInstall
            ? $"Review {selectedPackage.PackageId} before installing."
            : $"Loaded {selectedPackage.PackageId}.";
    }

    [RelayCommand]
    private async Task ShowMarketplaceAsync()
    {
        if (Mode == PackageWindowMode.Marketplace)
        {
            return;
        }

        Mode = PackageWindowMode.Marketplace;
        ApplySearchTextForCurrentMode();
        ClearWarnings();
        if (Marketplace.Packages.Count == 0)
        {
            await RefreshMarketplaceAsync();
            return;
        }

        var selected = _marketplace.ResolvePackageSelection(Marketplace.SelectedPackage?.PackageId);
        if (selected is not null)
        {
            await SelectMarketplacePackageAsync(selected);
        }
    }

    [RelayCommand]
    private async Task ShowInstalledAsync()
    {
        if (Mode == PackageWindowMode.Installed)
        {
            return;
        }

        Mode = PackageWindowMode.Installed;
        ApplySearchTextForCurrentMode();
        ClearWarnings();
        if (_installedPackages.IsDirty || Installed.Packages.Count == 0)
        {
            await RefreshInstalledAsync(Installed.SelectedPackage?.PackageId);
            return;
        }

        RebuildInstalledPackageList(Installed.SelectedPackage?.PackageId);
    }

    private void ApplySearchTextForCurrentMode()
    {
        var value = IsMarketplaceMode ? Marketplace.SearchText : Installed.SearchText;
        if (string.Equals(SearchText, value, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(HasSearchText));
            return;
        }

        _isApplyingModeSearchText = true;
        try
        {
            SearchText = value;
        }
        finally
        {
            _isApplyingModeSearchText = false;
        }

        OnPropertyChanged(nameof(HasSearchText));
    }

    [RelayCommand]
    private async Task SearchMarketplaceAsync()
    {
        Mode = PackageWindowMode.Marketplace;
        ApplySearchTextForCurrentMode();
        CancelQueuedMarketplaceSearch();
        await RefreshMarketplaceAsync();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        if (!string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
            return;
        }

        if (IsInstalledMode)
        {
            RebuildInstalledPackageList(Installed.SelectedPackage?.PackageId);
            return;
        }

        if (IsMarketplaceMode)
        {
            QueueMarketplaceSearch(TimeSpan.Zero);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsMarketplaceMode)
        {
            CancelQueuedMarketplaceSearch();
            _marketplace.Catalog.InvalidateOverviews();
            await Task.WhenAll(RefreshMarketplaceAsync(), RefreshMarketplaceBadgesAsync(_tasks.Token));
            return;
        }

        await RefreshInstalledAsync(Installed.SelectedPackage?.PackageId);
        _updateChecks?.EnqueueStartupCheck();
    }

    [RelayCommand]
    private async Task LoadMoreMarketplaceAsync()
    {
        if (!IsMarketplaceMode || !HasMoreMarketplacePackages) return;
        var generation = _marketplaceSearchVersion;
        try
        {
            var page = await _marketplace.Catalog.SearchAsync(Marketplace.SearchText,
                SelectedMarketplaceSortOption?.Sort ?? RegistrySearchSort.Downloads, _installedPackages.Catalog,
                item => SelectMarketplacePackageAsync(item), _tasks.Token, _nextMarketplaceOffset);
            if (_disposed || !IsMarketplaceMode || generation != _marketplaceSearchVersion)
            {
                foreach (var row in page.Packages) row.Dispose();
                return;
            }
            if (!page.Success) { Operations.StatusText = page.ErrorMessage!; return; }
            var ids = Marketplace.Packages.Select(row => row.PackageId).ToHashSet(StringComparer.Ordinal);
            foreach (var row in page.Packages)
            {
                if (ids.Add(row.PackageId)) Marketplace.Packages.Add(row);
                else row.Dispose();
            }
            _nextMarketplaceOffset += page.Packages.Count;
            HasMoreMarketplacePackages = page.Packages.Count == PackagesMarketplaceCatalog.PageSize;
            RefreshMarketplaceInstalledBadges();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { if (generation == _marketplaceSearchVersion) Operations.StatusText = error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanInstallPackage))]
    private async Task InstallPackageAsync()
        => await _selectedOperationCommands.InstallPackageAsync();

    [RelayCommand(CanExecute = nameof(CanEnableSelectedPackage))]
    private async Task EnableSelectedPackageAsync()
        => await _selectedOperationCommands.EnableSelectedPackageAsync();

    [RelayCommand(CanExecute = nameof(CanDisableSelectedPackage))]
    private async Task DisableSelectedPackageAsync()
        => await _selectedOperationCommands.DisableSelectedPackageAsync();

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedPackage))]
    private async Task UninstallSelectedPackageAsync()
        => await _selectedOperationCommands.UninstallSelectedPackageAsync();

    [RelayCommand(CanExecute = nameof(CanInstallSelectedMarketplacePackage))]
    private async Task InstallSelectedMarketplacePackageAsync()
        => await _selectedOperationCommands.InstallSelectedMarketplacePackageAsync();

    [RelayCommand(CanExecute = nameof(CanUpdateSelectedInstalledPackage))]
    private async Task UpdateSelectedInstalledPackageAsync()
        => await _selectedOperationCommands.UpdateSelectedInstalledPackageAsync();

    [RelayCommand(CanExecute = nameof(CanAdoptSelectedInstalledPackageSource))]
    private async Task AdoptSelectedInstalledPackageSourceAsync()
        => await _selectedOperationCommands.AdoptSelectedInstalledPackageSourceAsync();

    [RelayCommand(CanExecute = nameof(CanUpdateSelectedMarketplacePackage))]
    private async Task UpdateSelectedMarketplacePackageAsync()
        => await _selectedOperationCommands.UpdateSelectedMarketplacePackageAsync();

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedMarketplacePackage))]
    private async Task UninstallSelectedMarketplacePackageAsync()
        => await _selectedOperationCommands.UninstallSelectedMarketplacePackageAsync();

    [RelayCommand(CanExecute = nameof(CanUpdateAllPackages))]
    private async Task UpdateAllPackagesAsync()
        => await _selectedOperationCommands.UpdateAllPackagesAsync();

    [RelayCommand(CanExecute = nameof(CanRepairExecutionRuntime))]
    private async Task RepairExecutionRuntimeAsync(PackageExecutionRuntimeStatusViewModel? runtime)
    {
        if (runtime is null || !CanRepairExecutionRuntime(runtime))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        using var busy = Operations.EnterBusy();
        try
        {
            Operations.StatusText = $"Checking the impact of repairing {runtime.Title}...";
            var impact = await _runtimeApiClient.GetExecutionRuntimeImpactAsync(
                runtime.Identity,
                cancellationToken);
            if (impact.PackageReferenceCount > 1
                && (_confirmSharedRuntimeRepair is null
                    || !await _confirmSharedRuntimeRepair(runtime, impact, cancellationToken)))
            {
                Operations.StatusText = "Execution runtime repair cancelled.";
                return;
            }

            Operations.StatusText = $"Repairing {runtime.Title} for {runtime.Identity.Rid}...";
            var result = await _runtimeApiClient.RepairExecutionRuntimeAsync(
                runtime.Identity,
                impact,
                cancellationToken);
            Operations.StatusText = result.Message;
            if (result.Runtime is not null || result.Success)
            {
                await _installedPackages.RefreshExecutionRuntimesAsync(cancellationToken);
                if (Installed.SelectedPackage is { } selected)
                {
                    ApplySelectedPackageExecutionRuntimeState(selected.PackageId);
                }
            }
            if (!result.Success)
            {
                AddWarningLine(result.Message);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Operations.StatusText = ex.Message;
            AddWarningLine(ex.Message);
        }
        finally
        {
            NotifyCommandStateChanged();
        }
    }

    [RelayCommand]
    private void CancelSelectedPackageOperation()
        => _selectedOperationCommands.CancelSelectedPackageOperation();

    [RelayCommand(CanExecute = nameof(CanToggleSelectedMarketplacePackageStar))]
    private async Task ToggleSelectedMarketplacePackageStarAsync()
    {
        if (_disposed)
        {
            return;
        }

        var selectedPackage = SelectedMarketplacePackage;
        if (selectedPackage is null)
        {
            return;
        }

        if (!TryResolveRegistryUrl(out var registryUrl))
        {
            return;
        }

        var cancellationToken = _tasks.Token;
        using var busy = Operations.EnterBusy();
        try
        {
            var result = SelectedMarketplacePackageIsStarred
                ? await _runtimeApiClient.SetRegistryPackageStarAsync(new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, selectedPackage.PackageId, false), cancellationToken)
                : await _runtimeApiClient.SetRegistryPackageStarAsync(new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, selectedPackage.PackageId, true), cancellationToken);
            if (_disposed)
            {
                return;
            }
            if (!result.Success)
            {
                Operations.StatusText = result.Forbidden
                    ? "Sign in to the Registry before starring a package."
                    : result.Errors.FirstOrDefault() ?? "Registry package star update failed.";
                return;
            }

            selectedPackage.Stats = result.Stats;
            _marketplace.Catalog.InvalidateOverviews();
            Operations.StatusText = result.Message ?? "Updated package star.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Operations.StatusText = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                NotifyDetailsChanged();
                NotifyCommandStateChanged();
            }
        }
    }

}
