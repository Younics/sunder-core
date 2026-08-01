using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly IPackageOperationExecutor _packageOperationExecutor;
    private readonly PackageRegistryClientProvider _registryClientProvider;
    private readonly InstalledPackagesPaneViewModel _installedPackages;
    private readonly MarketplacePackagesPaneViewModel _marketplace;
    private readonly PackagesOperationCommandCoordinator _operationCommands;
    private readonly PackagesSelectedOperationCommands _selectedOperationCommands;
    private readonly SelectedPackageIconObserver _selectedPackageIconObserver;
    private readonly MarketplaceSearchScheduler _marketplaceSearchScheduler;
    private readonly LatestAsyncRequest _marketplaceVersionDetailsRequest = new();
    private readonly TimeSpan _marketplaceDetailSpinnerDelay;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly OwnedTaskObserver _tasks = new(nameof(PackagesWindowViewModel));
    private int _marketplaceSearchVersion;
    private CancellationTokenSource? _marketplacePackageDetailsSpinnerCancellation;
    private bool _disposed;
    private bool _isApplyingModeSearchText;
    private PresentationOperationState _marketplacePackageDetailsState = PresentationOperationState.Idle;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? MarketplaceImageGalleryRequested
    {
        add => _marketplace.ImageGalleryRequested += value;
        remove => _marketplace.ImageGalleryRequested -= value;
    }

    internal PackagesWindowViewModel(
        IRuntimePackagesClient runtimeApiClient,
        IPackageArchivePicker packageArchivePicker,
        IPackageOperationExecutor packageOperationExecutor,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        Func<Uri, IRegistryPackageBrowseClient>? registryClientFactory = null,
        TimeSpan? marketplaceSearchThrottleDelay = null,
        TimeSpan? marketplaceDetailSpinnerDelay = null,
        double backgroundProcessPopoverWidth = ShellState.DefaultBackgroundProcessPopoverWidth,
        double backgroundProcessPopoverHeight = ShellState.DefaultBackgroundProcessPopoverHeight,
        Action<double, double>? persistBackgroundProcessPopoverSize = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _runtimeApiClient = runtimeApiClient;
        _packageOperationExecutor = packageOperationExecutor;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _registryClientProvider = new PackageRegistryClientProvider(
            () => RegistryUrlText,
            registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl)));
        _marketplaceDetailSpinnerDelay = marketplaceDetailSpinnerDelay ?? MarketplaceDetailSpinnerDelay;
        _installedPackages = new InstalledPackagesPaneViewModel(
            new PackagesInstalledCatalog(_runtimeApiClient, _registryClientProvider),
            CreatePackageIconUri,
            SelectInstalledPackage);
        _marketplace = new MarketplacePackagesPaneViewModel(new PackagesMarketplaceCatalog(_registryClientProvider));
        Installed = _installedPackages;
        Marketplace = _marketplace;
        Operations = new PackageOperationPresentationViewModel(_packageOperationExecutor);
        Operations.PropertyChanged += Operations_OnPropertyChanged;
        _operationCommands = new PackagesOperationCommandCoordinator(
            _packageOperationExecutor,
            packageArchivePicker,
            _registryClientProvider,
            value => Operations.StatusText = value,
            RefreshPackageOperationState,
            () => _installedPackages.IsDirty = true);
        _selectedOperationCommands = new PackagesSelectedOperationCommands(
            _operationCommands,
            Operations,
            () => Mode,
            value => Mode = value,
            () => Installed.SelectedPackage,
            () => Marketplace.SelectedPackage,
            () => Marketplace.SelectedVersion,
            () => AvailableUpdateCount,
            GetSelectedInstalledPackageUpdate,
            GetPackageUpdate,
            RefreshPackageOperationState,
            value => Operations.StatusText = value);
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
        _packageOperationExecutor.OperationChanged += PackageOperationService_OnOperationChanged;
    }

    public InstalledPackagesPaneViewModel Installed { get; }

    public MarketplacePackagesPaneViewModel Marketplace { get; }

    public PackageOperationPresentationViewModel Operations { get; }

    public ObservableCollection<RegistrySearchSortOptionViewModel> MarketplaceSortOptions { get; } = new(RegistrySearchSortOptionViewModel.Defaults);

    public BackgroundProcessMonitorViewModel PackageProcesses { get; }

    public PackageCatalogItemViewModel? SelectedInstalledPackage
    {
        get => Installed.SelectedPackage;
        set
        {
            if (value is null || ReferenceEquals(Installed.SelectedPackage, value))
            {
                return;
            }

            SelectInstalledPackage(value);
        }
    }

    public RegistryPackageSearchItemViewModel? SelectedMarketplacePackage
    {
        get => Marketplace.SelectedPackage;
        set
        {
            if (value is null || ReferenceEquals(Marketplace.SelectedPackage, value))
            {
                return;
            }

            _tasks.Observe(SelectMarketplacePackageAsync(value, _tasks.Token), "loading selected marketplace package");
        }
    }

    public RegistryPackageVersionItemViewModel? SelectedMarketplaceVersion
    {
        get => Marketplace.SelectedVersion;
        set
        {
            if (ReferenceEquals(Marketplace.SelectedVersion, value))
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

    public bool HasMarketplaceAttributions => MarketplacePackageDetailsLoaded && Marketplace.HasAttributions;

    public bool HasMarketplaceCreators => MarketplacePackageDetailsLoaded && Marketplace.HasCreators;

    public bool HasMarketplaceMaintainers => MarketplacePackageDetailsLoaded && Marketplace.HasMaintainers;

    public bool ShowInstalledDetails => IsInstalledMode && Installed.SelectedPackage is not null;

    public bool ShowMarketplaceDetails => IsMarketplaceMode && Marketplace.SelectedPackage is not null;

    public bool ShowNoSelection => !ShowInstalledDetails && !ShowMarketplaceDetails;

    public bool ShowSelectedPackageIcon => ShowInstalledDetails || ShowMarketplaceDetails;

    public bool SelectedPackageHasIconImage => SelectedPackageIconImage is not null;

    public bool SelectedPackageShowGlyphFallback => SelectedPackageIconImage is null;

    public bool SelectedPackageHasIconLoadError => !string.IsNullOrWhiteSpace(SelectedPackageIconLoadError);

    private bool HasActivePackageStoreOperation => Operations.HasActivePackageStoreOperation;

    public bool CanRefresh => !Operations.IsBusy;

    public bool CanInstallPackage => !Operations.IsBusy;

    public bool CanEnableSelectedPackage => !Operations.IsBusy && !HasActivePackageStoreOperation && IsInstalledMode && Installed.SelectedPackage?.CanEnable == true;

    public bool CanDisableSelectedPackage => !Operations.IsBusy && !HasActivePackageStoreOperation && IsInstalledMode && Installed.SelectedPackage?.CanDisable == true;

    public bool ShowEnableSelectedPackage => IsInstalledMode && Installed.SelectedPackage?.CanEnable == true;

    public bool ShowDisableSelectedPackage => IsInstalledMode && Installed.SelectedPackage?.CanDisable == true;

    public bool CanUninstallSelectedPackage => !Operations.IsBusy && IsInstalledMode && Installed.SelectedPackage?.CanUninstall == true && !Operations.SelectedPackageHasActiveOperation;

    public bool CanUpdateSelectedInstalledPackage => !Operations.IsBusy && IsInstalledMode && GetSelectedInstalledPackageUpdate() is not null && !Operations.SelectedPackageHasActiveOperation;

    public bool ShowUpdateSelectedInstalledPackage => IsInstalledMode && GetSelectedInstalledPackageUpdate() is not null;

    public bool IsSelectedMarketplacePackageInstalled => Marketplace.SelectedPackage?.InstalledVersion is not null;

    public bool ShowMarketplaceInstallAction => IsMarketplaceMode && Marketplace.SelectedPackage is not null && !IsSelectedMarketplacePackageInstalled;

    public bool ShowMarketplaceInstalledActions => IsMarketplaceMode && IsSelectedMarketplacePackageInstalled;

    public bool ShowMarketplaceInstallButton => ShowMarketplaceInstallAction && !Operations.SelectedPackageHasActiveOperation;

    public bool ShowMarketplaceUninstallButton => ShowMarketplaceInstalledActions && !Operations.SelectedPackageHasActiveOperation;

    public bool ShowMarketplaceUpdateButton => ShowMarketplaceInstalledActions && !Operations.SelectedPackageHasActiveOperation;

    public bool ShowMarketplacePackageStats => ShowMarketplacePackageDetailsContent && HasMarketplacePackageStats;

    public bool ShowMarketplacePackageStarAction => ShowMarketplacePackageDetailsContent;

    public bool CanToggleSelectedMarketplacePackageStar => !Operations.IsBusy && ShowMarketplacePackageStarAction && Marketplace.SelectedPackage is not null;

    public bool CanInstallSelectedMarketplacePackage => ShowMarketplacePackageDetailsContent
        && ShowMarketplaceInstallAction
        && Marketplace.SelectedPackage is { IsYanked: false }
        && Marketplace.SelectedVersion is { IsYanked: false }
        && Marketplace.RpcAccessLoaded
        && !Operations.SelectedPackageHasActiveOperation;

    public bool CanUninstallSelectedMarketplacePackage => ShowMarketplaceInstalledActions && !Operations.SelectedPackageHasActiveOperation;

    public bool CanUpdateSelectedMarketplacePackage => IsMarketplaceMode
        && Marketplace.SelectedPackage?.HasUpdate == true
        && Marketplace.SelectedVersion is { } selectedVersion
        && GetPackageUpdate(Marketplace.SelectedPackage.PackageId) is { } update
        && string.Equals(selectedVersion.Version, update.AvailableVersion, StringComparison.Ordinal)
        && Marketplace.RpcAccessLoaded
        && !Operations.SelectedPackageHasActiveOperation;

    public bool CanUpdateAllPackages => !Operations.IsBusy && AvailableUpdateCount > 0;

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
    private string _registryUrlText;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private RegistrySearchSortOptionViewModel? _selectedMarketplaceSortOption = RegistrySearchSortOptionViewModel.Defaults[0];

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

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        if (_isApplyingModeSearchText)
        {
            return;
        }

        if (IsInstalledMode)
        {
            Installed.SearchText = value;
            RebuildInstalledPackageList(Installed.SelectedPackage?.PackageId);
            return;
        }

        if (IsMarketplaceMode)
        {
            Marketplace.SearchText = value;
            QueueMarketplaceSearch();
        }
    }

    partial void OnRegistryUrlTextChanged(string value)
    {
        if (Marketplace.SelectedPackage is not null)
        {
            ClearMarketplaceSelection();
        }
        NotifyCommandStateChanged();
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

    private void Operations_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageOperationPresentationViewModel.IsBusy)
            or nameof(PackageOperationPresentationViewModel.SelectedPackageHasActiveOperation)
            or nameof(PackageOperationPresentationViewModel.SelectedPackageOperationCanCancel))
        {
            NotifyCommandStateChanged();
        }
    }

    public bool ShowNoInstalledPackageError => !SelectedPackageHasError;

}
