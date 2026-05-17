using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public enum PackageWindowMode
{
    Marketplace,
    Installed,
}

public sealed partial class PackagesWindowViewModel : ViewModelBase, IDisposable
{
    private const int MarketplaceSearchThrottleDelayMilliseconds = 300;

    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly PackageOperationService? _packageOperationService;
    private readonly PackageRegistryClientProvider _registryClientProvider;
    private readonly InstalledPackagesPaneViewModel _installedPackages;
    private readonly MarketplacePackagesPaneViewModel _marketplace;
    private readonly PackagesOperationCommandCoordinator _operationCommands;
    private readonly PackagesSelectedOperationCommands _selectedOperationCommands;
    private readonly PackageOperationStatePresenter _operationState;
    private readonly PackageWarningsViewModel _warnings = new();
    private readonly SelectedPackageIconObserver _selectedPackageIconObserver;
    private readonly MarketplaceSearchScheduler _marketplaceSearchScheduler;
    private int _marketplaceSearchVersion;
    private bool _disposed;
    private bool _isApplyingModeSearchText;
    private string _marketplaceSearchText = string.Empty;
    private string _installedSearchText = string.Empty;
    private PackageCatalogItemViewModel? _selectedInstalledPackage;
    private RegistryPackageSearchItemViewModel? _selectedMarketplacePackage;
    private RegistryPackageVersionItemViewModel? _selectedMarketplaceVersion;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? MarketplaceImageGalleryRequested
    {
        add => _marketplace.ImageGalleryRequested += value;
        remove => _marketplace.ImageGalleryRequested -= value;
    }

