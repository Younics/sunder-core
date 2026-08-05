using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public enum StackBrowserMode
{
    Local,
    Marketplace,
}

public sealed partial class StacksWindowViewModel : ViewModelBase, IDisposable
{
    private const int MarketplaceSearchThrottleDelayMilliseconds = 300;
    private static readonly TimeSpan RegistryDetailSpinnerDelay = TimeSpan.FromSeconds(1);

    private readonly LocalStackLibraryService _library;
    private readonly StackSelectionCoordinator _selection;
    private readonly StackDetailLoadCoordinator _detailLoader;
    private readonly IStackArchivePicker _archivePicker;
    private readonly IRuntimeStacksClient _runtimeApiClient;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly StackPublishingController _publishing;
    private readonly Func<RuntimePackageStamp, CancellationToken, Task> _waitUntilPresentationAppliedAsync;
    private readonly MarketplaceSearchScheduler _registrySearchScheduler;
    private readonly TimeSpan _registryDetailSpinnerDelay;
    private readonly MarketplacePackageProfileViewModel _selectedLocalProfile = new();
    private readonly MarketplacePackageProfileViewModel _registryProfile = new();
    private StackSelectionRequest _localSelectionRequest;
    private CancellationTokenSource? _registryStackDetailsSpinnerCancellation;
    private readonly OwnedTaskObserver _tasks = new(nameof(StacksWindowViewModel));
    private PresentationOperationState _registryStackDetailsState = PresentationOperationState.Idle;
    private bool _disposed;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? RegistryImageGalleryRequested
    {
        add => _registryProfile.ImageGalleryRequested += value;
        remove => _registryProfile.ImageGalleryRequested -= value;
    }

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? LocalImageGalleryRequested
    {
        add => _selectedLocalProfile.ImageGalleryRequested += value;
        remove => _selectedLocalProfile.ImageGalleryRequested -= value;
    }

