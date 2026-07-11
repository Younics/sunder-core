using System.Collections.ObjectModel;
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
    private readonly IStackArchivePicker _archivePicker;
    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _applyPackageLifecycleChangesAsync;
    private readonly Func<IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor>, CancellationToken, Task<IReadOnlyList<string>>> _notifyStackImportAppliedAsync;
    private readonly Func<Uri, IRegistryApiClient> _registryClientFactory;
    private readonly MarketplaceSearchScheduler _registrySearchScheduler;
    private readonly TimeSpan _registryDetailSpinnerDelay;
    private readonly MarketplacePackageProfileViewModel _selectedLocalProfile = new();
    private readonly MarketplacePackageProfileViewModel _registryProfile = new();
    private IReadOnlyList<LocalStackLibraryItem> _allStacks = [];
    private IReadOnlyList<SunderStackPackageRequirement> _selectedPackageRequirements = [];
    private int _selectionVersion;
    private int _registrySelectionVersion;
    private int _registrySearchVersion;
    private CancellationTokenSource? _registryStackDetailsCancellation;
    private CancellationTokenSource? _registryStackDetailsSpinnerCancellation;
    private bool _selectedInstallPlanReady;
    private bool _selectedInstallPlanHasErrors;
    private readonly LatestAsyncRequest _registrySearchRequest = new();
    private readonly OwnedTaskObserver _tasks = new(nameof(StacksWindowViewModel));
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
        IRuntimeApiClient runtimeApiClient,
        RegistryPackageInstallService? registryInstallService = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? applyPackageLifecycleChangesAsync = null,
        Func<IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor>, CancellationToken, Task<IReadOnlyList<string>>>? notifyStackImportAppliedAsync = null,
        Func<Uri, IRegistryApiClient>? registryClientFactory = null,
        TimeSpan? registrySearchThrottleDelay = null,
        TimeSpan? registryDetailSpinnerDelay = null)
    {
        _library = library;
        _archivePicker = archivePicker;
        _runtimeApiClient = runtimeApiClient;
        _registryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        _applyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync ?? ((_, _) => Task.CompletedTask);
        _notifyStackImportAppliedAsync = notifyStackImportAppliedAsync ?? ((_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        _registryClientFactory = registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl));
        _registryDetailSpinnerDelay = registryDetailSpinnerDelay ?? RegistryDetailSpinnerDelay;
        _registrySearchScheduler = new MarketplaceSearchScheduler(
            SearchRegistryStacksCoreAsync,
            registrySearchThrottleDelay ?? TimeSpan.FromMilliseconds(MarketplaceSearchThrottleDelayMilliseconds));
        _registryUrlText = RegistryUrlHelper.DefaultRegistryUrl.ToString();
    }

    public ObservableCollection<LocalStackLibraryItemViewModel> Stacks { get; } = [];

    public ObservableCollection<StackPackageRequirementViewModel> SelectedPackages { get; } = [];

    public ObservableCollection<LocalStackDetailPackageViewModel> SelectedLocalDetails { get; } = [];

    public ObservableCollection<StackFragmentViewModel> SelectedFragments { get; } = [];

    public ObservableCollection<string> SelectedRequiredInputs { get; } = [];

    public ObservableCollection<StackPackageInstallPlanItemViewModel> SelectedInstallPlanItems { get; } = [];

    public ObservableCollection<string> SelectedInstallPlanWarnings { get; } = [];

    public ObservableCollection<string> SelectedInstallPlanErrors { get; } = [];

    public ObservableCollection<StackImportActionViewModel> SelectedImportActions { get; } = [];

    public ObservableCollection<StackRequiredInputValueViewModel> SelectedImportRequiredInputs { get; } = [];

    public ObservableCollection<string> SelectedImportWarnings { get; } = [];

    public ObservableCollection<string> SelectedImportErrors { get; } = [];

    public ObservableCollection<RegistryPackageMediaItemViewModel> SelectedStackProfileMedia => _selectedLocalProfile.Media;

    public LiveMarkdown.Avalonia.ObservableStringBuilder SelectedStackReadmeMarkdownBuilder => _selectedLocalProfile.ReadmeMarkdownBuilder;

    public ObservableCollection<RegistryStackSearchItemViewModel> RegistryStacks { get; } = [];

    public ObservableCollection<RegistryStackPackageRequirementViewModel> RegistrySelectedPackages { get; } = [];

    public ObservableCollection<LocalStackDetailPackageViewModel> RegistrySelectedDetails { get; } = [];

    public ObservableCollection<string> RegistrySelectedRequiredInputs { get; } = [];

    public ObservableCollection<RegistrySearchSortOptionViewModel> RegistrySortOptions { get; } = new(RegistrySearchSortOptionViewModel.Defaults);

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
    private LocalStackLibraryItemViewModel? _selectedStack;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _registrySearchText = string.Empty;

    [ObservableProperty]
    private RegistrySearchSortOptionViewModel? _selectedRegistrySortOption = RegistrySearchSortOptionViewModel.Defaults[0];

    [ObservableProperty]
    private RegistryStackSearchItemViewModel? _selectedRegistryStack;

    [ObservableProperty]
    private string _registryUrlText;

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
    private bool _isRegistryStackDetailsLoading;

    [ObservableProperty]
    private bool _registryStackDetailsLoaded;

    [ObservableProperty]
    private bool _showRegistryStackDetailsSpinner;

    [ObservableProperty]
    private string _registryStackDetailsError = string.Empty;

    public bool HasStacks => Stacks.Count > 0;

    public bool IsLocalMode => BrowserMode == StackBrowserMode.Local;

    public bool IsMarketplaceMode => BrowserMode == StackBrowserMode.Marketplace;

    public bool ShowNoStacks => IsLocalMode && !HasStacks && !IsBusy;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasRegistrySearchText => !string.IsNullOrWhiteSpace(RegistrySearchText);

    public bool HasRegistryUrlText => !string.IsNullOrWhiteSpace(RegistryUrlText);

    public bool HasRegistryStacks => RegistryStacks.Count > 0;

    public bool ShowNoRegistryStacks => IsMarketplaceMode && !HasRegistryStacks && !IsBusy;

    public bool HasSelection => SelectedStack is not null;

    public bool HasRegistrySelection => SelectedRegistryStack is not null;

    public bool HasRegistryStackDetailsError => !string.IsNullOrWhiteSpace(RegistryStackDetailsError);

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

    public bool HasSelectedInstallPlanItems => SelectedInstallPlanItems.Count > 0;

    public bool HasSelectedInstallPlanWarnings => SelectedInstallPlanWarnings.Count > 0;

    public bool HasSelectedInstallPlanErrors => SelectedInstallPlanErrors.Count > 0;

    public bool HasSelectedImportActions => SelectedImportActions.Count > 0;

    public bool HasSelectedImportRequiredInputs => SelectedImportRequiredInputs.Count > 0;

    public bool HasSelectedImportWarnings => SelectedImportWarnings.Count > 0;

    public bool HasSelectedImportErrors => SelectedImportErrors.Count > 0;

    public bool ShowSelectedInstallPlan => HasSelection && _selectedPackageRequirements.Count > 0;

    public bool ShowNoSelectedInstallPlanChanges => ShowSelectedInstallPlan
                                                   && _selectedInstallPlanReady
                                                   && !HasSelectedInstallPlanItems
                                                   && !HasSelectedInstallPlanErrors;

    public bool ShowSelectedImportPreview => HasSelection && HasSelectedFragments;

    public bool ShowSelectedDetails => IsLocalMode && HasSelection;

    public bool ShowRegistrySelectedDetails => IsMarketplaceMode && HasRegistrySelection;

    public bool ShowRegistryStackStats => ShowRegistryStackDetailsContent && HasSelectedRegistryStackStats;

    public bool ShowNoSelection => IsLocalMode && !HasSelection;

    public bool ShowNoRegistrySelection => IsMarketplaceMode && !HasRegistrySelection;

    public bool CanRefresh => !IsBusy;

    public bool CanImportStack => !IsBusy;

    public bool CanSearchRegistryStacks => !IsBusy && HasRegistryUrlText;

    public bool CanCreateStack => !IsBusy;

    public bool CanEditSelectedStack => !IsBusy && ShowSelectedDetails && SelectedStack is not null;

    public bool CanImportSelectedRegistryStack => !IsBusy && ShowRegistryStackDetailsContent && SelectedRegistryStack is not null;

    public bool CanUseSelectedRegistryStack => !IsBusy && ShowRegistryStackDetailsContent && SelectedRegistryStack is not null;

    public bool CanUseSelectedStack => !IsBusy && SelectedStack is not null;

    public bool CanDeleteSelectedRegistryStack => !IsBusy && SelectedRegistryStack is not null;

    public bool CanExportSelectedStack => !IsBusy && SelectedStack is not null;

    public bool ShowPublishSelectedStack => ShowSelectedDetails && SelectedStack?.IsPublished != true;

    public bool ShowUnpublishSelectedStack => ShowSelectedDetails && SelectedStack?.IsPublished == true;

    public bool CanPublishSelectedStack => !IsBusy && ShowPublishSelectedStack;

    public bool CanUnpublishSelectedStack => !IsBusy && ShowUnpublishSelectedStack;

    public bool ShowSelectedStackStarAction => ShowSelectedDetails && SelectedStack?.IsPublished == true;

    public bool CanToggleSelectedStackStar => !IsBusy && ShowSelectedStackStarAction;

    public bool ShowSelectedRegistryStackStarAction => ShowRegistryStackDetailsContent;

    public bool CanToggleSelectedRegistryStackStar => !IsBusy && ShowSelectedRegistryStackStarAction && SelectedRegistryStack is not null;

    public bool CanRemoveSelectedStack => !IsBusy && SelectedStack is not null;

    partial void OnSelectedStackChanged(LocalStackLibraryItemViewModel? value)
    {
        foreach (var stack in Stacks)
        {
            stack.IsSelected = ReferenceEquals(stack, value);
        }

        var selectionVersion = ++_selectionVersion;
        ApplySelectedStackSummary(value);
        NotifySelectionChanged();
        if (value is not null)
        {
            _tasks.Observe(LoadSelectedStackManifestAsync(value.Item, selectionVersion, _tasks.Token), "loading local Stack details");
            if (value.IsPublished)
            {
                _tasks.Observe(LoadSelectedPublishedStackStatsAsync(value, selectionVersion, _tasks.Token), "loading published Stack statistics");
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

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        RebuildStackList(SelectedStack?.StackId);
    }

    partial void OnRegistrySearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRegistrySearchText));
        if (IsMarketplaceMode && HasRegistryUrlText)
        {
            QueueRegistrySearch();
        }
    }

    partial void OnSelectedRegistrySortOptionChanged(RegistrySearchSortOptionViewModel? value)
    {
        if (IsMarketplaceMode && HasRegistryUrlText)
        {
            QueueRegistrySearch(TimeSpan.Zero);
        }
    }

    partial void OnSelectedRegistryStackChanged(RegistryStackSearchItemViewModel? value)
    {
        foreach (var stack in RegistryStacks)
        {
            stack.IsSelected = ReferenceEquals(stack, value);
        }

        var selectionVersion = BeginSelectedRegistryStackDetailsLoad(value);
        if (value is not null)
        {
            _tasks.Observe(
                LoadSelectedRegistryStackDetailsAsync(value.StackId, selectionVersion, _registryStackDetailsCancellation!),
                "loading Registry Stack details");
        }

        NotifyRegistrySelectionChanged();
        NotifyRegistryStackStateChanged();
    }

    partial void OnRegistryUrlTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRegistryUrlText));
        if (IsMarketplaceMode && HasRegistryUrlText)
        {
            QueueRegistrySearch();
        }

        NotifyRegistryStackStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registrySearchScheduler.Dispose();
        _registrySearchRequest.Dispose();
        _tasks.Dispose();
        CancelRegistryStackDetailsSpinnerDelay();
        CancelSelectedRegistryStackDetailsLoad();
        DisposeSelectedLocalDetails();
        DisposeRegistrySelectedDetails();
        _selectedLocalProfile.Dispose();
        _registryProfile.Dispose();
        _runtimeApiClient.Dispose();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_allStacks.Count == 0)
        {
            await RefreshLocalStacksAsync(cancellationToken);
        }

        if (IsMarketplaceMode && RegistryStacks.Count == 0 && HasRegistryUrlText)
        {
            await SearchRegistryStacksCoreAsync(cancellationToken);
        }
    }

    public CreateStackWizardViewModel CreateCreateStackWizardViewModel()
        => new(_library, _runtimeApiClient);

    public CreateStackWizardViewModel? CreateEditStackWizardViewModel()
        => SelectedStack is null
            ? null
            : new CreateStackWizardViewModel(_library, _runtimeApiClient, new CreateStackWizardEditContext(SelectedStack.Item));

    public UseStackWizardViewModel? CreateUseStackWizardViewModel()
        => SelectedStack is null
            ? null
            : new UseStackWizardViewModel(
                _library,
                SelectedStack.Item,
                _runtimeApiClient,
                _registryInstallService,
                _applyPackageLifecycleChangesAsync,
                _notifyStackImportAppliedAsync,
                _registryClientFactory,
                RegistryUrlText);

    public async Task RefreshAfterCreatedStackAsync(string? stackId)
    {
        _allStacks = await _library.ListAsync();
        RebuildStackList(stackId);
        StatusText = string.IsNullOrWhiteSpace(stackId)
            ? "Created local Stack."
            : $"Created local Stack '{stackId}'.";
    }

    public async Task RefreshAfterEditedStackAsync(string? stackId)
    {
        _allStacks = await _library.ListAsync();
        RebuildStackList(stackId);
        var selectedStack = SelectedStack;
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

        IsBusy = true;
        try
        {
            var upload = await _runtimeApiClient.UploadStackAsync(selectedStack.LocalPath);
            var result = await _runtimeApiClient.PublishRegistryStackAsync(new RuntimeRegistryPublishRequest(registryUrl.AbsoluteUri, upload.UploadId));
            if (!result.Success)
            {
                StatusText = $"Saved local Stack '{selectedStack.StackId}'. Registry update failed: {result.Errors.FirstOrDefault() ?? "publish failed"}.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var publishedStackId = string.IsNullOrWhiteSpace(result.StackId) ? selectedStack.PublishedStackId ?? selectedStack.StackId : result.StackId!;
            await _library.UpdatePublishStateAsync(
                selectedStack.StackId,
                registryUrl.ToString(),
                publishedStackId,
                selectedStack.Item.PublishedAtUtc ?? now,
                now);
            _allStacks = await _library.ListAsync();
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Saved and updated Registry Stack '{publishedStackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Saved local Stack '{selectedStack.StackId}'. Registry update failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    public async Task RefreshAfterUsedStackAsync()
    {
        var stackId = SelectedStack?.StackId;
        _allStacks = await _library.ListAsync();
        RebuildStackList(stackId);
        StatusText = stackId is null
            ? "Stack use completed."
            : $"Used local Stack '{stackId}'.";
    }

    public async Task ApplyLaunchRequestAsync(AppLaunchRequest request, CancellationToken cancellationToken = default)
    {
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