    public PackagesWindowViewModel(
        IRuntimeApiClient runtimeApiClient,
        IPackageArchivePicker packageArchivePicker,
        Func<IReadOnlyList<string>, CancellationToken, Task>? applyPackageLifecycleChangesAsync = null,
        PackageOperationService? packageOperationService = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        RegistryPackageInstallService? registryInstallService = null,
        NotificationCenterService? notificationCenter = null,
        Func<Uri, IRegistryApiClient>? registryClientFactory = null,
        TimeSpan? marketplaceSearchThrottleDelay = null,
        double backgroundProcessPopoverWidth = ShellState.DefaultBackgroundProcessPopoverWidth,
        double backgroundProcessPopoverHeight = ShellState.DefaultBackgroundProcessPopoverHeight,
        Action<double, double>? persistBackgroundProcessPopoverSize = null)
    {
        _runtimeApiClient = runtimeApiClient;
        _packageOperationService = packageOperationService;
        var resolvedRegistryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        var resolvedApplyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync ?? ((_, _) => Task.CompletedTask);
        _registryClientProvider = new PackageRegistryClientProvider(
            () => RegistryUrlText,
            registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl)));
        _installedPackages = new InstalledPackagesPaneViewModel(
            new PackagesInstalledCatalog(_runtimeApiClient, _registryClientProvider),
            CreatePackageIconUri,
            SelectInstalledPackage);
        _marketplace = new MarketplacePackagesPaneViewModel(new PackagesMarketplaceCatalog(_registryClientProvider));
        _operationState = new PackageOperationStatePresenter(_packageOperationService);
        _operationCommands = new PackagesOperationCommandCoordinator(
            _runtimeApiClient,
            packageArchivePicker,
            _registryClientProvider,
            resolvedRegistryInstallService,
            _packageOperationService,
            resolvedApplyPackageLifecycleChangesAsync,
            notificationCenter,
            () => IsBusy,
            value => IsBusy = value,
            value => StatusText = value,
            ClearWarnings,
            ReplaceWarningLines,
            AddWarningLine,
            () => WarningLines.Count,
            RefreshInstalledAsync,
            RefreshMarketplaceInstalledBadges,
            RefreshPackageOperationState,
            () => _installedPackages.IsDirty = true);
        _selectedOperationCommands = new PackagesSelectedOperationCommands(
            _operationCommands,
            _operationState,
            () => Mode,
            value => Mode = value,
            () => _selectedInstalledPackage,
            () => _selectedMarketplacePackage,
            () => _selectedMarketplaceVersion,
            () => AvailableUpdateCount,
            GetSelectedInstalledPackageUpdate,
            GetPackageUpdate,
            RefreshPackageOperationState,
            value => StatusText = value);
        _selectedPackageIconObserver = new SelectedPackageIconObserver(ApplySelectedPackageIconState);
        _marketplaceSearchScheduler = new MarketplaceSearchScheduler(
            RefreshMarketplaceAsync,
            marketplaceSearchThrottleDelay ?? TimeSpan.FromMilliseconds(MarketplaceSearchThrottleDelayMilliseconds));
        PackageProcesses = backgroundProcessQueue is null
            ? BackgroundProcessMonitorViewModel.Empty
            : new BackgroundProcessMonitorViewModel(
                backgroundProcessQueue,
                BackgroundProcessIndicator.Packages,
                "No package processes.",
                backgroundProcessPopoverWidth,
                backgroundProcessPopoverHeight,
                persistBackgroundProcessPopoverSize);
        RegistryUrlText = RegistryUrlHelper.DefaultRegistryUrl.ToString();
        if (_packageOperationService is not null)
        {
            _packageOperationService.OperationChanged += PackageOperationService_OnOperationChanged;
        }
    }

    public ObservableCollection<PackageCatalogItemViewModel> InstalledPackages => _installedPackages.Packages;

    public ObservableCollection<RegistryPackageSearchItemViewModel> MarketplacePackages => _marketplace.Packages;

    public ObservableCollection<RegistryPackageVersionItemViewModel> MarketplaceVersions => _marketplace.Versions;

    public ObservableCollection<RegistryPackageProfileLinkViewModel> MarketplaceProfileLinks => _marketplace.ProfileLinks;

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> MarketplaceProfileMetadata => _marketplace.ProfileMetadata;

    public ObservableCollection<string> MarketplaceProfileTags => _marketplace.ProfileTags;

    public ObservableCollection<RegistryPackageMediaItemViewModel> MarketplaceProfileMedia => _marketplace.ProfileMedia;

    public LiveMarkdown.Avalonia.ObservableStringBuilder MarketplaceReadmeMarkdownBuilder => _marketplace.ReadmeMarkdownBuilder;

    public ObservableCollection<string> WarningLines => _warnings.Lines;

    public BackgroundProcessMonitorViewModel PackageProcesses { get; }

    public PackageCatalogItemViewModel? SelectedInstalledPackage
    {
        get => _selectedInstalledPackage;
        set
        {
            if (value is null || ReferenceEquals(_selectedInstalledPackage, value))
            {
                return;
            }

            SelectInstalledPackage(value);
        }
    }

    public RegistryPackageSearchItemViewModel? SelectedMarketplacePackage
    {
        get => _selectedMarketplacePackage;
        set
        {
            if (value is null || ReferenceEquals(_selectedMarketplacePackage, value))
            {
                return;
            }

            _ = SelectMarketplacePackageAsync(value);
        }
    }

    public RegistryPackageVersionItemViewModel? SelectedMarketplaceVersion
    {
        get => _selectedMarketplaceVersion;
        set
        {
            if (ReferenceEquals(_selectedMarketplaceVersion, value))
            {
                return;
            }

            SelectMarketplaceVersion(value);
        }
    }

    public bool IsMarketplaceMode => Mode == PackageWindowMode.Marketplace;

    public bool IsInstalledMode => Mode == PackageWindowMode.Installed;

    public bool HasInstalledPackages => _installedPackages.HasPackages;

    public bool ShowNoInstalledPackages => IsInstalledMode && !HasInstalledPackages;

    public bool HasMarketplacePackages => _marketplace.HasPackages;

    public bool ShowNoMarketplacePackages => IsMarketplaceMode && !HasMarketplacePackages;

    public bool HasMarketplaceVersions => _marketplace.HasVersions;

    public bool ShowNoMarketplaceVersions => !HasMarketplaceVersions;

    public bool HasMarketplaceReadme => _marketplace.HasReadme;

    public bool HasMarketplaceProfileLinks => _marketplace.HasProfileLinks;

    public bool HasMarketplaceProfileMetadata => _marketplace.HasProfileMetadata;

    public bool HasMarketplaceProfileTags => _marketplace.HasProfileTags;

    public bool HasMarketplaceProfile => _marketplace.HasProfile;

    public bool HasMarketplaceProfileMedia => _marketplace.HasProfileMedia;

    public bool HasWarnings => _warnings.HasWarnings;

    public bool ShowInstalledDetails => IsInstalledMode && _selectedInstalledPackage is not null;

    public bool ShowMarketplaceDetails => IsMarketplaceMode && _selectedMarketplacePackage is not null;

    public bool ShowNoSelection => !ShowInstalledDetails && !ShowMarketplaceDetails;

    public bool ShowSelectedPackageIcon => ShowInstalledDetails || ShowMarketplaceDetails;

    public bool SelectedPackageHasIconImage => SelectedPackageIconImage is not null;

    public bool SelectedPackageShowGlyphFallback => SelectedPackageIconImage is null;

    public bool SelectedPackageHasIconLoadError => !string.IsNullOrWhiteSpace(SelectedPackageIconLoadError);

    public bool ShowSelectedPackageOperationStatus => SelectedPackageHasActiveOperation;

    public bool ShowCancelSelectedPackageOperation => SelectedPackageHasActiveOperation && SelectedPackageOperationCanCancel;

    private bool HasActivePackageStoreOperation => _operationState.HasActivePackageStoreOperation;

    public bool CanRefresh => !IsBusy;

    public bool CanInstallPackage => !IsBusy;

    public bool CanEnableSelectedPackage => !IsBusy && !HasActivePackageStoreOperation && IsInstalledMode && _selectedInstalledPackage?.CanEnable == true;

    public bool CanDisableSelectedPackage => !IsBusy && !HasActivePackageStoreOperation && IsInstalledMode && _selectedInstalledPackage?.CanDisable == true;

    public bool ShowEnableSelectedPackage => IsInstalledMode && _selectedInstalledPackage?.CanEnable == true;

    public bool ShowDisableSelectedPackage => IsInstalledMode && _selectedInstalledPackage?.CanDisable == true;

    public bool CanUninstallSelectedPackage => !IsBusy && IsInstalledMode && _selectedInstalledPackage?.CanUninstall == true && !SelectedPackageHasActiveOperation;

    public bool CanUpdateSelectedInstalledPackage => !IsBusy && IsInstalledMode && GetSelectedInstalledPackageUpdate() is not null && !SelectedPackageHasActiveOperation;

    public bool ShowUpdateSelectedInstalledPackage => IsInstalledMode && GetSelectedInstalledPackageUpdate() is not null;

    public bool IsSelectedMarketplacePackageInstalled => _selectedMarketplacePackage?.InstalledVersion is not null;

    public bool ShowMarketplaceInstallAction => IsMarketplaceMode && _selectedMarketplacePackage is not null && !IsSelectedMarketplacePackageInstalled;

    public bool ShowMarketplaceInstalledActions => IsMarketplaceMode && IsSelectedMarketplacePackageInstalled;

    public bool ShowMarketplaceInstallButton => ShowMarketplaceInstallAction && !SelectedPackageHasActiveOperation;

    public bool ShowMarketplaceUninstallButton => ShowMarketplaceInstalledActions && !SelectedPackageHasActiveOperation;

    public bool ShowMarketplaceUpdateButton => ShowMarketplaceInstalledActions && !SelectedPackageHasActiveOperation;

    public bool CanInstallSelectedMarketplacePackage => ShowMarketplaceInstallAction
        && _selectedMarketplacePackage is { IsYanked: false }
        && _selectedMarketplaceVersion is { IsYanked: false }
        && !SelectedPackageHasActiveOperation;

    public bool CanUninstallSelectedMarketplacePackage => ShowMarketplaceInstalledActions && !SelectedPackageHasActiveOperation;

    public bool CanUpdateSelectedMarketplacePackage => IsMarketplaceMode && _selectedMarketplacePackage?.HasUpdate == true && !SelectedPackageHasActiveOperation;

    public bool CanUpdateAllPackages => !IsBusy && AvailableUpdateCount > 0;

    public bool ShowUpdateAllPackages => AvailableUpdateCount > 0;

    public string SearchPlaceholder => IsMarketplaceMode ? "Search marketplace packages" : "Search installed and session packages";

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public int InstalledPackageCount => _installedPackages.InstalledPackageCount;

    public int ActivePackageCount => _installedPackages.ActivePackageCount;

    public int DisabledPackageCount => _installedPackages.DisabledPackageCount;

    public int FailedPackageCount => _installedPackages.FailedPackageCount;

    public int AvailableUpdateCount => _installedPackages.AvailableUpdateCount;

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
    private bool _selectedPackageHasActiveOperation;

    [ObservableProperty]
    private bool _selectedPackageOperationCanCancel;

    [ObservableProperty]
    private bool _selectedPackageOperationIsIndeterminate = true;

    [ObservableProperty]
    private double _selectedPackageOperationProgressPercent;

    [ObservableProperty]
    private string _selectedPackageOperationStatusText = string.Empty;

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
        ApplySearchTextForCurrentMode();
        NotifyListVisibilityChanged();
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandStateChanged();

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        if (_isApplyingModeSearchText)
        {
            return;
        }

        if (IsInstalledMode)
        {
            _installedSearchText = value;
            RebuildInstalledPackageList(_selectedInstalledPackage?.PackageId);
            return;
        }

        if (IsMarketplaceMode)
        {
            _marketplaceSearchText = value;
            QueueMarketplaceSearch();
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

    partial void OnSelectedPackageHasActiveOperationChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSelectedPackageOperationStatus));
        OnPropertyChanged(nameof(ShowCancelSelectedPackageOperation));
        NotifyCommandStateChanged();
    }

    partial void OnSelectedPackageOperationCanCancelChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCancelSelectedPackageOperation));
        NotifyCommandStateChanged();
    }

    public bool ShowNoInstalledPackageError => !SelectedPackageHasError;

    public async Task InitializeAsync()
    {
        if (_installedPackages.IsEmpty)
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
        ApplySearchTextForCurrentMode();
        ClearWarnings();
        if (MarketplacePackages.Count == 0)
        {
            await RefreshMarketplaceAsync();
            return;
        }

        var selected = _marketplace.ResolvePackageSelection(_selectedMarketplacePackage?.PackageId);
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
        if (_installedPackages.IsDirty || InstalledPackages.Count == 0)
        {
            await RefreshInstalledAsync(_selectedInstalledPackage?.PackageId);
            return;
        }

        RebuildInstalledPackageList(_selectedInstalledPackage?.PackageId);
    }

    private void ApplySearchTextForCurrentMode()
    {
        var value = IsMarketplaceMode ? _marketplaceSearchText : _installedSearchText;
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
            RebuildInstalledPackageList(_selectedInstalledPackage?.PackageId);
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

        await RefreshInstalledAsync(_selectedInstalledPackage?.PackageId);
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
        ApplySelectedPackageDetails(PackageSelectionDetails.FromMarketplace(item));
        ApplyMarketplaceProfile(null);
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
                StatusText = details.ErrorMessage ?? "Package details failed to load.";
                return;
            }

            ApplyMarketplaceProfile(details.Profile);
            _marketplace.ReplaceVersions(details.Versions);
            OnPropertyChanged(nameof(HasMarketplaceVersions));
            OnPropertyChanged(nameof(ShowNoMarketplaceVersions));
            SelectMarketplaceVersion(MarketplaceVersions.FirstOrDefault(version => string.Equals(version.Version, item.LatestVersion, StringComparison.OrdinalIgnoreCase))
                ?? MarketplaceVersions.FirstOrDefault());
            StatusText = details.PackageFound
                ? $"Loaded {details.Versions.Count} version(s) for {item.PackageId}."
                : $"Package '{item.PackageId}' was not found.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsCurrentMarketplaceSelection(item, selectionVersion))
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
        ApplySelectedPackageDetails(PackageSelectionDetails.NoMarketplaceMatch());
        ClearWarnings();
        RefreshSelectedPackageOperationState();
        OnPropertyChanged(nameof(HasMarketplaceVersions));
        OnPropertyChanged(nameof(ShowNoMarketplaceVersions));
        NotifyDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void QueueMarketplaceSearch(TimeSpan? delay = null)
        => _marketplaceSearchScheduler.Queue(delay);

    private void PackageOperationService_OnOperationChanged(object? sender, PackageOperationChangedEventArgs e)
        => _ = UiThread.InvokeAsync(async () =>
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
        });

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

            StatusText = snapshot.State switch
            {
                BackgroundProcessState.Completed => snapshot.StatusText,
                BackgroundProcessState.Cancelled => $"Cancelled {snapshot.Title}.",
                BackgroundProcessState.Failed => snapshot.ErrorMessage ?? snapshot.StatusText,
                _ => StatusText,
            };
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private string? GetPreferredPackageIdForCompletedOperation(BackgroundProcessSnapshot snapshot)
    {
        if (PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
            && metadata.PackageId is { Length: > 0 } packageId)
        {
            return packageId;
        }

        return IsInstalledMode ? _selectedInstalledPackage?.PackageId : _selectedMarketplacePackage?.PackageId;
    }

    private void CancelQueuedMarketplaceSearch()
        => _marketplaceSearchScheduler.Cancel();

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

        RefreshPackageOperationState();
        NotifyCommandStateChanged();
    }

    private void InvalidateMarketplaceSelectionLoad()
        => _marketplace.SelectionLoader.Invalidate();

    private bool IsCurrentMarketplaceSelection(RegistryPackageSearchItemViewModel item, int selectionVersion)
        => !_disposed
           && _marketplace.SelectionLoader.IsCurrent(selectionVersion)
           && ReferenceEquals(_selectedMarketplacePackage, item);

    private void RefreshPackageOperationState()
    {
        _operationState.RefreshPackageRows(MarketplacePackages, InstalledPackages);
        RefreshSelectedPackageOperationState();
        NotifyCommandStateChanged();
    }

    private void RefreshSelectedPackageOperationState()
    {
        ApplySelectedPackageOperationState(_operationState.GetSelectedPackageState(GetSelectedPackageOperationPackageId()));
    }

    private string? GetSelectedPackageOperationPackageId()
        => IsMarketplaceMode ? _selectedMarketplacePackage?.PackageId : _selectedInstalledPackage?.PackageId;

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

    private void ApplySelectedPackageOperationState(SelectedPackageOperationState state)
    {
        SelectedPackageHasActiveOperation = state.HasActiveOperation;
        SelectedPackageOperationCanCancel = state.CanCancel;
        SelectedPackageOperationIsIndeterminate = state.IsIndeterminate;
        SelectedPackageOperationProgressPercent = state.ProgressPercent;
        SelectedPackageOperationStatusText = state.StatusText;
    }

    private InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedPackages.GetInstalledPackage(packageId);

    private Uri? CreatePackageIconUri(string packageId, PackageIconDescriptor? icon)
        => PackageIconUriResolver.Resolve(packageId, icon, _runtimeApiClient.CreatePackageAssetUri);

    private RegistryPackageUpdate? GetSelectedInstalledPackageUpdate()
        => _selectedInstalledPackage is null ? null : GetPackageUpdate(_selectedInstalledPackage.PackageId);

    private RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _installedPackages.GetPackageUpdate(packageId);

    private void ClearWarnings()
    {
        _warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
    }

    private void AddWarningLine(string warning)
    {
        _warnings.Add(warning);
        OnPropertyChanged(nameof(HasWarnings));
    }

    private void ReplaceWarningLines(IReadOnlyList<string> warnings)
    {
        _warnings.ReplaceWith(warnings);
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
        NotifyMarketplaceProfileChanged();
    }

    private void NotifyMarketplaceProfileChanged()
    {
        OnPropertyChanged(nameof(HasMarketplaceReadme));
        OnPropertyChanged(nameof(HasMarketplaceProfileLinks));
        OnPropertyChanged(nameof(HasMarketplaceProfileMetadata));
        OnPropertyChanged(nameof(HasMarketplaceProfileTags));
        OnPropertyChanged(nameof(HasMarketplaceProfile));
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
        OnPropertyChanged(nameof(ShowMarketplaceInstallButton));
        OnPropertyChanged(nameof(ShowMarketplaceUninstallButton));
        OnPropertyChanged(nameof(ShowMarketplaceUpdateButton));
        OnPropertyChanged(nameof(CanInstallSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUninstallSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUpdateSelectedMarketplacePackage));
        OnPropertyChanged(nameof(CanUpdateAllPackages));
        RefreshCommand.NotifyCanExecuteChanged();
        InstallPackageCommand.NotifyCanExecuteChanged();
        EnableSelectedPackageCommand.NotifyCanExecuteChanged();
        DisableSelectedPackageCommand.NotifyCanExecuteChanged();
        UninstallSelectedPackageCommand.NotifyCanExecuteChanged();
        InstallSelectedMarketplacePackageCommand.NotifyCanExecuteChanged();
        UpdateSelectedInstalledPackageCommand.NotifyCanExecuteChanged();
        UpdateSelectedMarketplacePackageCommand.NotifyCanExecuteChanged();
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

    public void Dispose()
    {
        _disposed = true;
        if (_packageOperationService is not null)
        {
            _packageOperationService.OperationChanged -= PackageOperationService_OnOperationChanged;
        }

        _marketplaceSearchScheduler.Dispose();
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
