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

        if (_installedPackages.IsEmpty)
        {
            await RefreshInstalledCoreAsync(null, null, updateSelection: true, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Mode == PackageWindowMode.Marketplace && Marketplace.Packages.Count == 0)
        {
            await RefreshMarketplaceAsync(cancellationToken);
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
        await RefreshMarketplaceAsync(cancellationToken);

        var selectedPackage = Marketplace.Packages.FirstOrDefault(package =>
            string.Equals(package.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase));
        if (selectedPackage is null)
        {
            _marketplace.ReplacePackages([]);
            OnPropertyChanged(nameof(HasMarketplacePackages));
            OnPropertyChanged(nameof(ShowNoMarketplacePackages));
            ClearMarketplaceSelection();
            Operations.StatusText = $"Package '{request.PackageId}' was not found in the Registry.";
            return;
        }

        _marketplace.KeepOnlyPackage(selectedPackage);
        OnPropertyChanged(nameof(HasMarketplacePackages));
        OnPropertyChanged(nameof(ShowNoMarketplacePackages));
        await SelectMarketplacePackageAsync(selectedPackage, cancellationToken);
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
            await RefreshMarketplaceAsync();
            return;
        }

        await RefreshInstalledAsync(Installed.SelectedPackage?.PackageId);
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

    [RelayCommand(CanExecute = nameof(CanUpdateSelectedMarketplacePackage))]
    private async Task UpdateSelectedMarketplacePackageAsync()
        => await _selectedOperationCommands.UpdateSelectedMarketplacePackageAsync();

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedMarketplacePackage))]
    private async Task UninstallSelectedMarketplacePackageAsync()
        => await _selectedOperationCommands.UninstallSelectedMarketplacePackageAsync();

    [RelayCommand(CanExecute = nameof(CanUpdateAllPackages))]
    private async Task UpdateAllPackagesAsync()
        => await _selectedOperationCommands.UpdateAllPackagesAsync();

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
            ApplyMarketplacePackageStats(result.Stats);
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
