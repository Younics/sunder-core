using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Notifications;

namespace Sunder.App.ViewModels;

public enum PackageWindowMode
{
    Marketplace,
    Installed,
}

public sealed partial class PackagesWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly IPackageArchivePicker _packageArchivePicker;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly NotificationCenterService? _notificationCenter;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _applyPackageLifecycleChangesAsync;
    private readonly List<SessionPackageDescriptor> _sessionPackages = [];
    private readonly List<InstalledPackageDescriptor> _installedPackages = [];
    private readonly List<RegistryPackageUpdate> _availableUpdates = [];
    private PackageCatalogItemViewModel? _selectedInstalledPackage;
    private PackageCatalogItemViewModel? _observedSelectedInstalledPackage;
    private RegistryPackageSearchItemViewModel? _selectedMarketplacePackage;
    private RegistryPackageVersionItemViewModel? _selectedMarketplaceVersion;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? MarketplaceImageGalleryRequested;

    public PackagesWindowViewModel(
        IRuntimeApiClient runtimeApiClient,
        IPackageArchivePicker packageArchivePicker,
        Func<IReadOnlyList<string>, CancellationToken, Task>? applyPackageLifecycleChangesAsync = null,
        RegistryPackageInstallService? registryInstallService = null,
        NotificationCenterService? notificationCenter = null)
    {
        _runtimeApiClient = runtimeApiClient;
        _packageArchivePicker = packageArchivePicker;
        _applyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync ?? ((_, _) => Task.CompletedTask);
        _registryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        _notificationCenter = notificationCenter;
        InstalledPackages = [];
        MarketplacePackages = [];
        MarketplaceVersions = [];
        MarketplaceProfileLinks = [];
        MarketplaceProfileMetadata = [];
        MarketplaceProfileTags = [];
        MarketplaceProfileMedia = [];
        WarningLines = [];
        RegistryUrlText = RegistryUrlHelper.DefaultRegistryUrl.ToString();
    }

    public ObservableCollection<PackageCatalogItemViewModel> InstalledPackages { get; }

    public ObservableCollection<RegistryPackageSearchItemViewModel> MarketplacePackages { get; }

    public ObservableCollection<RegistryPackageVersionItemViewModel> MarketplaceVersions { get; }

    public ObservableCollection<RegistryPackageProfileLinkViewModel> MarketplaceProfileLinks { get; }

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> MarketplaceProfileMetadata { get; }

    public ObservableCollection<string> MarketplaceProfileTags { get; }

    public ObservableCollection<RegistryPackageMediaItemViewModel> MarketplaceProfileMedia { get; }

    public ObservableStringBuilder MarketplaceReadmeMarkdownBuilder { get; } = new();

    public ObservableCollection<string> WarningLines { get; }

    public bool IsMarketplaceMode => Mode == PackageWindowMode.Marketplace;

    public bool IsInstalledMode => Mode == PackageWindowMode.Installed;

    public bool HasInstalledPackages => InstalledPackages.Count > 0;

    public bool ShowNoInstalledPackages => IsInstalledMode && !HasInstalledPackages;

    public bool HasMarketplacePackages => MarketplacePackages.Count > 0;

    public bool ShowNoMarketplacePackages => IsMarketplaceMode && !HasMarketplacePackages;

    public bool HasMarketplaceVersions => MarketplaceVersions.Count > 0;

    public bool ShowNoMarketplaceVersions => !HasMarketplaceVersions;

    public bool HasMarketplaceReadme => MarketplaceReadmeMarkdownBuilder.Length > 0;

    public bool HasMarketplaceProfileLinks => MarketplaceProfileLinks.Count > 0;

    public bool HasMarketplaceProfileMetadata => MarketplaceProfileMetadata.Count > 0;

    public bool HasMarketplaceProfileTags => MarketplaceProfileTags.Count > 0;

    public bool HasMarketplaceProfile => HasMarketplaceProfileLinks || HasMarketplaceProfileMetadata || HasMarketplaceProfileTags;

    public bool HasMarketplaceProfileMedia => MarketplaceProfileMedia.Count > 0;

    public bool HasWarnings => WarningLines.Count > 0;

    public bool ShowInstalledDetails => IsInstalledMode && _selectedInstalledPackage is not null;

    public bool ShowMarketplaceDetails => IsMarketplaceMode && _selectedMarketplacePackage is not null;

    public bool ShowNoSelection => !ShowInstalledDetails && !ShowMarketplaceDetails;

    public bool ShowSelectedPackageIcon => ShowInstalledDetails || ShowMarketplaceDetails;

    public bool SelectedPackageHasIconImage => SelectedPackageIconImage is not null;

    public bool SelectedPackageShowGlyphFallback => SelectedPackageIconImage is null;

    public bool SelectedPackageHasIconLoadError => !string.IsNullOrWhiteSpace(SelectedPackageIconLoadError);

    public bool CanRefresh => !IsBusy;

    public bool CanInstallPackage => !IsBusy;

    public bool CanEnableSelectedPackage => !IsBusy && IsInstalledMode && _selectedInstalledPackage?.CanEnable == true;

    public bool CanDisableSelectedPackage => !IsBusy && IsInstalledMode && _selectedInstalledPackage?.CanDisable == true;

    public bool ShowEnableSelectedPackage => IsInstalledMode && _selectedInstalledPackage?.CanEnable == true;

    public bool ShowDisableSelectedPackage => IsInstalledMode && _selectedInstalledPackage?.CanDisable == true;

    public bool CanUninstallSelectedPackage => !IsBusy && IsInstalledMode && _selectedInstalledPackage?.CanUninstall == true;

    public bool CanUpdateSelectedInstalledPackage => !IsBusy && IsInstalledMode && GetSelectedInstalledPackageUpdate() is not null;

    public bool ShowUpdateSelectedInstalledPackage => IsInstalledMode && GetSelectedInstalledPackageUpdate() is not null;

    public bool IsSelectedMarketplacePackageInstalled => _selectedMarketplacePackage?.InstalledVersion is not null;

    public bool ShowMarketplaceInstallAction => IsMarketplaceMode && _selectedMarketplacePackage is not null && !IsSelectedMarketplacePackageInstalled;

    public bool ShowMarketplaceInstalledActions => IsMarketplaceMode && IsSelectedMarketplacePackageInstalled;

    public bool CanInstallSelectedMarketplacePackage => !IsBusy && ShowMarketplaceInstallAction && _selectedMarketplacePackage is { IsYanked: false };

    public bool CanUninstallSelectedMarketplacePackage => !IsBusy && ShowMarketplaceInstalledActions;

    public bool CanUpdateSelectedMarketplacePackage => !IsBusy && IsMarketplaceMode && _selectedMarketplacePackage?.HasUpdate == true;

    public bool CanUpdateAllPackages => !IsBusy && AvailableUpdateCount > 0;

    public bool ShowUpdateAllPackages => AvailableUpdateCount > 0;

    public string SearchPlaceholder => IsMarketplaceMode ? "Search marketplace packages" : "Search installed and session packages";

    public int InstalledPackageCount => _installedPackages.Count;

    public int ActivePackageCount => _sessionPackages.Count(package => package.IsEnabled);

    public int DisabledPackageCount => _installedPackages.Count(package => !package.IsEnabled);

    public int FailedPackageCount => _sessionPackages.Count(package => package.Readiness == PackageReadinessState.Failed);

    public int AvailableUpdateCount => _availableUpdates.Count;

    [ObservableProperty]
    private PackageWindowMode _mode = PackageWindowMode.Marketplace;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _registryUrlText;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Search the marketplace or inspect installed packages.";

    [ObservableProperty]
    private string _selectedPackageTitle = "No package selected";

    [ObservableProperty]
    private string _selectedPackageSubtitle = "Select a package to inspect details.";

    [ObservableProperty]
    private string _selectedPackageStatus = string.Empty;

    [ObservableProperty]
    private string _selectedPackageSummary = string.Empty;

    [ObservableProperty]
    private string _selectedPackageGlyph = "?";

    [ObservableProperty]
    private IImage? _selectedPackageIconImage;

    [ObservableProperty]
    private string _selectedPackageIconLoadError = string.Empty;

    [ObservableProperty]
    private string _selectedPackageError = string.Empty;

    [ObservableProperty]
    private string _selectedPackageOperationHint = string.Empty;

    [ObservableProperty]
    private bool _selectedPackageHasError;

    [ObservableProperty]
    private string _marketplaceLatestVersion = "-";

    [ObservableProperty]
    private string _marketplaceInstalledVersion = "Not installed";

    [ObservableProperty]
    private string _marketplaceSelectedVersion = "Latest";

    partial void OnModeChanged(PackageWindowMode value)
    {
        OnPropertyChanged(nameof(IsMarketplaceMode));
        OnPropertyChanged(nameof(IsInstalledMode));
        OnPropertyChanged(nameof(SearchPlaceholder));
        NotifyListVisibilityChanged();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandStateChanged();

    partial void OnSearchTextChanged(string value)
    {
        if (IsInstalledMode)
        {
            RebuildInstalledPackageList(_selectedInstalledPackage?.PackageId);
        }
    }

    partial void OnSelectedPackageHasErrorChanged(bool value) => OnPropertyChanged(nameof(ShowNoInstalledPackageError));

    partial void OnSelectedPackageIconImageChanged(IImage? value)
    {
        OnPropertyChanged(nameof(SelectedPackageHasIconImage));
        OnPropertyChanged(nameof(SelectedPackageShowGlyphFallback));
    }

    partial void OnSelectedPackageIconLoadErrorChanged(string value)
        => OnPropertyChanged(nameof(SelectedPackageHasIconLoadError));

    public bool ShowNoInstalledPackageError => !SelectedPackageHasError;

    public async Task InitializeAsync()
    {
        if (_installedPackages.Count == 0 && _sessionPackages.Count == 0)
        {
            await RefreshInstalledAsync();
        }

        if (Mode == PackageWindowMode.Marketplace && MarketplacePackages.Count == 0)
        {
            await RefreshMarketplaceAsync();
        }
    }

    [RelayCommand]
    private async Task ShowMarketplaceAsync()
    {
        if (Mode == PackageWindowMode.Marketplace)
        {
            return;
        }

        Mode = PackageWindowMode.Marketplace;
        ClearWarnings();
        if (MarketplacePackages.Count == 0)
        {
            await RefreshMarketplaceAsync();
            return;
        }

        var selected = MarketplacePackages.FirstOrDefault(package =>
                string.Equals(package.PackageId, _selectedMarketplacePackage?.PackageId, StringComparison.OrdinalIgnoreCase))
            ?? MarketplacePackages.FirstOrDefault();
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
        ClearWarnings();
        if (InstalledPackages.Count == 0)
        {
            await RefreshInstalledAsync();
            return;
        }

        SelectInstalledPackage(InstalledPackages.FirstOrDefault(package =>
                string.Equals(package.PackageId, _selectedInstalledPackage?.PackageId, StringComparison.OrdinalIgnoreCase))
            ?? InstalledPackages.First());
    }

    [RelayCommand]
    private async Task SearchMarketplaceAsync()
    {
        Mode = PackageWindowMode.Marketplace;
        await RefreshMarketplaceAsync();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        if (IsInstalledMode)
        {
            RebuildInstalledPackageList(_selectedInstalledPackage?.PackageId);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsMarketplaceMode)
        {
            await RefreshMarketplaceAsync();
            return;
        }

        await RefreshInstalledAsync(_selectedInstalledPackage?.PackageId);
    }

    [RelayCommand]
    private async Task InstallPackageAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var packagePath = await _packageArchivePicker.PickPackagePathAsync();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => _runtimeApiClient.InstallPackageFromPathAsync(packagePath),
            selectedPackageId: null,
            "Package installed",
            "Package installed from disk.");
        Mode = PackageWindowMode.Installed;
    }

    [RelayCommand]
    private async Task EnableSelectedPackageAsync()
    {
        var packageId = _selectedInstalledPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId) || IsBusy)
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => _runtimeApiClient.EnableInstalledPackageAsync(packageId),
            packageId,
            "Package enabled",
            $"{packageId} was enabled.");
    }

    [RelayCommand]
    private async Task DisableSelectedPackageAsync()
    {
        var packageId = _selectedInstalledPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId) || IsBusy)
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => _runtimeApiClient.DisableInstalledPackageAsync(packageId),
            packageId,
            "Package disabled",
            $"{packageId} was disabled.");
    }

    [RelayCommand]
    private async Task UninstallSelectedPackageAsync()
    {
        var packageId = _selectedInstalledPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId) || IsBusy)
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => _runtimeApiClient.UninstallPackageAsync(packageId),
            packageId,
            "Package uninstalled",
            $"{packageId} was uninstalled.");
    }

    [RelayCommand]
    private async Task InstallSelectedMarketplacePackageAsync()
    {
        var packageId = _selectedMarketplacePackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId) || IsBusy)
        {
            return;
        }

        await ExecuteRegistryInstallAsync(registryClient => _registryInstallService.InstallPackageAsync(
            packageId,
            version: null,
            tag: "latest",
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            _runtimeApiClient),
            "Package installed",
            $"{packageId} was installed from the marketplace.");
    }

    [RelayCommand]
    private async Task UpdateSelectedInstalledPackageAsync()
    {
        var update = GetSelectedInstalledPackageUpdate();
        if (update is null || IsBusy)
        {
            return;
        }

        await ExecuteRegistryInstallAsync(registryClient => _registryInstallService.InstallPackageAsync(
            update.PackageId,
            update.AvailableVersion,
            tag: null,
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            _runtimeApiClient),
            "Package updated",
            $"{update.PackageId} was updated to {update.AvailableVersion}.");
    }

    [RelayCommand]
    private async Task UpdateSelectedMarketplacePackageAsync()
    {
        var packageId = _selectedMarketplacePackage?.PackageId;
        var update = string.IsNullOrWhiteSpace(packageId) ? null : GetPackageUpdate(packageId);
        if (update is null || IsBusy)
        {
            return;
        }

        await ExecuteRegistryInstallAsync(registryClient => _registryInstallService.InstallPackageAsync(
            update.PackageId,
            update.AvailableVersion,
            tag: null,
            allowDowngrade: false,
            reinstall: false,
            registryClient,
            _runtimeApiClient),
            "Package updated",
            $"{update.PackageId} was updated to {update.AvailableVersion}.");
    }

    [RelayCommand]
    private async Task UninstallSelectedMarketplacePackageAsync()
    {
        var packageId = _selectedMarketplacePackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId) || IsBusy)
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => _runtimeApiClient.UninstallPackageAsync(packageId),
            packageId,
            "Package uninstalled",
            $"{packageId} was uninstalled.");
    }

    [RelayCommand]
    private async Task UpdateAllPackagesAsync()
    {
        if (IsBusy || AvailableUpdateCount == 0)
        {
            return;
        }

        await ExecuteRegistryInstallAsync(
            registryClient => _registryInstallService.UpdateAllAsync(registryClient, _runtimeApiClient),
            "Packages updated",
            "Installed packages were updated.");
    }

    private async Task ExecuteLocalPackageOperationAsync(
        Func<Task<PackageOperationResult>> operation,
        string? selectedPackageId,
        string successTitle,
        string successFallbackMessage)
    {
        IsBusy = true;
        PackageOperationResult? operationResult = null;
        try
        {
            operationResult = await operation();
            if (operationResult.Success)
            {
                StatusText = "Applying package changes to the running shell...";
                operationResult = await ApplyShellPackageChangesAsync(operationResult);
            }
        }
        catch (Exception ex)
        {
            operationResult = new PackageOperationResult(false, ex.Message, RequiresAppRestart: false, [], [ex.Message]);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshInstalledAsync(selectedPackageId, operationResult);
        if (operationResult is { Success: true } && operationResult.ImpactedPackageIds.Count > 0)
        {
            await PublishPackageOperationSuccessToastAsync(
                successTitle,
                BuildLocalPackageOperationToastMessage(operationResult, successFallbackMessage));
        }
    }

    private async Task<PackageOperationResult> ApplyShellPackageChangesAsync(PackageOperationResult operationResult)
    {
        try
        {
            await _applyPackageLifecycleChangesAsync(operationResult.ImpactedPackageIds, CancellationToken.None);
            return operationResult with { RequiresAppRestart = false };
        }
        catch (Exception ex)
        {
            return operationResult with
            {
                RequiresAppRestart = true,
                Warnings = operationResult.Warnings.Concat([$"Package store updated, but the running shell did not apply the change: {ex.Message}"]).ToArray(),
            };
        }
    }

    private async Task ExecuteRegistryInstallAsync(
        Func<IRegistryApiClient, Task<RegistryPackageInstallExecutionResult>> executeAsync,
        string successTitle,
        string successFallbackMessage)
    {
        if (!TryCreateRegistryClient(out var registryClient))
        {
            return;
        }

        using (registryClient)
        {
            IsBusy = true;
            RegistryPackageInstallExecutionResult? result = null;
            try
            {
                ClearWarnings();
                StatusText = "Resolving registry install plan...";
                result = await executeAsync(registryClient);
                ApplyRegistryInstallResult(result);
                if (result.Success && result.ImpactedPackageIds.Count > 0)
                {
                    StatusText = "Applying package changes to the running shell...";
                    try
                    {
                        await _applyPackageLifecycleChangesAsync(result.ImpactedPackageIds, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        WarningLines.Add($"Package store updated, but the running shell did not apply the change: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result = RegistryPackageInstallExecutionResult.Failed(ex.Message);
                ApplyRegistryInstallResult(result);
            }
            finally
            {
                IsBusy = false;
            }

            await RefreshInstalledAsync(_selectedInstalledPackage?.PackageId);
            RefreshMarketplaceInstalledBadges();
            StatusText = result is null ? "Registry operation completed." : BuildRegistryResultStatusText(result);
            if (result is { Success: true } && result.ImpactedPackageIds.Count > 0)
            {
                await PublishPackageOperationSuccessToastAsync(
                    successTitle,
                    BuildRegistryPackageOperationToastMessage(result, successFallbackMessage));
            }
        }
    }

    private async Task RefreshInstalledAsync(string? preferredPackageId = null, PackageOperationResult? operationResult = null)
    {
        IsBusy = true;
        try
        {
            var sessionPackagesTask = _runtimeApiClient.GetSessionPackagesAsync();
            var installedPackagesTask = _runtimeApiClient.GetInstalledPackagesAsync();
            await Task.WhenAll(sessionPackagesTask, installedPackagesTask);

            _sessionPackages.Clear();
            _sessionPackages.AddRange(await sessionPackagesTask);
            _installedPackages.Clear();
            _installedPackages.AddRange(await installedPackagesTask);

            await ResolveAvailableUpdatesAsync();
            StatusText = BuildInstalledStatusText(operationResult);
            RebuildInstalledPackageList(preferredPackageId);
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

    private async Task RefreshMarketplaceAsync()
    {
        if (!TryCreateRegistryClient(out var registryClient))
        {
            return;
        }

        using (registryClient)
        {
            IsBusy = true;
            try
            {
                StatusText = "Searching marketplace...";
                await RefreshInstalledPackageStateOnlyAsync();
                var packages = await registryClient.SearchAsync(SearchText, skip: 0, take: 50);
                ReplaceItems(
                    MarketplacePackages,
                    packages.Select(package => new RegistryPackageSearchItemViewModel(
                        package,
                        GetInstalledPackage(package.PackageId)?.Version,
                        GetPackageUpdate(package.PackageId),
                        SelectMarketplacePackageAsync)).ToArray());
                OnPropertyChanged(nameof(HasMarketplacePackages));
                OnPropertyChanged(nameof(ShowNoMarketplacePackages));

                var selected = MarketplacePackages.FirstOrDefault(package =>
                        string.Equals(package.PackageId, _selectedMarketplacePackage?.PackageId, StringComparison.OrdinalIgnoreCase))
                    ?? MarketplacePackages.FirstOrDefault();
                if (selected is null)
                {
                    ClearMarketplaceSelection();
                    StatusText = "No marketplace packages matched the search.";
                    return;
                }

                await SelectMarketplacePackageAsync(selected);
                StatusText = $"Found {MarketplacePackages.Count} marketplace package(s).";
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
    }

    private async Task RefreshInstalledPackageStateOnlyAsync()
    {
        _installedPackages.Clear();
        _installedPackages.AddRange(await _runtimeApiClient.GetInstalledPackagesAsync());
        await ResolveAvailableUpdatesAsync();
    }

    private async Task ResolveAvailableUpdatesAsync()
    {
        _availableUpdates.Clear();
        if (_installedPackages.Count == 0 || !RegistryUrlHelper.TryParse(RegistryUrlText, out var registryUrl) || registryUrl is null)
        {
            NotifyUpdateStateChanged();
            return;
        }

        try
        {
            using var registryClient = new RegistryApiClient(registryUrl);
            var response = await registryClient.ResolveUpdatesAsync(
                new RegistryResolveUpdatesRequest(
                    _installedPackages.Select(package => new RegistryInstalledPackage(package.PackageId, package.Version)).ToArray()));
            _availableUpdates.AddRange(response.Updates);
        }
        catch (Exception ex)
        {
            WarningLines.Add($"Registry update check failed: {ex.Message}");
        }

        NotifyUpdateStateChanged();
    }

    private void RebuildInstalledPackageList(string? preferredPackageId = null)
    {
        var sessionById = _sessionPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var installedById = _installedPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var packageIds = sessionById.Keys.Concat(installedById.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase);

        var filteredPackages = packageIds
            .Select(packageId => new PackageCatalogItemViewModel(
                sessionById.GetValueOrDefault(packageId),
                installedById.GetValueOrDefault(packageId),
                GetPackageUpdate(packageId),
                CreatePackageIconUri(packageId, sessionById.GetValueOrDefault(packageId)?.Icon ?? installedById.GetValueOrDefault(packageId)?.Icon),
                SelectInstalledPackage))
            .Where(MatchesInstalledSearch)
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DisposeInstalledPackageItems();
        ReplaceItems(InstalledPackages, filteredPackages);
        NotifyListVisibilityChanged();
        NotifyPackageCountsChanged();

        var selectedItem = InstalledPackages
            .FirstOrDefault(item => string.Equals(item.PackageId, preferredPackageId, StringComparison.OrdinalIgnoreCase))
            ?? InstalledPackages.FirstOrDefault(item => string.Equals(item.PackageId, _selectedInstalledPackage?.PackageId, StringComparison.OrdinalIgnoreCase))
            ?? InstalledPackages.FirstOrDefault();

        if (selectedItem is null)
        {
            ClearInstalledSelection();
            return;
        }

        SelectInstalledPackage(selectedItem);
    }

    private bool MatchesInstalledSearch(PackageCatalogItemViewModel package)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return package.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || package.PackageId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || package.Version.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || package.SourceLabel.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectInstalledPackage(PackageCatalogItemViewModel item)
    {
        foreach (var package in InstalledPackages)
        {
            package.IsSelected = ReferenceEquals(package, item);
        }

        _selectedInstalledPackage = item;
        ObserveSelectedInstalledPackage(item);
        SelectedPackageTitle = item.DisplayName;
        SelectedPackageSubtitle = $"{item.PackageId} · v{item.Version} · {item.SourceLabel}";
        SelectedPackageStatus = item.StatusText;
        SelectedPackageSummary = item.HasUpdate
            ? $"Views: {item.ViewCount} · State: {item.StatusText} · Update available: {item.AvailableVersion}"
            : $"Views: {item.ViewCount} · State: {item.StatusText}";
        SelectedPackageHasError = !string.IsNullOrWhiteSpace(item.LastError);
        SelectedPackageError = item.LastError ?? string.Empty;
        SelectedPackageOperationHint = item.OperationHint;
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ClearInstalledSelection()
    {
        _selectedInstalledPackage = null;
        ObserveSelectedInstalledPackage(null);
        SelectedPackageTitle = "No package selected";
        SelectedPackageSubtitle = "No installed packages match the current filter.";
        SelectedPackageStatus = string.Empty;
        SelectedPackageSummary = "Adjust the filter or install packages to inspect session status.";
        SelectedPackageGlyph = "?";
        SelectedPackageIconImage = null;
        SelectedPackageIconLoadError = string.Empty;
        SelectedPackageHasError = false;
        SelectedPackageError = string.Empty;
        SelectedPackageOperationHint = "Install a .sunderpkg from disk or use the marketplace tab.";
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task SelectMarketplacePackageAsync(RegistryPackageSearchItemViewModel item)
    {
        foreach (var package in MarketplacePackages)
        {
            package.IsSelected = ReferenceEquals(package, item);
        }

        _selectedMarketplacePackage = item;
        ObserveSelectedInstalledPackage(null);
        SelectedPackageTitle = item.Name;
        SelectedPackageSubtitle = item.PackageId;
        SelectedPackageSummary = item.Summary ?? "No package summary provided.";
        SelectedPackageGlyph = item.Glyph;
        SelectedPackageIconImage = null;
        SelectedPackageIconLoadError = string.Empty;
        ApplyMarketplaceProfile(null);
        MarketplaceLatestVersion = item.LatestVersion ?? "-";
        MarketplaceInstalledVersion = item.InstalledVersion ?? "Not installed";
        MarketplaceSelectedVersion = "Latest";
        ClearWarnings();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();

        if (!TryCreateRegistryClient(out var registryClient))
        {
            return;
        }

        using (registryClient)
        {
            try
            {
                StatusText = $"Loading {item.PackageId}...";
                var package = await registryClient.GetPackageAsync(item.PackageId);
                ApplyMarketplaceProfile(package?.Profile);
                var versions = package?.Versions
                    .OrderByDescending(version => TryParseVersion(version.Version), VersionComparer.Instance)
                    .ThenByDescending(version => version.PublishedAtUtc)
                    .Select(version => new RegistryPackageVersionItemViewModel(version, SelectMarketplaceVersion))
                    .ToArray() ?? [];
                ReplaceItems(MarketplaceVersions, versions);
                OnPropertyChanged(nameof(HasMarketplaceVersions));
                OnPropertyChanged(nameof(ShowNoMarketplaceVersions));
                SelectMarketplaceVersion(MarketplaceVersions.FirstOrDefault(version => string.Equals(version.Version, item.LatestVersion, StringComparison.OrdinalIgnoreCase))
                    ?? MarketplaceVersions.FirstOrDefault());
                StatusText = package is null
                    ? $"Package '{item.PackageId}' was not found."
                    : $"Loaded {versions.Length} version(s) for {item.PackageId}.";
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
            }
        }
    }

    private void SelectMarketplaceVersion(RegistryPackageVersionItemViewModel? item)
    {
        foreach (var version in MarketplaceVersions)
        {
            version.IsSelected = ReferenceEquals(version, item);
        }

        _selectedMarketplaceVersion = item;
        MarketplaceSelectedVersion = item?.Version ?? "Latest";
        ClearWarnings();
        if (item is { IsYanked: true })
        {
            WarningLines.Add("Selected version is yanked and cannot be installed from the registry.");
        }

        if (!string.IsNullOrWhiteSpace(item?.DeprecatedMessage))
        {
            WarningLines.Add($"Selected version is deprecated: {item.DeprecatedMessage}");
        }

        OnPropertyChanged(nameof(HasWarnings));
        NotifyCommandStateChanged();
    }

    private void ClearMarketplaceSelection()
    {
        _selectedMarketplacePackage = null;
        _selectedMarketplaceVersion = null;
        MarketplaceVersions.Clear();
        ApplyMarketplaceProfile(null);
        SelectedPackageTitle = "No package selected";
        SelectedPackageSubtitle = "No marketplace packages match the current search.";
        SelectedPackageSummary = "Adjust the search or registry URL to browse packages.";
        SelectedPackageGlyph = "?";
        SelectedPackageIconImage = null;
        SelectedPackageIconLoadError = string.Empty;
        MarketplaceLatestVersion = "-";
        MarketplaceInstalledVersion = "Not installed";
        MarketplaceSelectedVersion = "Latest";
        ClearWarnings();
        OnPropertyChanged(nameof(HasMarketplaceVersions));
        OnPropertyChanged(nameof(ShowNoMarketplaceVersions));
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ApplyRegistryInstallResult(RegistryPackageInstallExecutionResult result)
    {
        ReplaceItems(WarningLines, result.Warnings.Concat(result.Errors).ToArray());
        OnPropertyChanged(nameof(HasWarnings));
    }

    private string BuildRegistryResultStatusText(RegistryPackageInstallExecutionResult result)
    {
        if (!result.Success)
        {
            return result.Errors.FirstOrDefault() ?? result.Message;
        }

        var text = result.Message;
        if (WarningLines.Count > 0)
        {
            text += " Review warnings below.";
        }

        return text;
    }

    private async ValueTask PublishPackageOperationSuccessToastAsync(string title, string message)
    {
        if (_notificationCenter is null)
        {
            return;
        }

        await _notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                title,
                message,
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Success));
    }

    private static string BuildLocalPackageOperationToastMessage(PackageOperationResult result, string fallback)
        => string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message.Trim();

    private static string BuildRegistryPackageOperationToastMessage(RegistryPackageInstallExecutionResult result, string fallback)
    {
        if (result.PlanItems.Count == 1)
        {
            var item = result.PlanItems[0];
            if (item.CurrentVersion is null)
            {
                return $"{item.PackageId} {item.Version} was installed.";
            }

            if (string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase))
            {
                return $"{item.PackageId} {item.Version} was reinstalled.";
            }

            return $"{item.PackageId} was updated from {item.CurrentVersion} to {item.Version}.";
        }

        var packageChangeCount = result.PlanItems
            .Select(item => item.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (packageChangeCount > 0)
        {
            return $"{packageChangeCount} package change(s) completed.";
        }

        return string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message.Trim();
    }

    private string BuildInstalledStatusText(PackageOperationResult? operationResult)
    {
        if (operationResult is null)
        {
            var status = $"{InstalledPackageCount} installed · {ActivePackageCount} active · {DisabledPackageCount} disabled · {FailedPackageCount} failed this session";
            if (AvailableUpdateCount > 0)
            {
                status += $" · {AvailableUpdateCount} update(s) available";
            }

            return status;
        }

        var message = operationResult.Success
            ? operationResult.Message ?? "Package operation completed."
            : operationResult.Errors.FirstOrDefault() ?? operationResult.Message ?? "Package operation failed.";

        if (operationResult.RequiresAppRestart)
        {
            message += " Restart Sunder to apply package UI changes.";
        }

        if (operationResult.Warnings.Count > 0)
        {
            message += " " + string.Join(" ", operationResult.Warnings);
        }

        return message;
    }

    private bool TryCreateRegistryClient(out IRegistryApiClient registryClient)
    {
        registryClient = null!;
        if (!RegistryUrlHelper.TryParse(RegistryUrlText, out var registryUrl) || registryUrl is null)
        {
            StatusText = "Enter a valid HTTP or HTTPS registry URL.";
            return false;
        }

        registryClient = new RegistryApiClient(registryUrl);
        return true;
    }

    private void RefreshMarketplaceInstalledBadges()
    {
        foreach (var package in MarketplacePackages)
        {
            package.InstalledVersion = GetInstalledPackage(package.PackageId)?.Version;
            package.Update = GetPackageUpdate(package.PackageId);
        }

        if (_selectedMarketplacePackage is not null)
        {
            MarketplaceInstalledVersion = GetInstalledPackage(_selectedMarketplacePackage.PackageId)?.Version ?? "Not installed";
        }

        NotifyCommandStateChanged();
    }

    private InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedPackages.FirstOrDefault(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    private Uri? CreatePackageIconUri(string packageId, PackageIconDescriptor? icon)
    {
        if (string.IsNullOrWhiteSpace(icon?.AssetPath))
        {
            return null;
        }

        try
        {
            return _runtimeApiClient.CreatePackageAssetUri(packageId, icon.AssetPath!);
        }
        catch
        {
            return null;
        }
    }

    private RegistryPackageUpdate? GetSelectedInstalledPackageUpdate()
        => _selectedInstalledPackage is null ? null : GetPackageUpdate(_selectedInstalledPackage.PackageId);

    private RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _availableUpdates.FirstOrDefault(update => string.Equals(update.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    private void ClearWarnings()
    {
        WarningLines.Clear();
        OnPropertyChanged(nameof(HasWarnings));
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
        OnPropertyChanged(nameof(HasMarketplaceReadme));
        OnPropertyChanged(nameof(HasMarketplaceProfileLinks));
        OnPropertyChanged(nameof(HasMarketplaceProfileMedia));
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
        OnPropertyChanged(nameof(CanInstallSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUninstallSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUpdateSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUpdateAllPackages));
    }

    private static Version? TryParseVersion(string value)
        => Version.TryParse(value.Split('-', '+')[0], out var version) ? version : null;

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void ApplyMarketplaceProfile(RegistryPackageProfile? profile)
    {
        if (!string.IsNullOrWhiteSpace(profile?.ShortDescription))
        {
            SelectedPackageSummary = profile.ShortDescription;
        }

        MarketplaceReadmeMarkdownBuilder.Clear();
        if (!string.IsNullOrWhiteSpace(profile?.ReadmeMarkdown))
        {
            MarketplaceReadmeMarkdownBuilder.Append(profile.ReadmeMarkdown);
        }

        ReplaceItems(MarketplaceProfileLinks, BuildProfileLinks(profile));
        ReplaceItems(MarketplaceProfileMetadata, BuildProfileMetadata(profile));
        ReplaceItems(MarketplaceProfileTags, BuildProfileTags(profile));
        DisposeMarketplaceProfileMedia();
        ReplaceItems(MarketplaceProfileMedia, profile?.Media
            .OrderBy(media => media.SortOrder)
            .Select(media => new RegistryPackageMediaItemViewModel(media, OpenMarketplaceImageGalleryAsync))
            .ToArray() ?? []);
        OnPropertyChanged(nameof(HasMarketplaceReadme));
        OnPropertyChanged(nameof(HasMarketplaceProfileLinks));
        OnPropertyChanged(nameof(HasMarketplaceProfileMetadata));
        OnPropertyChanged(nameof(HasMarketplaceProfileTags));
        OnPropertyChanged(nameof(HasMarketplaceProfile));
        OnPropertyChanged(nameof(HasMarketplaceProfileMedia));
    }

    private static IReadOnlyList<RegistryPackageProfileLinkViewModel> BuildProfileLinks(RegistryPackageProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var links = new List<RegistryPackageProfileLinkViewModel>();
        AddProfileLink(links, "Website", profile.WebsiteUrl);
        AddProfileLink(links, "Source", profile.SourceUrl);
        AddProfileLink(links, "Issues", profile.IssueTrackerUrl);
        return links;
    }

    private static IReadOnlyList<RegistryPackageProfileMetadataItemViewModel> BuildProfileMetadata(RegistryPackageProfile? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.License))
        {
            return [];
        }

        return [new RegistryPackageProfileMetadataItemViewModel("License", profile.License.Trim())];
    }

    private static IReadOnlyList<string> BuildProfileTags(RegistryPackageProfile? profile)
        => profile?.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private static void AddProfileLink(
        ICollection<RegistryPackageProfileLinkViewModel> links,
        string label,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var navigateUri))
        {
            return;
        }

        links.Add(new RegistryPackageProfileLinkViewModel(label, navigateUri));
    }

    public void Dispose()
    {
        DisposeInstalledPackageItems();
        DisposeMarketplaceProfileMedia();
        _runtimeApiClient.Dispose();
    }

    private void DisposeInstalledPackageItems()
    {
        foreach (var package in InstalledPackages)
        {
            package.Dispose();
        }
    }

    private void ObserveSelectedInstalledPackage(PackageCatalogItemViewModel? item)
    {
        if (_observedSelectedInstalledPackage is not null)
        {
            _observedSelectedInstalledPackage.PropertyChanged -= SelectedInstalledPackage_OnPropertyChanged;
        }

        _observedSelectedInstalledPackage = item;
        if (_observedSelectedInstalledPackage is not null)
        {
            _observedSelectedInstalledPackage.PropertyChanged += SelectedInstalledPackage_OnPropertyChanged;
        }

        RefreshSelectedInstalledPackageIcon();
    }

    private void SelectedInstalledPackage_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageCatalogItemViewModel.IconImage) or nameof(PackageCatalogItemViewModel.IconLoadError))
        {
            RefreshSelectedInstalledPackageIcon();
        }
    }

    private void RefreshSelectedInstalledPackageIcon()
    {
        SelectedPackageGlyph = _observedSelectedInstalledPackage?.Glyph ?? "?";
        SelectedPackageIconImage = _observedSelectedInstalledPackage?.IconImage;
        SelectedPackageIconLoadError = _observedSelectedInstalledPackage?.IconLoadError ?? string.Empty;
    }

    private async Task OpenMarketplaceImageGalleryAsync(RegistryPackageMediaItemViewModel media)
    {
        var items = MarketplaceProfileMedia.ToArray();
        var index = Array.IndexOf(items, media);
        if (index < 0 || MarketplaceImageGalleryRequested is null)
        {
            return;
        }

        await MarketplaceImageGalleryRequested.Invoke(items, index);
    }

    private void DisposeMarketplaceProfileMedia()
    {
        foreach (var media in MarketplaceProfileMedia)
        {
            media.Dispose();
        }
    }

    private sealed class VersionComparer : IComparer<Version?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(Version? x, Version? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return x.CompareTo(y);
        }
    }
}

public sealed partial class PackageCatalogItemViewModel : ViewModelBase, IDisposable
{
    private readonly Action<PackageCatalogItemViewModel> _onSelect;
    private bool _isDisposed;

    public PackageCatalogItemViewModel(
        SessionPackageDescriptor? sessionPackage,
        InstalledPackageDescriptor? installedPackage,
        RegistryPackageUpdate? update,
        Uri? iconUri,
        Action<PackageCatalogItemViewModel> onSelect)
    {
        PackageId = sessionPackage?.PackageId ?? installedPackage!.PackageId;
        DisplayName = sessionPackage?.DisplayName ?? installedPackage!.Name;
        Version = sessionPackage?.Version ?? installedPackage!.Version;
        Glyph = ToGlyph(sessionPackage?.Icon ?? installedPackage?.Icon, DisplayName);
        IconUri = iconUri;
        StatusText = ToStatusText(sessionPackage, installedPackage);
        IsEnabled = sessionPackage?.IsEnabled ?? installedPackage?.IsEnabled ?? false;
        IsFailed = sessionPackage?.Readiness == PackageReadinessState.Failed;
        IsInstalled = installedPackage is not null;
        SourceLabel = installedPackage is null ? "Dev package" : "Installed package";
        ViewCount = sessionPackage?.Views.Count ?? 0;
        LastError = sessionPackage?.LastError;
        FailureOrigin = sessionPackage?.FailureOrigin;
        CanEnable = installedPackage is { IsEnabled: false };
        CanDisable = installedPackage is { IsEnabled: true };
        CanUninstall = installedPackage is not null;
        AvailableVersion = update?.AvailableVersion;
        DeprecatedUpdateMessage = update?.DeprecatedMessage;
        OperationHint = ToOperationHint(installedPackage, update);
        _onSelect = onSelect;

        if (IconUri is not null)
        {
            _ = LoadIconAsync(IconUri);
        }
    }

    public string PackageId { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public string Glyph { get; }

    public Uri? IconUri { get; }

    public bool HasIconImage => IconImage is not null;

    public bool ShowGlyphFallback => IconImage is null;

    public bool HasIconLoadError => !string.IsNullOrWhiteSpace(IconLoadError);

    public string StatusText { get; }

    public bool IsEnabled { get; }

    public bool IsFailed { get; }

    public bool IsInstalled { get; }

    public string SourceLabel { get; }

    public int ViewCount { get; }

    public string? LastError { get; }

    public PackageFailureOrigin? FailureOrigin { get; }

    public bool CanEnable { get; }

    public bool CanDisable { get; }

    public bool CanUninstall { get; }

    public string? AvailableVersion { get; }

    public string? DeprecatedUpdateMessage { get; }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);

    public string UpdateText => HasUpdate ? $"Update {AvailableVersion}" : string.Empty;

    public string OperationHint { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private IImage? _iconImage;

    [ObservableProperty]
    private string _iconLoadError = string.Empty;

    partial void OnIconImageChanged(IImage? value)
    {
        OnPropertyChanged(nameof(HasIconImage));
        OnPropertyChanged(nameof(ShowGlyphFallback));
    }

    partial void OnIconLoadErrorChanged(string value)
        => OnPropertyChanged(nameof(HasIconLoadError));

    [RelayCommand]
    private void Select() => _onSelect(this);

    public void Dispose()
    {
        _isDisposed = true;
        if (IconImage is IDisposable disposable)
        {
            disposable.Dispose();
        }

        IconImage = null;
    }

    private async Task LoadIconAsync(Uri iconUri)
    {
        var result = await PackageIconImageLoader.LoadAsync(iconUri);
        await RunOnUiThreadAsync(() =>
        {
            if (_isDisposed)
            {
                if (result.Image is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                return;
            }

            IconLoadError = result.Error ?? string.Empty;
            IconImage = result.Image;
        });
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private static string ToGlyph(PackageIconDescriptor? icon, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(icon?.Glyph))
        {
            return icon.Glyph!;
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : displayName[0].ToString().ToUpperInvariant();
    }

    private static string ToStatusText(SessionPackageDescriptor? sessionPackage, InstalledPackageDescriptor? installedPackage)
    {
        if (sessionPackage is not null)
        {
            if (sessionPackage.IsEnabled)
            {
                return sessionPackage.Readiness switch
                {
                    PackageReadinessState.Ready => "Ready",
                    PackageReadinessState.NeedsConfiguration => "Needs configuration",
                    PackageReadinessState.Degraded => "Degraded",
                    PackageReadinessState.Failed => "Failed",
                    _ => "Unknown",
                };
            }

            if (sessionPackage.Readiness == PackageReadinessState.Disabled)
            {
                return "Disabled";
            }

            var origin = sessionPackage.FailureOrigin switch
            {
                PackageFailureOrigin.AppActivation => "app activation",
                PackageFailureOrigin.AppHostedView => "hosted view",
                PackageFailureOrigin.AppUnhandledUi => "UI interaction",
                PackageFailureOrigin.RuntimeActivation => "runtime activation",
                PackageFailureOrigin.RuntimeConfiguration => "runtime configuration",
                PackageFailureOrigin.RuntimeAuthentication => "runtime auth",
                _ => "package fault",
            };

            return $"Failed · {origin}";
        }

        if (installedPackage is not null)
        {
            return installedPackage.IsEnabled ? "Installed" : "Disabled";
        }

        return "Unknown";
    }

    private static string ToOperationHint(InstalledPackageDescriptor? installedPackage, RegistryPackageUpdate? update)
    {
        if (installedPackage is null)
        {
            return "Dev packages are managed by the current app launch arguments.";
        }

        if (update is not null)
        {
            return $"Update available: {update.CurrentVersion} -> {update.AvailableVersion}.";
        }

        return installedPackage.IsEnabled
            ? "Disable or uninstall this package. Running shell changes apply live when possible."
            : "Enable or uninstall this package. Running shell changes apply live when possible.";
    }
}

public sealed partial class RegistryPackageSearchItemViewModel : ViewModelBase
{
    private readonly Func<RegistryPackageSearchItemViewModel, Task> _onSelectAsync;

    public RegistryPackageSearchItemViewModel(
        RegistryPackageSummary package,
        string? installedVersion,
        RegistryPackageUpdate? update,
        Func<RegistryPackageSearchItemViewModel, Task> onSelectAsync)
    {
        PackageId = package.PackageId;
        Name = package.Name;
        Glyph = ToGlyph(package.Name);
        Summary = package.Summary;
        LatestVersion = package.LatestVersion;
        IsYanked = package.IsYanked;
        InstalledVersion = installedVersion;
        Update = update;
        _onSelectAsync = onSelectAsync;
    }

    public string PackageId { get; }

    public string Name { get; }

    public string Glyph { get; }

    public string? Summary { get; }

    public string? LatestVersion { get; }

    public bool IsYanked { get; }

    public string LatestVersionText => LatestVersion ?? "No latest";

    public string InstalledVersionText => InstalledVersion is null ? "Not installed" : $"Installed {InstalledVersion}";

    public bool HasUpdate => Update is not null;

    public string ActionText => HasUpdate ? $"Update {Update!.AvailableVersion}" : InstalledVersion is null ? "Install" : "Installed";

    [ObservableProperty]
    private string? _installedVersion;

    [ObservableProperty]
    private RegistryPackageUpdate? _update;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnInstalledVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(InstalledVersionText));
        OnPropertyChanged(nameof(ActionText));
    }

    partial void OnUpdateChanged(RegistryPackageUpdate? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ActionText));
    }

    [RelayCommand]
    private async Task SelectAsync() => await _onSelectAsync(this);

    private static string ToGlyph(string name)
        => string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Trim()[0].ToString().ToUpperInvariant();
}

public sealed partial class RegistryPackageVersionItemViewModel : ViewModelBase
{
    private readonly Action<RegistryPackageVersionItemViewModel> _onSelect;

    public RegistryPackageVersionItemViewModel(
        RegistryPackageVersionSummary version,
        Action<RegistryPackageVersionItemViewModel> onSelect)
    {
        Version = version.Version;
        IsYanked = version.IsYanked;
        DeprecatedMessage = version.DeprecatedMessage;
        PublishedAtText = version.PublishedAtUtc.ToLocalTime().ToString("g");
        StatusText = ToStatusText(version);
        _onSelect = onSelect;
    }

    public string Version { get; }

    public bool IsYanked { get; }

    public string? DeprecatedMessage { get; }

    public string PublishedAtText { get; }

    public string StatusText { get; }

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => _onSelect(this);

    private static string ToStatusText(RegistryPackageVersionSummary version)
    {
        if (version.IsYanked)
        {
            return "Yanked";
        }

        return string.IsNullOrWhiteSpace(version.DeprecatedMessage) ? "Available" : "Deprecated";
    }
}

public sealed record RegistryPackageProfileLinkViewModel(string Label, Uri NavigateUri)
{
    public string DisplayUrl => NavigateUri.AbsoluteUri;
}

public sealed record RegistryPackageProfileMetadataItemViewModel(string Label, string Value);

public sealed partial class RegistryPackageMediaItemViewModel : ViewModelBase, IDisposable
{
    private static readonly HttpClient ImageHttpClient = new();
    private readonly Func<RegistryPackageMediaItemViewModel, Task> _onSelectAsync;
    private Task? _loadTask;

    public RegistryPackageMediaItemViewModel(
        RegistryPackageMedia media,
        Func<RegistryPackageMediaItemViewModel, Task> onSelectAsync)
    {
        Url = media.Url;
        Caption = media.AltText ?? media.FileName;
        FileName = media.FileName;
        Size = media.Size;
        _onSelectAsync = onSelectAsync;
        _ = EnsureImageLoadedAsync();
    }

    public string Url { get; }

    public string Caption { get; }

    public string FileName { get; }

    public long Size { get; }

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isImageLoading;

    [RelayCommand]
    private async Task SelectAsync()
    {
        await EnsureImageLoadedAsync();
        await _onSelectAsync(this);
    }

    public async Task EnsureImageLoadedAsync(CancellationToken cancellationToken = default)
    {
        _loadTask ??= LoadImageAsync(cancellationToken);
        await _loadTask;
    }

    public void Dispose()
    {
        Image?.Dispose();
        Image = null;
    }

    private async Task LoadImageAsync(CancellationToken cancellationToken)
    {
        await SetImageLoadingAsync(true);
        try
        {
            using var response = await ImageHttpClient.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            var bitmap = new Bitmap(memory);
            await RunOnUiThreadAsync(() => Image = bitmap);
        }
        catch
        {
            await RunOnUiThreadAsync(() => Image = null);
        }
        finally
        {
            await SetImageLoadingAsync(false);
        }
    }

    private Task SetImageLoadingAsync(bool value)
        => RunOnUiThreadAsync(() => IsImageLoading = value);

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }
}