    public StacksWindowViewModel(
        LocalStackLibraryService library,
        IStackArchivePicker archivePicker,
        IRuntimeStacksClient runtimeApiClient,
        RegistryPackageInstallService? registryInstallService = null,
        Func<RuntimePackageStamp, CancellationToken, Task>? waitUntilPresentationAppliedAsync = null,
        Func<Uri, IRegistryApiClient>? registryClientFactory = null,
        TimeSpan? registrySearchThrottleDelay = null,
        TimeSpan? registryDetailSpinnerDelay = null)
    {
        _library = library;
        _archivePicker = archivePicker;
        _runtimeApiClient = runtimeApiClient;
        _selection = new StackSelectionCoordinator(_tasks.Token);
        _detailLoader = new StackDetailLoadCoordinator(library, runtimeApiClient);
        _registryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        _publishing = new StackPublishingController(library, runtimeApiClient);
        _waitUntilPresentationAppliedAsync = waitUntilPresentationAppliedAsync ?? ((_, _) => Task.CompletedTask);
        Local = new LocalStacksViewModel(library, LocalSelectionChanged);
        Registry = new RegistryStacksViewModel(
            registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl)),
            RegistrySelectionChanged);
        Local.PropertyChanged += Local_OnPropertyChanged;
        Registry.PropertyChanged += Registry_OnPropertyChanged;
        _registryDetailSpinnerDelay = registryDetailSpinnerDelay ?? RegistryDetailSpinnerDelay;
        _registrySearchScheduler = new MarketplaceSearchScheduler(
            SearchRegistryStacksCoreAsync,
            registrySearchThrottleDelay ?? TimeSpan.FromMilliseconds(MarketplaceSearchThrottleDelayMilliseconds));
    }

    public LocalStacksViewModel Local { get; }

    public RegistryStacksViewModel Registry { get; }

    public ObservableCollection<StackPackageRequirementViewModel> SelectedPackages { get; } = [];

    public ObservableCollection<LocalStackDetailPackageViewModel> SelectedLocalDetails { get; } = [];

    public ObservableCollection<StackFragmentViewModel> SelectedFragments { get; } = [];

    public ObservableCollection<string> SelectedRequiredInputs { get; } = [];

    public ObservableCollection<RegistryPackageMediaItemViewModel> SelectedStackProfileMedia => _selectedLocalProfile.Media;

    public LiveMarkdown.Avalonia.ObservableStringBuilder SelectedStackReadmeMarkdownBuilder => _selectedLocalProfile.ReadmeMarkdownBuilder;

    public ObservableCollection<RegistryStackPackageRequirementViewModel> RegistrySelectedPackages { get; } = [];

    public ObservableCollection<LocalStackDetailPackageViewModel> RegistrySelectedDetails { get; } = [];

    public ObservableCollection<string> RegistrySelectedRequiredInputs { get; } = [];

    public ObservableCollection<RegistryPackageProfileLinkViewModel> RegistryProfileLinks => _registryProfile.Links;

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> RegistryProfileMetadata => _registryProfile.Metadata;

    public ObservableCollection<string> RegistryProfileTags => _registryProfile.Tags;

    public ObservableCollection<RegistryPackageMediaItemViewModel> RegistryProfileMedia => _registryProfile.Media;

    public LiveMarkdown.Avalonia.ObservableStringBuilder RegistryReadmeMarkdownBuilder => _registryProfile.ReadmeMarkdownBuilder;

    public ObservableCollection<RegistryUserAttributionViewModel> RegistryAttributions { get; } = [];

    public ObservableCollection<RegistryUserAttributionViewModel> RegistryCreators { get; } = [];

    public ObservableCollection<RegistryUserAttributionViewModel> RegistryMaintainers { get; } = [];

    [ObservableProperty]
    private StackBrowserMode _browserMode = StackBrowserMode.Marketplace;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Import a .sunderstack file or select a local Stack.";

    [ObservableProperty]
    private string _selectedStackTitle = "No Stack selected";

    [ObservableProperty]
    private string _selectedStackSubtitle = "Select a Stack to inspect its setup plan.";

    [ObservableProperty]
    private string _selectedStackSummary = string.Empty;

    [ObservableProperty]
    private string _selectedStackMetadata = string.Empty;

    [ObservableProperty]
    private string _selectedStackPath = string.Empty;

    [ObservableProperty]
    private string _selectedStackStatsText = string.Empty;

    [ObservableProperty]
    private string _selectedStackStarActionText = "Star";

    [ObservableProperty]
    private bool _hasSelectedStackStats;

    [ObservableProperty]
    private bool _selectedStackIsStarred;

    [ObservableProperty]
    private string _selectedRegistryStackTitle = "No Marketplace Stack selected";

    [ObservableProperty]
    private string _selectedRegistryStackSubtitle = "Search Marketplace Stacks to inspect shared setup presets.";

    [ObservableProperty]
    private string _selectedRegistryStackSummary = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackContentText = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackUpdatedText = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackStatsText = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackStarActionText = "Star";

    [ObservableProperty]
    private bool _hasSelectedRegistryStackStats;

    [ObservableProperty]
    private bool _selectedRegistryStackIsStarred;

    [ObservableProperty]
    private bool _showRegistryStackDetailsSpinner;

    public bool HasStacks => Local.HasStacks;

    public bool IsLocalMode => BrowserMode == StackBrowserMode.Local;

    public bool IsMarketplaceMode => BrowserMode == StackBrowserMode.Marketplace;

    public bool ShowNoStacks => IsLocalMode && !HasStacks && !IsBusy;

    public bool HasSearchText => Local.HasSearchText;

    public bool HasRegistrySearchText => Registry.HasSearchText;

    public bool HasRegistryUrlText => Registry.HasRegistryUrlText;

    public bool HasRegistryStacks => Registry.HasStacks;

    public bool ShowNoRegistryStacks => IsMarketplaceMode && !HasRegistryStacks && !IsBusy;

    public bool HasSelection => Local.SelectedStack is not null;

    public bool HasRegistrySelection => Registry.SelectedStack is not null;

    public bool HasRegistryStackDetailsError => !string.IsNullOrWhiteSpace(RegistryStackDetailsError);

    public bool IsRegistryStackDetailsLoading => _registryStackDetailsState.IsRunning;

    public bool RegistryStackDetailsLoaded => _registryStackDetailsState.IsSucceeded;

    public string RegistryStackDetailsError => _registryStackDetailsState.ErrorMessage;

    public bool ShowRegistryStackDetailsLoading => ShowRegistrySelectedDetails && IsRegistryStackDetailsLoading && ShowRegistryStackDetailsSpinner;

    public bool ShowRegistryStackDetailsContent => ShowRegistrySelectedDetails && RegistryStackDetailsLoaded && !HasRegistryStackDetailsError;

    public bool ShowRegistryStackDetailsError => ShowRegistrySelectedDetails && HasRegistryStackDetailsError;

    public bool HasRegistrySelectedSummary => !string.IsNullOrWhiteSpace(SelectedRegistryStackSummary);

    public bool HasRegistrySelectedPackages => RegistryStackDetailsLoaded && RegistrySelectedPackages.Count > 0;

    public bool HasRegistrySelectedFragments => RegistryStackDetailsLoaded && RegistrySelectedDetails.Count > 0;

    public bool HasRegistrySelectedRequiredInputs => RegistryStackDetailsLoaded && RegistrySelectedRequiredInputs.Count > 0;

    public bool HasRegistryReadme => RegistryStackDetailsLoaded && _registryProfile.HasReadme;

    public bool HasRegistryProfileLinks => RegistryStackDetailsLoaded && _registryProfile.HasLinks;

    public bool HasRegistryProfileMetadata => RegistryStackDetailsLoaded && _registryProfile.HasMetadata;

    public bool HasRegistryProfileTags => RegistryStackDetailsLoaded && _registryProfile.HasTags;

    public bool HasRegistryProfile => RegistryStackDetailsLoaded && _registryProfile.HasProfile;

    public bool HasRegistryProfileMedia => RegistryStackDetailsLoaded && _registryProfile.HasMedia;

    public bool HasRegistryAttributions => RegistryStackDetailsLoaded && RegistryAttributions.Count > 0;

    public bool HasRegistryCreators => RegistryStackDetailsLoaded && RegistryCreators.Count > 0;

    public bool HasRegistryMaintainers => RegistryStackDetailsLoaded && RegistryMaintainers.Count > 0;

    public bool HasSelectedSummary => !string.IsNullOrWhiteSpace(SelectedStackSummary);

    public bool HasSelectedReadme => _selectedLocalProfile.HasReadme;

    public bool HasSelectedProfileMedia => _selectedLocalProfile.HasMedia;

    public bool ShowSelectedStackStats => ShowSelectedDetails && HasSelectedStackStats;

    public bool HasSelectedPackages => SelectedPackages.Count > 0;

    public bool HasSelectedLocalDetails => SelectedLocalDetails.Count > 0;

    public bool HasSelectedFragments => SelectedFragments.Count > 0;

    public bool ShowSelectedFragments => HasSelectedFragments && !HasSelectedLocalDetails;

    public bool HasSelectedRequiredInputs => SelectedRequiredInputs.Count > 0;

    public bool ShowSelectedDetails => IsLocalMode && HasSelection;

    public bool ShowRegistrySelectedDetails => IsMarketplaceMode && HasRegistrySelection;

    public bool ShowRegistryStackStats => ShowRegistryStackDetailsContent && HasSelectedRegistryStackStats;

    public bool ShowNoSelection => IsLocalMode && !HasSelection;

    public bool ShowNoRegistrySelection => IsMarketplaceMode && !HasRegistrySelection;

    public bool CanRefresh => !IsBusy;

    public bool CanImportStack => !IsBusy;

    public bool CanSearchRegistryStacks => !IsBusy && HasRegistryUrlText;

    public bool CanCreateStack => !IsBusy;

    public bool CanEditSelectedStack => !IsBusy && ShowSelectedDetails && Local.SelectedStack is not null;

    public bool CanImportSelectedRegistryStack => !IsBusy && ShowRegistryStackDetailsContent && Registry.SelectedStack is not null;

    public bool CanUseSelectedRegistryStack => !IsBusy && ShowRegistryStackDetailsContent && Registry.SelectedStack is not null;

    public bool CanUseSelectedStack => !IsBusy && Local.SelectedStack is not null;

    public bool CanDeleteSelectedRegistryStack => !IsBusy && Registry.SelectedStack is not null;

    public bool CanExportSelectedStack => !IsBusy && Local.SelectedStack is not null;

    public bool ShowPublishSelectedStack => ShowSelectedDetails && Local.SelectedStack?.IsPublished != true;

    public bool ShowUnpublishSelectedStack => ShowSelectedDetails && Local.SelectedStack?.IsPublished == true;

    public bool CanPublishSelectedStack => !IsBusy && ShowPublishSelectedStack;

    public bool CanUnpublishSelectedStack => !IsBusy && ShowUnpublishSelectedStack;

    public bool ShowSelectedStackStarAction => ShowSelectedDetails && Local.SelectedStack?.IsPublished == true;

    public bool CanToggleSelectedStackStar => !IsBusy && ShowSelectedStackStarAction;

    public bool ShowSelectedRegistryStackStarAction => ShowRegistryStackDetailsContent;

    public bool CanToggleSelectedRegistryStackStar => !IsBusy && ShowSelectedRegistryStackStarAction && Registry.SelectedStack is not null;

    public bool CanRemoveSelectedStack => !IsBusy && Local.SelectedStack is not null;

    private void LocalSelectionChanged(LocalStackLibraryItemViewModel? value)
    {
        _localSelectionRequest = _selection.BeginLocal(value is not null && !_disposed);
        ApplySelectedStackSummary(value);
        NotifySelectionChanged();
        if (value is not null)
        {
            _tasks.Observe(LoadSelectedStackManifestAsync(value.Item, _localSelectionRequest), "loading local Stack details");
            if (value.IsPublished)
            {
                _tasks.Observe(LoadSelectedPublishedStackStatsAsync(value, _localSelectionRequest), "loading published Stack statistics");
            }
        }
    }

    partial void OnBrowserModeChanged(StackBrowserMode value)
    {
        NotifyBrowserModeChanged();
        NotifySelectionChanged();
        NotifyRegistrySelectionChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoStacks));
        OnPropertyChanged(nameof(ShowNoRegistryStacks));
        NotifyCommandStateChanged();
    }

    private void Local_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalStacksViewModel.SearchText) or nameof(LocalStacksViewModel.HasStacks))
        {
            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(HasStacks));
            OnPropertyChanged(nameof(ShowNoStacks));
        }
    }

    private void Registry_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegistryStacksViewModel.SearchText))
        {
            OnPropertyChanged(nameof(HasRegistrySearchText));
            if (IsMarketplaceMode && HasRegistryUrlText)
            {
                QueueRegistrySearch();
            }
        }
        else if (e.PropertyName == nameof(RegistryStacksViewModel.SelectedSortOption))
        {
            if (IsMarketplaceMode && HasRegistryUrlText)
            {
                QueueRegistrySearch(TimeSpan.Zero);
            }
        }
        else if (e.PropertyName == nameof(RegistryStacksViewModel.RegistryUrlText))
        {
            OnPropertyChanged(nameof(HasRegistryUrlText));
            if (IsMarketplaceMode && HasRegistryUrlText)
            {
                QueueRegistrySearch();
            }

            NotifyRegistryStackStateChanged();
        }
        else if (e.PropertyName == nameof(RegistryStacksViewModel.HasStacks))
        {
            NotifyRegistryStackStateChanged();
        }
    }

    private void RegistrySelectionChanged(RegistryStackSearchItemViewModel? value)
    {
        var selectionRequest = BeginSelectedRegistryStackDetailsLoad(value);
        if (value is not null)
        {
            _tasks.Observe(
                LoadSelectedRegistryStackDetailsAsync(value.StackId, selectionRequest),
                "loading Registry Stack details");
        }

        NotifyRegistrySelectionChanged();
        NotifyRegistryStackStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRegistryStackDetailsSpinnerDelay();
        _selection.Dispose();
        _registrySearchScheduler.Dispose();
        Local.PropertyChanged -= Local_OnPropertyChanged;
        Registry.PropertyChanged -= Registry_OnPropertyChanged;
        Registry.Dispose();
        _tasks.Dispose();
        DisposeSelectedLocalDetails();
        DisposeRegistrySelectedDetails();
        _selectedLocalProfile.Dispose();
        _registryProfile.Dispose();
        _runtimeApiClient.Dispose();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (Local.Count == 0)
        {
            await RefreshLocalStacksAsync(cancellationToken);
        }

        if (IsMarketplaceMode && Registry.Stacks.Count == 0 && HasRegistryUrlText)
        {
            await SearchRegistryStacksCoreAsync(cancellationToken);
        }
    }

    public CreateStackWizardViewModel CreateCreateStackWizardViewModel()
        => new(_library, _runtimeApiClient);

    public CreateStackWizardViewModel? CreateEditStackWizardViewModel()
        => Local.SelectedStack is null
            ? null
            : new CreateStackWizardViewModel(_library, _runtimeApiClient, new CreateStackWizardEditContext(Local.SelectedStack.Item));

    public UseStackWizardViewModel? CreateUseStackWizardViewModel()
        => Local.SelectedStack is null
            ? null
            : new UseStackWizardViewModel(
                _library,
                Local.SelectedStack.Item,
                _runtimeApiClient,
                _registryInstallService,
                _waitUntilPresentationAppliedAsync,
                Registry.CreateClient,
                Registry.RegistryUrlText);

    public async Task RefreshAfterCreatedStackAsync(string? stackId)
    {
        if (_disposed)
        {
            return;
        }

        await Local.RefreshAsync(stackId, _tasks.Token);
        if (_disposed)
        {
            return;
        }

        StatusText = string.IsNullOrWhiteSpace(stackId)
            ? "Created local Stack."
            : $"Created local Stack '{stackId}'.";
    }

    public async Task RefreshAfterEditedStackAsync(string? stackId)
    {
        if (_disposed)
        {
            return;
        }

        await Local.RefreshAsync(stackId, _tasks.Token);
        if (_disposed)
        {
            return;
        }

        var selectedStack = Local.SelectedStack;
        StatusText = string.IsNullOrWhiteSpace(stackId)
            ? "Saved local Stack changes."
            : $"Saved local Stack '{stackId}'.";
        if (selectedStack?.IsPublished == true)
        {
            await UpdatePublishedStackAfterEditAsync(selectedStack);
        }
    }

    private async Task UpdatePublishedStackAfterEditAsync(LocalStackLibraryItemViewModel selectedStack)
    {
        if (!RegistryUrlHelper.TryParse(selectedStack.RegistryUrl, out var registryUrl) || registryUrl is null)
        {
            StatusText = $"Saved local Stack '{selectedStack.StackId}'. Registry update was skipped because its saved Registry URL is invalid.";
            return;
        }

        var cancellationToken = _tasks.Token;
        IsBusy = true;
        try
        {
            var result = await _publishing.PublishAsync(
                selectedStack,
                registryUrl,
                preserveOriginalPublishTime: true,
                cancellationToken);
            StatusText = result.Success
                ? result.Message
                : $"Saved local Stack '{selectedStack.StackId}'. Registry update failed: {result.Message}";
            if (result.Success)
            {
                await Local.RefreshAsync(selectedStack.StackId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"Saved local Stack '{selectedStack.StackId}'. Registry update failed: {ex.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                NotifySelectionChanged();
            }
        }
    }

    public async Task RefreshAfterUsedStackAsync()
    {
        if (_disposed)
        {
            return;
        }

        var stackId = Local.SelectedStack?.StackId;
        await Local.RefreshAsync(stackId, _tasks.Token);
        if (_disposed)
        {
            return;
        }
        StatusText = stackId is null
            ? "Stack use completed."
            : $"Used local Stack '{stackId}'.";
    }

    public async Task ApplyLaunchRequestAsync(AppLaunchRequest request, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
        switch (request.Kind)
        {
            case AppLaunchRequestKind.StackFile when !string.IsNullOrWhiteSpace(request.FilePath):
                await ImportStackFromPathAsync(request.FilePath, cancellationToken);
                break;
            case AppLaunchRequestKind.StackDetails:
            case AppLaunchRequestKind.StackUse:
                await ShowLinkedRegistryStackAsync(request, cancellationToken);
                break;
        }
    }
}
