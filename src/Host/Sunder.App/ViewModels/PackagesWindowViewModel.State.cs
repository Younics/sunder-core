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

public enum PackageWindowMode
{
    Marketplace,
    Installed,
}

public sealed partial class PackagesWindowViewModel : ViewModelBase, IDisposable
{
    private const int MarketplaceSearchThrottleDelayMilliseconds = 300;
    private static readonly TimeSpan MarketplaceDetailSpinnerDelay = TimeSpan.FromSeconds(1);

    private readonly IRuntimePackagesClient _runtimeApiClient;
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
    private readonly TimeSpan _marketplaceDetailSpinnerDelay;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly OwnedTaskObserver _tasks = new(nameof(PackagesWindowViewModel));
    private int _marketplaceSearchVersion;
    private CancellationTokenSource? _marketplacePackageDetailsSpinnerCancellation;
    private bool _disposed;
    private bool _isApplyingModeSearchText;
    private string _marketplaceSearchText = string.Empty;
    private string _installedSearchText = string.Empty;
    private PackageCatalogItemViewModel? _selectedInstalledPackage;
    private RegistryPackageSearchItemViewModel? _selectedMarketplacePackage;
    private RegistryPackageVersionItemViewModel? _selectedMarketplaceVersion;
    private PresentationOperationState _marketplacePackageDetailsState = PresentationOperationState.Idle;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? MarketplaceImageGalleryRequested
    {
        add => _marketplace.ImageGalleryRequested += value;
        remove => _marketplace.ImageGalleryRequested -= value;
    }

    internal PackagesWindowViewModel(
        IRuntimePackagesClient runtimeApiClient,
        IPackageArchivePicker packageArchivePicker,
        Func<IReadOnlyList<string>, CancellationToken, Task>? applyPackageLifecycleChangesAsync = null,
        PackageOperationService? packageOperationService = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        RegistryPackageInstallService? registryInstallService = null,
        NotificationCenterService? notificationCenter = null,
        Func<Uri, IRegistryPackageBrowseClient>? registryClientFactory = null,
        TimeSpan? marketplaceSearchThrottleDelay = null,
        TimeSpan? marketplaceDetailSpinnerDelay = null,
        double backgroundProcessPopoverWidth = ShellState.DefaultBackgroundProcessPopoverWidth,
        double backgroundProcessPopoverHeight = ShellState.DefaultBackgroundProcessPopoverHeight,
        Action<double, double>? persistBackgroundProcessPopoverSize = null,
        Func<IReadOnlyList<ActivePackageDescriptor>, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyList<string>, CancellationToken, Task>? preflightPackageLifecycleChangesAsync = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _runtimeApiClient = runtimeApiClient;
        _packageOperationService = packageOperationService;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        var resolvedRegistryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        var resolvedApplyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync ?? ((_, _) => Task.CompletedTask);
        var resolvedPreflightPackageLifecycleChangesAsync = preflightPackageLifecycleChangesAsync ?? ((_, _, _, _) => Task.CompletedTask);
        _registryClientProvider = new PackageRegistryClientProvider(
            () => RegistryUrlText,
            registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl)));
        _marketplaceDetailSpinnerDelay = marketplaceDetailSpinnerDelay ?? MarketplaceDetailSpinnerDelay;
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
            resolvedPreflightPackageLifecycleChangesAsync,
            notificationCenter,
            () => IsBusy,
            value => IsBusy = value,
            value => StatusText = value,
            ClearWarnings,
            ReplaceWarningLines,
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

    public ObservableCollection<RegistrySearchSortOptionViewModel> MarketplaceSortOptions { get; } = new(RegistrySearchSortOptionViewModel.Defaults);

    public ObservableCollection<RegistryPackageVersionItemViewModel> MarketplaceVersions => _marketplace.Versions;

    public ObservableCollection<RegistryPackageProfileLinkViewModel> MarketplaceProfileLinks => _marketplace.ProfileLinks;

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> MarketplaceProfileMetadata => _marketplace.ProfileMetadata;

    public ObservableCollection<string> MarketplaceProfileTags => _marketplace.ProfileTags;

    public ObservableCollection<RegistryPackageMediaItemViewModel> MarketplaceProfileMedia => _marketplace.ProfileMedia;

    public ObservableCollection<RegistryUserAttributionViewModel> MarketplaceAttributions { get; } = [];

    public ObservableCollection<RegistryUserAttributionViewModel> MarketplaceCreators { get; } = [];

    public ObservableCollection<RegistryUserAttributionViewModel> MarketplaceMaintainers { get; } = [];

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

            _tasks.Observe(SelectMarketplacePackageAsync(value, _tasks.Token), "loading selected marketplace package");
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

    public bool HasMarketplacePackageDetailsError => !string.IsNullOrWhiteSpace(MarketplacePackageDetailsError);

    public bool IsMarketplacePackageDetailsLoading => _marketplacePackageDetailsState.IsRunning;

    public bool MarketplacePackageDetailsLoaded => _marketplacePackageDetailsState.IsSucceeded;

    public string MarketplacePackageDetailsError => _marketplacePackageDetailsState.ErrorMessage;

    public bool ShowMarketplacePackageDetailsLoading => ShowMarketplaceDetails && IsMarketplacePackageDetailsLoading && ShowMarketplacePackageDetailsSpinner;

    public bool ShowMarketplacePackageDetailsContent => ShowMarketplaceDetails && MarketplacePackageDetailsLoaded && !HasMarketplacePackageDetailsError;

    public bool ShowMarketplacePackageDetailsError => ShowMarketplaceDetails && HasMarketplacePackageDetailsError;

    public bool HasMarketplaceVersions => MarketplacePackageDetailsLoaded && _marketplace.HasVersions;

    public bool ShowNoMarketplaceVersions => MarketplacePackageDetailsLoaded && !HasMarketplaceVersions && !HasMarketplacePackageDetailsError;

    public bool HasMarketplaceReadme => MarketplacePackageDetailsLoaded && _marketplace.HasReadme;

    public bool HasMarketplaceProfileLinks => MarketplacePackageDetailsLoaded && _marketplace.HasProfileLinks;

    public bool HasMarketplaceProfileMetadata => MarketplacePackageDetailsLoaded && _marketplace.HasProfileMetadata;

    public bool HasMarketplaceProfileTags => MarketplacePackageDetailsLoaded && _marketplace.HasProfileTags;

    public bool HasMarketplaceProfile => MarketplacePackageDetailsLoaded && _marketplace.HasProfile;

    public bool HasMarketplaceProfileMedia => MarketplacePackageDetailsLoaded && _marketplace.HasProfileMedia;

    public bool HasMarketplaceAttributions => MarketplacePackageDetailsLoaded && MarketplaceAttributions.Count > 0;

    public bool HasMarketplaceCreators => MarketplacePackageDetailsLoaded && MarketplaceCreators.Count > 0;

    public bool HasMarketplaceMaintainers => MarketplacePackageDetailsLoaded && MarketplaceMaintainers.Count > 0;

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

    public bool ShowMarketplacePackageStats => ShowMarketplacePackageDetailsContent && HasMarketplacePackageStats;

    public bool ShowMarketplacePackageStarAction => ShowMarketplacePackageDetailsContent;

    public bool CanToggleSelectedMarketplacePackageStar => !IsBusy && ShowMarketplacePackageStarAction && _selectedMarketplacePackage is not null;

    public bool CanInstallSelectedMarketplacePackage => ShowMarketplacePackageDetailsContent
        && ShowMarketplaceInstallAction
        && _selectedMarketplacePackage is { IsYanked: false }
        && _selectedMarketplaceVersion is { IsYanked: false }
        && !SelectedPackageHasActiveOperation;

    public bool CanUninstallSelectedMarketplacePackage => ShowMarketplaceInstalledActions && !SelectedPackageHasActiveOperation;

    public bool CanUpdateSelectedMarketplacePackage => IsMarketplaceMode && _selectedMarketplacePackage?.HasUpdate == true && !SelectedPackageHasActiveOperation;

    public bool CanUpdateAllPackages => !IsBusy && AvailableUpdateCount > 0;

    public bool ShowUpdateAllPackages => AvailableUpdateCount > 0;

    public bool ShowHeaderUpdateAllPackages => ShowUpdateAllPackages;

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
    private RegistrySearchSortOptionViewModel? _selectedMarketplaceSortOption = RegistrySearchSortOptionViewModel.Defaults[0];

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

    [ObservableProperty]
    private string _marketplacePackageStatsText = string.Empty;

    [ObservableProperty]
    private string _marketplacePackageStarActionText = "Star";

    [ObservableProperty]
    private bool _hasMarketplacePackageStats;

    [ObservableProperty]
    private bool _selectedMarketplacePackageIsStarred;

    [ObservableProperty]
    private bool _showMarketplacePackageDetailsSpinner;

    partial void OnModeChanged(PackageWindowMode value)
    {
        OnPropertyChanged(nameof(IsMarketplaceMode));
        OnPropertyChanged(nameof(IsInstalledMode));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(ShowHeaderUpdateAllPackages));
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

    partial void OnSelectedMarketplaceSortOptionChanged(RegistrySearchSortOptionViewModel? value)
    {
        if (IsMarketplaceMode)
        {
            QueueMarketplaceSearch(TimeSpan.Zero);
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

}
