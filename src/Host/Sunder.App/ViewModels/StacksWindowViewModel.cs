using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.PackageManagement;
using Sunder.Protocol;
using Sunder.Registry.Shared;

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
    private readonly Func<Uri, RegistryAuthToken?> _registryTokenProvider;
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
        Func<Uri, RegistryAuthToken?>? registryTokenProvider = null,
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
        _registryTokenProvider = registryTokenProvider ?? (registryUrl => SunderAuthStore.Load().GetToken(registryUrl));
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
            _ = LoadSelectedStackManifestAsync(value.Item, selectionVersion);
            if (value.IsPublished)
            {
                _ = LoadSelectedPublishedStackStatsAsync(value, selectionVersion);
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
            _ = LoadSelectedRegistryStackDetailsAsync(value.StackId, selectionVersion, _registryStackDetailsCancellation!);
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
        CancelRegistryStackDetailsSpinnerDelay();
        CancelSelectedRegistryStackDetailsLoad();
        DisposeSelectedLocalDetails();
        DisposeRegistrySelectedDetails();
        _selectedLocalProfile.Dispose();
        _registryProfile.Dispose();
        _runtimeApiClient.Dispose();
    }

    public async Task InitializeAsync()
    {
        if (_allStacks.Count == 0)
        {
            await RefreshLocalStacksAsync();
        }

        if (IsMarketplaceMode && RegistryStacks.Count == 0 && HasRegistryUrlText)
        {
            await SearchRegistryStacksAsync();
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

        if (!TryGetRegistryToken(registryUrl, "updating", out var token))
        {
            StatusText = $"Saved local Stack '{selectedStack.StackId}'. Sign in to the Registry to update the published Stack.";
            return;
        }

        IsBusy = true;
        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var result = await registryClient.PublishStackAsync(selectedStack.LocalPath, token.Token);
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

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsMarketplaceMode)
        {
            await SearchRegistryStacksAsync();
            return;
        }

        await RefreshLocalStacksAsync();
    }

    private async Task RefreshLocalStacksAsync()
    {
        IsBusy = true;
        try
        {
            var preferredStackId = SelectedStack?.StackId;
            _allStacks = await _library.ListAsync();
            RebuildStackList(preferredStackId);
            if (_allStacks.Count == 0)
            {
                StatusText = "No local Stacks yet. Import a .sunderstack file to start.";
            }
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

    [RelayCommand]
    private void ShowLocal()
    {
        BrowserMode = StackBrowserMode.Local;
    }

    [RelayCommand]
    private void ShowMarketplace()
    {
        BrowserMode = StackBrowserMode.Marketplace;
        if (RegistryStacks.Count == 0 && HasRegistryUrlText)
        {
            QueueRegistrySearch(TimeSpan.Zero);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void ClearRegistrySearch()
    {
        if (!string.IsNullOrEmpty(RegistrySearchText))
        {
            RegistrySearchText = string.Empty;
            return;
        }

        if (IsMarketplaceMode && HasRegistryUrlText)
        {
            QueueRegistrySearch(TimeSpan.Zero);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchRegistryStacks))]
    private async Task SearchRegistryStacksAsync()
    {
        CancelQueuedRegistrySearch();
        await SearchRegistryStacksCoreAsync();
    }

    private async Task SearchRegistryStacksCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        var searchVersion = ++_registrySearchVersion;
        IsBusy = true;
        SelectedRegistryStack = null;
        RegistryStacks.Clear();
        NotifyRegistryStackStateChanged();
        try
        {
            StatusText = "Searching Registry Stacks...";
            using var registryClient = _registryClientFactory(registryUrl);
            var query = string.IsNullOrWhiteSpace(RegistrySearchText) ? null : RegistrySearchText.Trim();
            var results = await registryClient.SearchStacksAsync(
                query,
                0,
                50,
                SelectedRegistrySortOption?.Sort ?? RegistrySearchSort.Downloads,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (searchVersion != _registrySearchVersion)
            {
                return;
            }

            foreach (var stack in results)
            {
                RegistryStacks.Add(new RegistryStackSearchItemViewModel(stack));
            }

            SelectedRegistryStack = RegistryStacks.FirstOrDefault();
            StatusText = RegistryStacks.Count == 0
                ? "No Registry Stacks matched the search."
                : $"Found {RegistryStacks.Count} Registry Stack{(RegistryStacks.Count == 1 ? string.Empty : "s")}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (searchVersion == _registrySearchVersion)
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (searchVersion == _registrySearchVersion)
            {
                IsBusy = false;
                NotifyRegistryStackStateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportSelectedRegistryStack))]
    private async Task ImportSelectedRegistryStackAsync()
        => await ImportSelectedRegistryStackAsLocalAsync(AppLaunchRequestKind.StackDetails);

    public async Task<bool> ImportSelectedRegistryStackAsLocalAsync(
        AppLaunchRequestKind launchKind = AppLaunchRequestKind.StackDetails,
        CancellationToken cancellationToken = default)
    {
        if (SelectedRegistryStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return false;
        }

        return await ImportRegistryStackAsync(SelectedRegistryStack.StackId, registryUrl, launchKind, cancellationToken) is not null;
    }

    [RelayCommand(CanExecute = nameof(CanImportStack))]
    private async Task ImportStackAsync()
        => await ImportStackWithPickerAsync();

    public async Task<bool> ImportStackWithPickerAsync(CancellationToken cancellationToken = default)
    {
        var path = await _archivePicker.PickStackPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return await ImportStackFromPathAsync(path, cancellationToken) is not null;
    }

    [RelayCommand(CanExecute = nameof(CanExportSelectedStack))]
    private async Task ExportSelectedStackAsync()
    {
        if (SelectedStack is null)
        {
            return;
        }

        var path = await _archivePicker.PickStackSavePathAsync(SelectedStack.StackId + ".sunderstack");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _library.ExportAsync(SelectedStack.Item, path);
            StatusText = $"Exported '{SelectedStack.Name}'.";
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

    [RelayCommand(CanExecute = nameof(CanPublishSelectedStack))]
    private async Task PublishSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        if (selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        if (!TryGetRegistryToken(registryUrl, "publishing", out var token))
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var result = await registryClient.PublishStackAsync(selectedStack.LocalPath, token.Token);
            if (!result.Success)
            {
                StatusText = result.Errors.FirstOrDefault() ?? "Registry Stack publish failed.";
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var publishedStackId = string.IsNullOrWhiteSpace(result.StackId) ? selectedStack.StackId : result.StackId!;
            await _library.UpdatePublishStateAsync(
                selectedStack.StackId,
                registryUrl.ToString(),
                publishedStackId,
                now,
                now);
            _allStacks = await _library.ListAsync();
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Published Stack '{publishedStackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnpublishSelectedStack))]
    private async Task UnpublishSelectedStackAsync()
    {
        var selectedStack = SelectedStack;
        var publishedStackId = selectedStack?.PublishedStackId;
        if (selectedStack is null || string.IsNullOrWhiteSpace(publishedStackId))
        {
            return;
        }

        if (!TryResolvePublishedRegistryUrl(selectedStack, out var registryUrl))
        {
            return;
        }

        if (!TryGetRegistryToken(registryUrl, "unpublishing", out var token))
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var result = await registryClient.DeleteStackAsync(publishedStackId, token.Token);
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Registry Stack unpublish requires sign-in as the Stack owner."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack unpublish failed.";
                return;
            }

            await _library.ClearPublishStateAsync(selectedStack.StackId);
            _allStacks = await _library.ListAsync();
            RebuildStackList(selectedStack.StackId);
            StatusText = result.Message ?? $"Unpublished Stack '{publishedStackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedStackStar))]
    private async Task ToggleSelectedStackStarAsync()
    {
        var selectedStack = SelectedStack;
        var publishedStackId = selectedStack?.PublishedStackId;
        if (selectedStack is null || string.IsNullOrWhiteSpace(publishedStackId))
        {
            return;
        }

        if (!TryResolvePublishedRegistryUrl(selectedStack, out var registryUrl))
        {
            return;
        }

        if (!TryGetRegistryToken(registryUrl, "starring", out var token))
        {
            NotifyCommandStateChanged();
            return;
        }

        IsBusy = true;
        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var result = SelectedStackIsStarred
                ? await registryClient.UnstarStackAsync(publishedStackId, token.Token)
                : await registryClient.StarStackAsync(publishedStackId, token.Token);
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Sign in to the Registry before starring a Stack."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack star update failed.";
                return;
            }

            ApplySelectedStackStats(result.Stats);
            StatusText = result.Message ?? "Updated Stack star.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedRegistryStackStar))]
    private async Task ToggleSelectedRegistryStackStarAsync()
    {
        var selectedStack = SelectedRegistryStack;
        if (selectedStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        if (!TryGetRegistryToken(registryUrl, "starring", out var token))
        {
            NotifyCommandStateChanged();
            return;
        }

        IsBusy = true;
        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var result = SelectedRegistryStackIsStarred
                ? await registryClient.UnstarStackAsync(selectedStack.StackId, token.Token)
                : await registryClient.StarStackAsync(selectedStack.StackId, token.Token);
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Sign in to the Registry before starring a Stack."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack star update failed.";
                return;
            }

            ApplySelectedRegistryStackStats(result.Stats);
            StatusText = result.Message ?? "Updated Stack star.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyRegistrySelectionChanged();
            NotifyRegistryStackStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRegistryStack))]
    private async Task DeleteSelectedRegistryStackAsync()
    {
        if (SelectedRegistryStack is null || !TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        if (!TryGetRegistryToken(registryUrl, "deleting", out var token))
        {
            return;
        }

        var selectedStack = SelectedRegistryStack;
        IsBusy = true;
        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var result = await registryClient.DeleteStackAsync(selectedStack.StackId, token.Token);
            if (!result.Success)
            {
                StatusText = result.Forbidden
                    ? "Registry Stack deletion requires sign-in as the Stack owner."
                    : result.Errors.FirstOrDefault() ?? "Registry Stack deletion failed.";
                return;
            }

            RegistryStacks.Remove(selectedStack);
            SelectedRegistryStack = RegistryStacks.FirstOrDefault();
            StatusText = result.Message ?? $"Deleted Registry Stack '{selectedStack.StackId}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyRegistryStackStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedStack))]
    private async Task RemoveSelectedStackAsync()
    {
        if (SelectedStack is null)
        {
            return;
        }

        var removedName = SelectedStack.Name;
        IsBusy = true;
        try
        {
            await _library.DeleteAsync(SelectedStack.Item);
            _allStacks = await _library.ListAsync();
            RebuildStackList();
            StatusText = $"Removed local Stack '{removedName}'.";
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

    private async Task<LocalStackLibraryItem?> ImportStackFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var item = await _library.ImportAsync(path, cancellationToken);
            _allStacks = await _library.ListAsync(cancellationToken);
            RebuildStackList(item.StackId);
            StatusText = $"Imported '{item.Name}'. Review it before use.";
            return item;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ShowLinkedRegistryStackAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StackId))
        {
            return;
        }

        if (request.RegistryUrl is not null)
        {
            RegistryUrlText = request.RegistryUrl.ToString();
        }

        BrowserMode = StackBrowserMode.Marketplace;
        RegistrySearchText = request.StackId;
        CancelQueuedRegistrySearch();
        if (!TryResolveRegistryUrlForRegistryAction(out _))
        {
            RegistryStacks.Clear();
            SelectedRegistryStack = null;
            NotifyRegistryStackStateChanged();
            return;
        }

        await SearchRegistryStacksCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedStack = RegistryStacks.FirstOrDefault(stack =>
            string.Equals(stack.StackId, request.StackId, StringComparison.OrdinalIgnoreCase));
        if (selectedStack is null)
        {
            RegistryStacks.Clear();
            SelectedRegistryStack = null;
            NotifyRegistryStackStateChanged();
            StatusText = $"Registry Stack '{request.StackId}' was not found.";
            return;
        }

        KeepOnlyRegistryStack(selectedStack);
        SelectedRegistryStack = selectedStack;
        StatusText = $"Loaded {selectedStack.StackId}.";
    }

    private void KeepOnlyRegistryStack(RegistryStackSearchItemViewModel stack)
    {
        for (var index = RegistryStacks.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(RegistryStacks[index], stack))
            {
                continue;
            }

            RegistryStacks.RemoveAt(index);
        }

        NotifyRegistryStackStateChanged();
    }

    private async Task<LocalStackLibraryItem?> ImportRegistryStackAsync(
        string stackId,
        Uri registryUrl,
        AppLaunchRequestKind launchKind,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "registry", Guid.NewGuid().ToString("N"));
        var tempPath = Path.Combine(tempDirectory, SunderStackFormat.BuildStackFileName(stackId));
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            StatusText = $"Downloading Registry Stack '{stackId}'...";
            using var registryClient = _registryClientFactory(registryUrl);
            var stack = await registryClient.GetStackAsync(stackId, cancellationToken);
            if (stack is null)
            {
                StatusText = $"Registry Stack '{stackId}' was not found.";
                return null;
            }

            await registryClient.DownloadStackAsync(stack.Artifact, stack.StackId, tempPath, cancellationToken);
            var item = await _library.ImportAsync(tempPath, cancellationToken);
            _allStacks = await _library.ListAsync(cancellationToken);
            RebuildStackList(item.StackId);
            StatusText = launchKind == AppLaunchRequestKind.StackUse
                ? $"Downloaded '{item.Name}' from the Registry. Review it before use."
                : $"Downloaded '{item.Name}' from the Registry.";
            return item;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
            TryDeleteDirectory(tempDirectory);
        }
    }

    private void RebuildStackList(string? preferredStackId = null)
    {
        var query = SearchText.Trim();
        var items = _allStacks
            .Where(item => MatchesSearch(item, query))
            .Select(item => new LocalStackLibraryItemViewModel(item))
            .ToArray();

        Stacks.Clear();
        foreach (var item in items)
        {
            Stacks.Add(item);
        }

        OnPropertyChanged(nameof(HasStacks));
        OnPropertyChanged(nameof(ShowNoStacks));

        var selected = !string.IsNullOrWhiteSpace(preferredStackId)
            ? Stacks.FirstOrDefault(item => string.Equals(item.StackId, preferredStackId, StringComparison.OrdinalIgnoreCase))
            : Stacks.FirstOrDefault();
        SelectedStack = selected;
        if (selected is null)
        {
            ApplySelectedStackSummary(null);
            NotifySelectionChanged();
        }
    }

    private async Task LoadSelectedStackManifestAsync(LocalStackLibraryItem item, int selectionVersion)
    {
        try
        {
            var manifest = await _library.ReadManifestAsync(item.LocalPath);
            if (selectionVersion != _selectionVersion)
            {
                return;
            }

            ApplySelectedStackManifest(manifest);
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion)
            {
                StatusText = ex.Message;
            }
        }
    }

    private async Task LoadSelectedPublishedStackStatsAsync(LocalStackLibraryItemViewModel stack, int selectionVersion)
    {
        if (string.IsNullOrWhiteSpace(stack.PublishedStackId)
            || !RegistryUrlHelper.TryParse(stack.RegistryUrl, out var registryUrl)
            || registryUrl is null)
        {
            return;
        }

        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var token = _registryTokenProvider(registryUrl);
            var details = token is not null && !string.IsNullOrWhiteSpace(token.Token)
                ? await registryClient.GetStackAsync(stack.PublishedStackId, token.Token)
                : await registryClient.GetStackAsync(stack.PublishedStackId);
            if (selectionVersion != _selectionVersion || SelectedStack?.StackId != stack.StackId)
            {
                return;
            }

            ApplySelectedStackStats(details?.Stats);
        }
        catch
        {
            // Published Stack stats are auxiliary; keep the local Stack details usable if the Registry is unavailable.
        }
    }

    private void ApplySelectedStackSummary(LocalStackLibraryItemViewModel? value)
    {
        SelectedPackages.Clear();
        DisposeSelectedLocalDetails();
        SelectedFragments.Clear();
        SelectedRequiredInputs.Clear();
        ClearSelectedInstallPlan();
        ClearSelectedImportPreview();
        _selectedPackageRequirements = [];

        if (value is null)
        {
            SelectedStackTitle = "No Stack selected";
            SelectedStackSubtitle = "Select a Stack to inspect its setup plan.";
            SelectedStackSummary = string.Empty;
            SelectedStackMetadata = string.Empty;
            SelectedStackPath = string.Empty;
            _selectedLocalProfile.Apply((RegistryStackProfile?)null);
            ClearSelectedStackStats();
            NotifySelectedDetailsChanged();
            return;
        }

        SelectedStackTitle = value.Name;
        SelectedStackSubtitle = value.StackId;
        SelectedStackSummary = value.Summary;
        SelectedStackMetadata = $"{value.PackageCount} package{(value.PackageCount == 1 ? string.Empty : "s")} · {value.FragmentCount} fragment{(value.FragmentCount == 1 ? string.Empty : "s")} · {value.UpdatedText}";
        SelectedStackPath = value.LocalPath;
        _selectedLocalProfile.Apply(ToRegistryStackProfile(value.Item));
        if (value.IsPublished)
        {
            HasSelectedStackStats = true;
            SelectedStackStatsText = "Loading Registry stats...";
            SelectedStackIsStarred = false;
            SelectedStackStarActionText = "Star";
        }
        else
        {
            ClearSelectedStackStats();
        }

        PopulateSelectedLocalDetails(value.Item, new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase));
        var selectionVersion = _selectionVersion;
        if (value.Item.Details?.Count > 0)
        {
            _ = RefreshSelectedLocalDetailIconsAsync(value.Item, selectionVersion);
        }

        NotifySelectedDetailsChanged();
    }

    private void ClearSelectedStackStats()
    {
        HasSelectedStackStats = false;
        SelectedStackStatsText = string.Empty;
        SelectedStackIsStarred = false;
        SelectedStackStarActionText = "Star";
    }

    private void ApplySelectedStackStats(RegistryStackStats? stats)
    {
        if (stats is null)
        {
            return;
        }

        HasSelectedStackStats = true;
        SelectedStackStatsText = FormatStackStats(stats);
        SelectedStackIsStarred = stats.IsStarred;
        SelectedStackStarActionText = stats.IsStarred ? "Unstar" : "Star";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ApplySelectedStackManifest(SunderStackManifest manifest)
    {
        _selectedPackageRequirements = manifest.Packages ?? [];
        SelectedPackages.Clear();
        foreach (var package in manifest.Packages ?? [])
        {
            SelectedPackages.Add(new StackPackageRequirementViewModel(package));
        }

        SelectedFragments.Clear();
        foreach (var fragment in manifest.Fragments ?? [])
        {
            SelectedFragments.Add(new StackFragmentViewModel(fragment));
        }

        SelectedRequiredInputs.Clear();
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task RefreshSelectedLocalDetailIconsAsync(LocalStackLibraryItem item, int selectionVersion)
    {
        try
        {
            var packageIcons = await LoadLocalDetailPackageIconsAsync(CancellationToken.None);
            if (selectionVersion != _selectionVersion || _disposed || SelectedStack?.StackId != item.StackId)
            {
                return;
            }

            PopulateSelectedLocalDetails(item, packageIcons);
            NotifySelectedDetailsChanged();
        }
        catch
        {
            // Local detail icons are decorative; keep glyph fallback if package metadata is unavailable.
        }
    }

    private void PopulateSelectedLocalDetails(LocalStackLibraryItem item, IReadOnlyDictionary<string, Uri?> packageIcons)
    {
        DisposeSelectedLocalDetails();
        foreach (var detailPackage in item.Details ?? [])
        {
            packageIcons.TryGetValue(detailPackage.PackageId, out var iconUri);
            iconUri ??= string.IsNullOrWhiteSpace(detailPackage.IconAssetPath)
                ? null
                : _runtimeApiClient.CreatePackageAssetUri(detailPackage.PackageId, detailPackage.IconAssetPath!);
            SelectedLocalDetails.Add(new LocalStackDetailPackageViewModel(detailPackage, iconUri));
        }
    }

    private async Task<IReadOnlyDictionary<string, Uri?>> LoadLocalDetailPackageIconsAsync(CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in await _runtimeApiClient.GetInstalledPackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        foreach (var package in await _runtimeApiClient.GetSessionPackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        foreach (var package in await _runtimeApiClient.GetActivePackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        return packages;
    }

    private void AddPackageIcon(IDictionary<string, Uri?> packages, string packageId, PackageIconDescriptor? icon)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        packages[packageId] = PackageIconUriResolver.Resolve(packageId, icon, _runtimeApiClient.CreatePackageAssetUri);
    }

    private static async Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadRegistryDetailPackageInfoAsync(
        IRegistryApiClient registryClient,
        IReadOnlyList<RegistryStackFragmentSummary> fragments,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, StackPackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in fragments
                     .Select(GetRegistryFragmentPackageId)
                     .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var package = string.IsNullOrWhiteSpace(bearerToken)
                    ? await registryClient.GetPackageAsync(packageId, cancellationToken)
                    : await registryClient.GetPackageAsync(packageId, bearerToken!, cancellationToken);
                if (package is null)
                {
                    continue;
                }

                packages[packageId] = new StackPackageInfo(
                    string.IsNullOrWhiteSpace(package.Name) ? packageId : package.Name,
                    null,
                    ResolveRegistryPackageIconUri(registryClient.RegistryUrl, package.IconUrl));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Registry package metadata is decorative; keep package-id fallback details.
            }
        }

        return packages;
    }

    private void PopulateRegistrySelectedDetails(
        IReadOnlyList<RegistryStackFragmentSummary> fragments,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        DisposeRegistrySelectedDetails();
        foreach (var packageGroup in fragments
                     .GroupBy(GetRegistryFragmentPackageId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => ResolveRegistryPackageDisplayName(group.Key, packageInfo), StringComparer.OrdinalIgnoreCase))
        {
            var packageId = string.IsNullOrWhiteSpace(packageGroup.Key) ? "unknown" : packageGroup.Key;
            packageInfo.TryGetValue(packageId, out var info);
            var package = new LocalStackDetailPackage(
                packageId,
                string.IsNullOrWhiteSpace(info?.DisplayName) ? packageId : info.DisplayName,
                BuildPackageGlyph(packageId),
                packageGroup
                    .Select(ToRegistryLocalDetailItem)
                    .OrderBy(item => StackContentKindLabels.FormatGroupName(item.Kind), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            RegistrySelectedDetails.Add(new LocalStackDetailPackageViewModel(package, info?.IconUri));
        }
    }

    private static LocalStackDetailItem ToRegistryLocalDetailItem(RegistryStackFragmentSummary fragment)
    {
        var itemId = string.IsNullOrWhiteSpace(fragment.SourceItemId) ? fragment.FragmentId : fragment.SourceItemId!;
        var displayName = string.IsNullOrWhiteSpace(fragment.DisplayName) ? itemId : fragment.DisplayName;
        var kind = StackContentKindLabels.InferKind(
            fragment.Kind,
            fragment.OwnerPackageId,
            schemaId: fragment.SchemaId,
            itemId: itemId,
            displayName: displayName,
            summary: fragment.Description);
        return new LocalStackDetailItem(
            itemId,
            displayName,
            BuildRegistryFragmentSummary(fragment),
            (fragment.DisplayDetails ?? [])
                .Select(detail => new LocalStackDetailValue(
                    string.IsNullOrWhiteSpace(detail.Label) ? "Value" : detail.Label,
                    string.IsNullOrWhiteSpace(detail.Value) ? "No preview value." : detail.Value,
                    detail.Behavior ?? string.Empty))
                .ToArray(),
            kind);
    }

    private static string BuildRegistryFragmentSummary(RegistryStackFragmentSummary fragment)
    {
        var detailLabels = (fragment.DisplayDetails ?? [])
            .Select(detail => detail.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Take(3)
            .ToArray();
        if (detailLabels.Length > 0)
        {
            return string.Join(", ", detailLabels);
        }

        if (!string.IsNullOrWhiteSpace(fragment.Description))
        {
            return fragment.Description!;
        }

        return string.IsNullOrWhiteSpace(fragment.SchemaId) ? "Selected setup values" : fragment.SchemaId;
    }

    private static string GetRegistryFragmentPackageId(RegistryStackFragmentSummary fragment)
        => string.IsNullOrWhiteSpace(fragment.OwnerPackageId) ? "unknown" : fragment.OwnerPackageId;

    private static string ResolveRegistryPackageDisplayName(
        string packageId,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
        => packageInfo.TryGetValue(packageId, out var info) && !string.IsNullOrWhiteSpace(info.DisplayName)
            ? info.DisplayName
            : packageId;

    private static Uri? ResolveRegistryPackageIconUri(Uri registryUrl, string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            return null;
        }

        var trimmed = iconUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return Uri.TryCreate(registryUrl, trimmed, out var relativeUri) ? relativeUri : null;
    }

    private static string BuildPackageGlyph(string packageId)
    {
        var first = packageId.FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "?" : char.ToUpperInvariant(first).ToString();
    }

    private int BeginSelectedRegistryStackDetailsLoad(RegistryStackSearchItemViewModel? value)
    {
        CancelSelectedRegistryStackDetailsLoad();
        CancelRegistryStackDetailsSpinnerDelay();
        var selectionVersion = ++_registrySelectionVersion;
        ApplySelectedRegistryStackSummary(value);

        RegistryStackDetailsError = string.Empty;
        RegistryStackDetailsLoaded = false;
        ShowRegistryStackDetailsSpinner = false;
        if (value is null)
        {
            IsRegistryStackDetailsLoading = false;
        }
        else
        {
            _registryStackDetailsCancellation = new CancellationTokenSource();
            IsRegistryStackDetailsLoading = true;
            QueueRegistryStackDetailsSpinner(value, selectionVersion);
        }

        NotifyRegistryStackDetailsStateChanged();
        return selectionVersion;
    }

    private void CompleteSelectedRegistryStackDetailsLoad(string? errorMessage = null)
    {
        CancelRegistryStackDetailsSpinnerDelay();
        ShowRegistryStackDetailsSpinner = false;
        RegistryStackDetailsError = errorMessage ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            SelectedRegistryStackSummary = "Stack details could not be loaded.";
        }

        RegistryStackDetailsLoaded = string.IsNullOrWhiteSpace(errorMessage);
        IsRegistryStackDetailsLoading = false;
        NotifyRegistryStackDetailsStateChanged();
    }

    private void QueueRegistryStackDetailsSpinner(RegistryStackSearchItemViewModel stack, int selectionVersion)
    {
        var spinnerCancellation = new CancellationTokenSource();
        _registryStackDetailsSpinnerCancellation = spinnerCancellation;
        _ = ShowRegistryStackDetailsSpinnerAfterDelayAsync(stack, selectionVersion, spinnerCancellation);
    }

    private async Task ShowRegistryStackDetailsSpinnerAfterDelayAsync(
        RegistryStackSearchItemViewModel stack,
        int selectionVersion,
        CancellationTokenSource spinnerCancellation)
    {
        try
        {
            await Task.Delay(_registryDetailSpinnerDelay, spinnerCancellation.Token);
            if (!_disposed
                && selectionVersion == _registrySelectionVersion
                && ReferenceEquals(SelectedRegistryStack, stack)
                && IsRegistryStackDetailsLoading
                && !RegistryStackDetailsLoaded)
            {
                ShowRegistryStackDetailsSpinner = true;
                OnPropertyChanged(nameof(ShowRegistryStackDetailsLoading));
            }
        }
        catch (OperationCanceledException) when (spinnerCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_registryStackDetailsSpinnerCancellation, spinnerCancellation))
            {
                _registryStackDetailsSpinnerCancellation = null;
            }

            spinnerCancellation.Dispose();
        }
    }

    private void CancelRegistryStackDetailsSpinnerDelay()
    {
        var spinnerCancellation = _registryStackDetailsSpinnerCancellation;
        if (spinnerCancellation is null)
        {
            return;
        }

        _registryStackDetailsSpinnerCancellation = null;
        spinnerCancellation.Cancel();
    }

    private void CancelSelectedRegistryStackDetailsLoad()
    {
        var selectionCancellation = _registryStackDetailsCancellation;
        if (selectionCancellation is null)
        {
            return;
        }

        _registryStackDetailsCancellation = null;
        selectionCancellation.Cancel();
    }

    private void ApplySelectedRegistryStackSummary(RegistryStackSearchItemViewModel? value)
    {
        RegistrySelectedPackages.Clear();
        DisposeRegistrySelectedDetails();
        RegistrySelectedRequiredInputs.Clear();
        ApplyRegistryStackProfile(null);
        ApplyRegistryAttributions(null, []);

        if (value is null)
        {
            SelectedRegistryStackTitle = "No Marketplace Stack selected";
            SelectedRegistryStackSubtitle = "Search Marketplace Stacks to inspect shared setup presets.";
            SelectedRegistryStackSummary = string.Empty;
            SelectedRegistryStackContentText = string.Empty;
            SelectedRegistryStackUpdatedText = string.Empty;
            ClearSelectedRegistryStackStats();
            NotifyRegistrySelectedDetailsChanged();
            return;
        }

        SelectedRegistryStackTitle = value.Name;
        SelectedRegistryStackSubtitle = value.StackId;
        SelectedRegistryStackSummary = string.Empty;
        SelectedRegistryStackContentText = string.Empty;
        SelectedRegistryStackUpdatedText = string.Empty;
        ClearSelectedRegistryStackStats();
        NotifyRegistrySelectedDetailsChanged();
    }

    private async Task LoadSelectedRegistryStackDetailsAsync(
        string stackId,
        int selectionVersion,
        CancellationTokenSource selectionCancellation)
    {
        var cancellationToken = selectionCancellation.Token;
        if (!TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            CompleteSelectedRegistryStackDetailsLoad("Enter a valid HTTP Registry URL before using Registry Stacks.");
            return;
        }

        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var token = _registryTokenProvider(registryUrl);
            var details = token is not null && !string.IsNullOrWhiteSpace(token.Token)
                ? await registryClient.GetStackAsync(stackId, token.Token, cancellationToken)
                : await registryClient.GetStackAsync(stackId, cancellationToken);
            if (selectionVersion != _registrySelectionVersion)
            {
                return;
            }

            if (details is null)
            {
                CompleteSelectedRegistryStackDetailsLoad($"Registry Stack '{stackId}' was not found.");
                StatusText = RegistryStackDetailsError;
                return;
            }

            var packageInfo = await LoadRegistryDetailPackageInfoAsync(
                registryClient,
                details.Fragments,
                token?.Token,
                cancellationToken);
            if (selectionVersion != _registrySelectionVersion)
            {
                return;
            }

            ApplySelectedRegistryStackDetails(details, packageInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (selectionVersion == _registrySelectionVersion)
            {
                CompleteSelectedRegistryStackDetailsLoad(ex.Message);
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_registryStackDetailsCancellation, selectionCancellation))
            {
                _registryStackDetailsCancellation = null;
            }

            selectionCancellation.Dispose();
        }
    }

    private void ApplySelectedRegistryStackDetails(
        RegistryStackDetails details,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        SelectedRegistryStackTitle = details.Name;
        SelectedRegistryStackSubtitle = details.StackId;
        SelectedRegistryStackSummary = string.IsNullOrWhiteSpace(details.Summary) ? string.Empty : details.Summary;
        SelectedRegistryStackContentText = $"{details.Packages.Count} package{(details.Packages.Count == 1 ? string.Empty : "s")} · {details.Fragments.Count} fragment{(details.Fragments.Count == 1 ? string.Empty : "s")}";
        SelectedRegistryStackUpdatedText = $"Updated {details.UpdatedAtUtc.LocalDateTime:g}";
        ApplySelectedRegistryStackStats(details.Stats);
        ApplyRegistryStackProfile(details.Profile);
        ApplyRegistryAttributions(details.Creator, details.Maintainers ?? []);

        RegistrySelectedPackages.Clear();
        foreach (var package in details.Packages)
        {
            RegistrySelectedPackages.Add(new RegistryStackPackageRequirementViewModel(package));
        }

        PopulateRegistrySelectedDetails(details.Fragments, packageInfo);

        RegistrySelectedRequiredInputs.Clear();
        foreach (var input in details.RequiredInputs)
        {
            RegistrySelectedRequiredInputs.Add(input.Label);
        }

        CompleteSelectedRegistryStackDetailsLoad();
        NotifyRegistrySelectedDetailsChanged();
    }

    private void ApplyRegistryStackProfile(RegistryStackProfile? profile)
    {
        var shortDescription = _registryProfile.Apply(profile);
        if (!string.IsNullOrWhiteSpace(shortDescription))
        {
            SelectedRegistryStackSummary = shortDescription;
        }

        NotifyRegistryProfileChanged();
    }

    private void ApplyRegistryAttributions(
        RegistryUserAttribution? creator,
        IReadOnlyList<RegistryUserAttribution> maintainers)
    {
        var creators = creator is null
            ? Array.Empty<RegistryUserAttributionViewModel>()
            : new[] { new RegistryUserAttributionViewModel(creator) };
        var maintainerAttributions = maintainers
            .Where(maintainer => creator is null || !maintainer.IsOwner)
            .Select(maintainer => new RegistryUserAttributionViewModel(maintainer))
            .ToArray();

        RegistryCreators.ReplaceWith(creators);
        RegistryMaintainers.ReplaceWith(maintainerAttributions);
        RegistryAttributions.ReplaceWith(creators.Concat(maintainerAttributions));
        OnPropertyChanged(nameof(HasRegistryAttributions));
        OnPropertyChanged(nameof(HasRegistryCreators));
        OnPropertyChanged(nameof(HasRegistryMaintainers));
    }

    private void ClearSelectedRegistryStackStats()
    {
        HasSelectedRegistryStackStats = false;
        SelectedRegistryStackStatsText = string.Empty;
        SelectedRegistryStackIsStarred = false;
        SelectedRegistryStackStarActionText = "Star";
    }

    private void ApplySelectedRegistryStackStats(RegistryStackStats? stats)
    {
        if (stats is null)
        {
            return;
        }

        HasSelectedRegistryStackStats = true;
        SelectedRegistryStackStatsText = FormatStackStats(stats);
        SelectedRegistryStackIsStarred = stats.IsStarred;
        SelectedRegistryStackStarActionText = stats.IsStarred ? "Unstar" : "Star";
        NotifyRegistrySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task RefreshSelectedInstallPlanAsync(int selectionVersion)
    {
        ClearSelectedInstallPlan();
        if (_selectedPackageRequirements.Count == 0)
        {
            NotifySelectedDetailsChanged();
            return;
        }

        if (!TryResolveRegistryUrl(out var registryUrl))
        {
            return;
        }

        try
        {
            StatusText = "Resolving Stack package graph...";
            using var registryClient = _registryClientFactory(registryUrl);
            var plan = await _registryInstallService.ResolveInstallPlanForPackagesAsync(
                _selectedPackageRequirements,
                registryClient,
                _runtimeApiClient,
                progress => StatusText = progress.StatusText);
            if (selectionVersion != _selectionVersion || _disposed)
            {
                return;
            }

            ApplySelectedInstallPlan(plan);
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion && !_disposed)
            {
                SelectedInstallPlanErrors.Add(ex.Message);
                _selectedInstallPlanHasErrors = true;
                _selectedInstallPlanReady = true;
                StatusText = ex.Message;
                NotifySelectedDetailsChanged();
            }
        }
    }

    private void ApplySelectedInstallPlan(RegistryResolveInstallPlanResponse plan)
    {
        ClearSelectedInstallPlan();
        foreach (var item in plan.Items)
        {
            SelectedInstallPlanItems.Add(new StackPackageInstallPlanItemViewModel(item));
        }

        foreach (var warning in plan.Warnings)
        {
            SelectedInstallPlanWarnings.Add(warning);
        }

        foreach (var error in plan.Errors.Concat(plan.Conflicts.Select(conflict => conflict.Message)))
        {
            SelectedInstallPlanErrors.Add(error);
        }

        _selectedInstallPlanReady = true;
        _selectedInstallPlanHasErrors = !plan.Success;
        StatusText = plan.Success
            ? plan.Items.Count == 0
                ? "All Stack package requirements are already satisfied."
                : $"Stack package graph resolved {plan.Items.Count} package change{(plan.Items.Count == 1 ? string.Empty : "s")}."
            : SelectedInstallPlanErrors.FirstOrDefault() ?? "Stack package graph resolution failed.";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
        ClearSelectedImportPreview();
    }

    private void ClearSelectedInstallPlan()
    {
        SelectedInstallPlanItems.Clear();
        SelectedInstallPlanWarnings.Clear();
        SelectedInstallPlanErrors.Clear();
        _selectedInstallPlanReady = false;
        _selectedInstallPlanHasErrors = false;
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task RefreshSelectedImportPreviewAsync(int selectionVersion)
    {
        var existingInputValues = GetImportInputValues(includeEmptyValues: true);
        ClearSelectedImportPreview();
        if (SelectedStack is null || !HasSelectedFragments)
        {
            NotifySelectedDetailsChanged();
            return;
        }

        try
        {
            StatusText = "Previewing Stack setup actions...";
            var preview = await _runtimeApiClient.PreviewStackImportAsync(new RuntimeStackImportPreviewRequest(
                SelectedStack.LocalPath,
                GetSelectedFragmentIds(),
                existingInputValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>()));
            if (selectionVersion != _selectionVersion || _disposed)
            {
                return;
            }

            foreach (var action in preview.Actions)
            {
                SelectedImportActions.Add(new StackImportActionViewModel(action));
            }

            foreach (var input in preview.RequiredInputs)
            {
                SelectedImportRequiredInputs.Add(new StackRequiredInputValueViewModel(
                    input,
                    existingInputValues.TryGetValue(input.InputId, out var value) ? value : null,
                    NotifyImportRequiredInputChanged));
            }

            foreach (var warning in preview.Warnings)
            {
                SelectedImportWarnings.Add(warning);
            }

            foreach (var conflict in preview.Conflicts)
            {
                var prefix = string.Equals(conflict.Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning";
                (prefix == "Error" ? SelectedImportErrors : SelectedImportWarnings).Add($"{prefix}: {conflict.Message}");
            }

            foreach (var error in preview.Errors)
            {
                SelectedImportErrors.Add(error);
            }

            StatusText = preview.Success
                ? SelectedImportActions.Count == 0
                    ? "No actions are needed for the selected fragments."
                    : $"Previewed {SelectedImportActions.Count} setup action{(SelectedImportActions.Count == 1 ? string.Empty : "s")}."
                : SelectedImportErrors.FirstOrDefault() ?? "Stack setup preview failed.";
            NotifySelectedDetailsChanged();
            NotifyCommandStateChanged();
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion && !_disposed)
            {
                SelectedImportErrors.Add(ex.Message);
                StatusText = ex.Message;
                NotifySelectedDetailsChanged();
                NotifyCommandStateChanged();
            }
        }
    }

    private void ClearSelectedImportPreview()
    {
        SelectedImportActions.Clear();
        SelectedImportRequiredInputs.Clear();
        SelectedImportWarnings.Clear();
        SelectedImportErrors.Clear();
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ApplyInstallResult(RegistryPackageInstallExecutionResult result)
    {
        SelectedInstallPlanWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            SelectedInstallPlanWarnings.Add(warning);
        }

        SelectedInstallPlanErrors.Clear();
        foreach (var error in result.Errors)
        {
            SelectedInstallPlanErrors.Add(error);
        }

        StatusText = result.Success
            ? result.PlanItems.Count == 0
                ? "Stack package requirements are already installed. Fragment import will be enabled when package contributors are wired."
                : $"Installed {result.PlanItems.Count} Stack package change{(result.PlanItems.Count == 1 ? string.Empty : "s")}. Fragment import will be enabled when package contributors are wired."
            : result.Message;
        NotifySelectedDetailsChanged();
    }

    private async Task ApplySelectedStackImportAsync()
    {
        if (SelectedStack is null)
        {
            return;
        }

        if (SelectedImportActions.Count == 0 && !HasSelectedImportErrors)
        {
            await RefreshSelectedImportPreviewAsync(_selectionVersion);
        }

        if (HasSelectedImportErrors)
        {
            StatusText = SelectedImportErrors.FirstOrDefault() ?? "Resolve Stack setup preview errors before importing.";
            return;
        }

        var selectedActionIds = SelectedImportActions
            .Where(action => action.IsSelected)
            .Select(action => action.ActionId)
            .ToArray();
        if (selectedActionIds.Length == 0)
        {
            StatusText = "No Stack setup actions are selected.";
            return;
        }

        var missingRequiredInput = SelectedImportRequiredInputs.FirstOrDefault(input => input.IsMissingRequiredValue);
        if (missingRequiredInput is not null)
        {
            StatusText = $"Enter a value for '{missingRequiredInput.Label}' before importing this Stack.";
            return;
        }

        var result = await _runtimeApiClient.ImportStackAsync(new RuntimeStackImportRequest(
            SelectedStack.LocalPath,
            GetSelectedFragmentIds(),
            GetImportInputValues(),
            new Dictionary<string, string>(),
            selectedActionIds));

        SelectedImportWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            SelectedImportWarnings.Add(warning);
        }

        SelectedImportErrors.Clear();
        foreach (var error in result.Errors)
        {
            SelectedImportErrors.Add(error);
        }

        await NotifyStackImportAppliedAsync(result.AppliedContributions, CancellationToken.None);

        StatusText = result.Success
            ? $"Imported {result.ImportedItems.Count} Stack setup item{(result.ImportedItems.Count == 1 ? string.Empty : "s")}."
            : result.Errors.FirstOrDefault() ?? "Stack setup import failed.";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task NotifyStackImportAppliedAsync(
        IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> appliedContributions,
        CancellationToken cancellationToken)
    {
        if (appliedContributions.Count == 0)
        {
            return;
        }

        var warnings = await _notifyStackImportAppliedAsync(appliedContributions, cancellationToken);
        foreach (var warning in warnings)
        {
            SelectedImportWarnings.Add(warning);
        }
    }

    private bool TryResolveRegistryUrl(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(RegistryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before resolving or using this Stack.";
        SelectedInstallPlanErrors.Clear();
        SelectedInstallPlanErrors.Add(StatusText);
        _selectedInstallPlanHasErrors = true;
        _selectedInstallPlanReady = true;
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
        return false;
    }

    private bool TryResolveRegistryUrlForRegistryAction(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(RegistryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before using Registry Stacks.";
        NotifyRegistryStackStateChanged();
        return false;
    }

    private bool TryResolvePublishedRegistryUrl(LocalStackLibraryItemViewModel stack, out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(stack.RegistryUrl, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        return TryResolveRegistryUrlForRegistryAction(out registryUrl);
    }

    private bool TryGetRegistryToken(Uri registryUrl, string actionName, out RegistryAuthToken token)
    {
        var resolvedToken = _registryTokenProvider(registryUrl);
        if (resolvedToken is null || string.IsNullOrWhiteSpace(resolvedToken.Token))
        {
            token = default!;
            StatusText = $"Sign in to the Registry before {actionName} a Stack.";
            return false;
        }

        if (resolvedToken.ExpiresAtUtc is not null && resolvedToken.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            token = default!;
            StatusText = $"Saved Registry sign-in has expired. Sign in again before {actionName} a Stack.";
            return false;
        }

        token = resolvedToken;
        return true;
    }

    private static bool MatchesSearch(LocalStackLibraryItem item, string query)
        => string.IsNullOrWhiteSpace(query)
           || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || item.StackId.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (item.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private void QueueRegistrySearch(TimeSpan? delay = null)
        => _registrySearchScheduler.Queue(delay);

    private void CancelQueuedRegistrySearch()
        => _registrySearchScheduler.Cancel();

    private static string FormatStackStats(RegistryStackStats stats)
        => $"{FormatCount(stats.Stars, "star")} · {FormatCount(stats.TotalDownloads, "download")}";

    private static string FormatCount(long count, string singular)
        => $"{count:N0} {singular}{(count == 1 ? string.Empty : "s")}";

    private void NotifyRegistryStackStateChanged()
    {
        OnPropertyChanged(nameof(HasRegistryStacks));
        OnPropertyChanged(nameof(ShowNoRegistryStacks));
        OnPropertyChanged(nameof(CanSearchRegistryStacks));
        OnPropertyChanged(nameof(CanCreateStack));
        OnPropertyChanged(nameof(CanEditSelectedStack));
        OnPropertyChanged(nameof(CanImportSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedRegistryStack));
        OnPropertyChanged(nameof(CanDeleteSelectedRegistryStack));
        OnPropertyChanged(nameof(CanToggleSelectedRegistryStackStar));
        SearchRegistryStacksCommand.NotifyCanExecuteChanged();
        ImportSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        DeleteSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        ToggleSelectedRegistryStackStarCommand.NotifyCanExecuteChanged();
    }

    private void NotifyBrowserModeChanged()
    {
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsMarketplaceMode));
        OnPropertyChanged(nameof(ShowNoStacks));
        OnPropertyChanged(nameof(ShowNoRegistryStacks));
    }

    private void NotifyRegistrySelectionChanged()
    {
        OnPropertyChanged(nameof(HasRegistrySelection));
        OnPropertyChanged(nameof(ShowRegistrySelectedDetails));
        OnPropertyChanged(nameof(ShowNoRegistrySelection));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsLoading));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsContent));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsError));
        NotifyRegistrySelectedDetailsChanged();
    }

    private void NotifyRegistryStackDetailsStateChanged()
    {
        OnPropertyChanged(nameof(HasRegistryStackDetailsError));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsSpinner));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsLoading));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsContent));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsError));
        NotifyRegistrySelectedDetailsChanged();
        NotifyRegistryStackStateChanged();
    }

    private void NotifyRegistrySelectedDetailsChanged()
    {
        OnPropertyChanged(nameof(HasRegistrySelectedSummary));
        OnPropertyChanged(nameof(ShowRegistryStackDetailsContent));
        OnPropertyChanged(nameof(HasRegistrySelectedPackages));
        OnPropertyChanged(nameof(HasRegistrySelectedFragments));
        OnPropertyChanged(nameof(HasRegistrySelectedRequiredInputs));
        OnPropertyChanged(nameof(ShowRegistryStackStats));
        OnPropertyChanged(nameof(ShowSelectedRegistryStackStarAction));
        NotifyRegistryProfileChanged();
    }

    private void NotifyRegistryProfileChanged()
    {
        OnPropertyChanged(nameof(HasRegistryReadme));
        OnPropertyChanged(nameof(HasRegistryProfileLinks));
        OnPropertyChanged(nameof(HasRegistryProfileMetadata));
        OnPropertyChanged(nameof(HasRegistryProfileTags));
        OnPropertyChanged(nameof(HasRegistryProfile));
        OnPropertyChanged(nameof(HasRegistryProfileMedia));
        OnPropertyChanged(nameof(HasRegistryAttributions));
        OnPropertyChanged(nameof(HasRegistryCreators));
        OnPropertyChanged(nameof(HasRegistryMaintainers));
    }

    private void NotifyImportRequiredInputChanged()
    {
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void DisposeSelectedLocalDetails()
    {
        foreach (var detail in SelectedLocalDetails)
        {
            detail.Dispose();
        }

        SelectedLocalDetails.Clear();
    }

    private void DisposeRegistrySelectedDetails()
    {
        foreach (var detail in RegistrySelectedDetails)
        {
            detail.Dispose();
        }

        RegistrySelectedDetails.Clear();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowSelectedDetails));
        OnPropertyChanged(nameof(ShowPublishSelectedStack));
        OnPropertyChanged(nameof(ShowUnpublishSelectedStack));
        OnPropertyChanged(nameof(ShowSelectedStackStats));
        OnPropertyChanged(nameof(ShowSelectedStackStarAction));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(CanEditSelectedStack));
        NotifyCommandStateChanged();
    }

    private void NotifySelectedDetailsChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSummary));
        OnPropertyChanged(nameof(HasSelectedReadme));
        OnPropertyChanged(nameof(HasSelectedProfileMedia));
        OnPropertyChanged(nameof(HasSelectedPackages));
        OnPropertyChanged(nameof(HasSelectedLocalDetails));
        OnPropertyChanged(nameof(HasSelectedFragments));
        OnPropertyChanged(nameof(ShowSelectedFragments));
        OnPropertyChanged(nameof(HasSelectedRequiredInputs));
        OnPropertyChanged(nameof(HasSelectedInstallPlanItems));
        OnPropertyChanged(nameof(HasSelectedInstallPlanWarnings));
        OnPropertyChanged(nameof(HasSelectedInstallPlanErrors));
        OnPropertyChanged(nameof(HasSelectedImportActions));
        OnPropertyChanged(nameof(HasSelectedImportRequiredInputs));
        OnPropertyChanged(nameof(HasSelectedImportWarnings));
        OnPropertyChanged(nameof(HasSelectedImportErrors));
        OnPropertyChanged(nameof(ShowSelectedInstallPlan));
        OnPropertyChanged(nameof(ShowNoSelectedInstallPlanChanges));
        OnPropertyChanged(nameof(ShowSelectedImportPreview));
        OnPropertyChanged(nameof(ShowPublishSelectedStack));
        OnPropertyChanged(nameof(ShowUnpublishSelectedStack));
        OnPropertyChanged(nameof(ShowSelectedStackStats));
        OnPropertyChanged(nameof(ShowSelectedStackStarAction));
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanImportStack));
        OnPropertyChanged(nameof(CanSearchRegistryStacks));
        OnPropertyChanged(nameof(CanEditSelectedStack));
        OnPropertyChanged(nameof(CanImportSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedStack));
        OnPropertyChanged(nameof(CanDeleteSelectedRegistryStack));
        OnPropertyChanged(nameof(CanExportSelectedStack));
        OnPropertyChanged(nameof(CanPublishSelectedStack));
        OnPropertyChanged(nameof(CanUnpublishSelectedStack));
        OnPropertyChanged(nameof(CanToggleSelectedStackStar));
        OnPropertyChanged(nameof(CanToggleSelectedRegistryStackStar));
        OnPropertyChanged(nameof(CanRemoveSelectedStack));
        RefreshCommand.NotifyCanExecuteChanged();
        ImportStackCommand.NotifyCanExecuteChanged();
        SearchRegistryStacksCommand.NotifyCanExecuteChanged();
        ImportSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        DeleteSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        ExportSelectedStackCommand.NotifyCanExecuteChanged();
        PublishSelectedStackCommand.NotifyCanExecuteChanged();
        UnpublishSelectedStackCommand.NotifyCanExecuteChanged();
        ToggleSelectedStackStarCommand.NotifyCanExecuteChanged();
        ToggleSelectedRegistryStackStarCommand.NotifyCanExecuteChanged();
        RemoveSelectedStackCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<string> GetSelectedFragmentIds()
        => SelectedFragments
            .Where(fragment => fragment.IsSelected)
            .Select(fragment => fragment.FragmentId)
            .ToArray();

    private static RegistryStackProfile ToRegistryStackProfile(LocalStackLibraryItem item)
        => new(
            item.StackId,
            item.Summary,
            item.ReadmeMarkdown,
            WebsiteUrl: null,
            SourceUrl: null,
            IssueTrackerUrl: null,
            License: null,
            Tags: [],
            Media: (item.Media ?? [])
                .OrderBy(media => media.SortOrder)
                .Select(media => new RegistryStackMedia(
                    Guid.NewGuid(),
                    media.FileName,
                    media.ContentType,
                    media.Size,
                    media.AltText,
                    media.SortOrder,
                    new Uri(media.LocalPath).AbsoluteUri))
                .ToArray(),
            item.UpdatedAtUtc);

    private Dictionary<string, string> GetImportInputValues(bool includeEmptyValues = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in SelectedImportRequiredInputs)
        {
            if (includeEmptyValues || !string.IsNullOrWhiteSpace(input.Value))
            {
                values[input.InputId] = input.Value;
            }
        }

        return values;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary Registry Stack download cleanup is best effort.
        }
    }
}

public sealed partial class RegistryStackSearchItemViewModel(RegistryStackSummary stack) : ViewModelBase
{
    public string StackId { get; } = stack.StackId;

    public string Name { get; } = stack.Name;

    public string Summary { get; } = string.IsNullOrWhiteSpace(stack.Summary) ? "No summary provided." : stack.Summary;

    public int PackageCount { get; } = stack.PackageCount;

    public int FragmentCount { get; } = stack.FragmentCount;

    public RegistryStackStats? Stats { get; } = stack.Stats;

    public string UpdatedText { get; } = $"Updated {stack.UpdatedAtUtc.LocalDateTime:g}";

    public string CountText { get; } = $"{stack.PackageCount} package{(stack.PackageCount == 1 ? string.Empty : "s")} · {stack.FragmentCount} fragment{(stack.FragmentCount == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class RegistryStackPackageRequirementViewModel(RegistryStackPackageRequirement package)
{
    public string PackageId { get; } = package.PackageId;

    public string InstallText { get; } = BuildInstallText(package);

    private static string BuildInstallText(RegistryStackPackageRequirement package)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(package.InstallTag))
        {
            parts.Add(package.InstallTag);
        }

        if (!string.IsNullOrWhiteSpace(package.MinimumVersion))
        {
            parts.Add($">= {package.MinimumVersion}");
        }

        if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion))
        {
            parts.Add($"created with {package.CreatedWithVersion}");
        }

        parts.Add(package.Required ? "required" : "optional");
        return string.Join(" · ", parts);
    }
}

public sealed partial class LocalStackLibraryItemViewModel(LocalStackLibraryItem item) : ViewModelBase
{
    public LocalStackLibraryItem Item { get; } = item;

    public string StackId => Item.StackId;

    public string Name => Item.Name;

    public string Summary => string.IsNullOrWhiteSpace(Item.Summary) ? "No summary provided." : Item.Summary;

    public string LocalPath => Item.LocalPath;

    public string? RegistryUrl => Item.RegistryUrl;

    public string? PublishedStackId => Item.PublishedStackId;

    public bool IsPublished => !string.IsNullOrWhiteSpace(Item.PublishedStackId);

    public int PackageCount => Item.PackageCount;

    public int FragmentCount => Item.FragmentCount;

    public string UpdatedText => $"Updated {Item.UpdatedAtUtc.LocalDateTime:g}";

    public string CountText => $"{PackageCount} package{(PackageCount == 1 ? string.Empty : "s")} · {FragmentCount} fragment{(FragmentCount == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class LocalStackDetailPackageViewModel(LocalStackDetailPackage package, Uri? iconUri) : PackageIconItemViewModel(iconUri)
{
    public string PackageId { get; } = package.PackageId;

    public string DisplayName { get; } = package.DisplayName;

    public string Glyph { get; } = string.IsNullOrWhiteSpace(package.Glyph) ? "?" : package.Glyph;

    public IReadOnlyList<LocalStackDetailItemViewModel> Items { get; } = package.Items
        .Select(item => new LocalStackDetailItemViewModel(item, package.PackageId))
        .ToArray();

    public IReadOnlyList<LocalStackDetailKindGroupViewModel> ItemGroups { get; } = package.Items
        .Select(item => new LocalStackDetailItemViewModel(item, package.PackageId))
        .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => StackContentKindLabels.FormatGroupName(group.Key), StringComparer.OrdinalIgnoreCase)
        .Select(group => new LocalStackDetailKindGroupViewModel(group.Key, group.ToArray()))
        .ToArray();

    public string CountText => $"{Items.Count} setup item{(Items.Count == 1 ? string.Empty : "s")}";

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandActionText));
        OnPropertyChanged(nameof(IsCollapsed));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}

public sealed class LocalStackDetailKindGroupViewModel(string? kind, IReadOnlyList<LocalStackDetailItemViewModel> items)
{
    public string? Kind { get; } = kind;

    public string DisplayName { get; } = StackContentKindLabels.FormatGroupName(kind);

    public IReadOnlyList<LocalStackDetailItemViewModel> Items { get; } = items;

    public string CountText => $"{Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";
}

public sealed partial class LocalStackDetailItemViewModel(LocalStackDetailItem item, string packageId) : ViewModelBase
{
    public string ItemId { get; } = item.ItemId;

    public string DisplayName { get; } = item.DisplayName;

    public string Summary { get; } = string.IsNullOrWhiteSpace(item.Summary) ? "Selected setup values" : item.Summary;

    public string? Kind { get; } = StackContentKindLabels.InferKind(
        item.Kind,
        packageId,
        itemId: item.ItemId,
        displayName: item.DisplayName,
        summary: item.Summary);

    public IReadOnlyList<LocalStackDetailValueViewModel> Values { get; } = item.Values
        .Select(value => new LocalStackDetailValueViewModel(value))
        .ToArray();

    public bool HasValues => Values.Count > 0;

    public string DetailCountText => $"{Values.Count} value{(Values.Count == 1 ? string.Empty : "s")}";

    public string ExpandActionText => IsExpanded ? "Hide details" : "Show details";

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandActionText));
        OnPropertyChanged(nameof(IsCollapsed));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}

public sealed partial class LocalStackDetailValueViewModel(LocalStackDetailValue value) : ViewModelBase
{
    public string Label { get; } = value.Label;

    public string Value { get; } = value.Value;

    public string ValuePreview { get; } = StackDisplayFormatters.ShortenSingleLine(value.Value, 180);

    public string Behavior { get; } = value.Behavior;

    public bool HasBehavior => !string.IsNullOrWhiteSpace(Behavior);

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandActionText));
        OnPropertyChanged(nameof(IsCollapsed));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

}

public sealed class StackPackageRequirementViewModel(SunderStackPackageRequirement package)
{
    public string PackageId { get; } = package.PackageId ?? "unknown";

    public string InstallText { get; } = StackDisplayFormatters.PackageRequirementText(package);
}

public sealed class StackPackageInstallPlanItemViewModel(RegistryPackageInstallPlanItem item)
{
    public string PackageId { get; } = item.PackageId;

    public string VersionText { get; } = item.CurrentVersion is null
        ? $"Install {item.Version}"
        : item.IsUpdate
            ? $"Update {item.CurrentVersion} -> {item.Version}"
            : $"Keep {item.Version}";

    public string DependencyText { get; } = item.DependsOn.Count == 0
        ? "No package dependencies"
        : string.Join(", ", item.DependsOn.Select(dependency => $"{dependency.PackageId} {dependency.VersionRange}"));

    public bool HasDeprecatedMessage { get; } = !string.IsNullOrWhiteSpace(item.DeprecatedMessage);

    public string DeprecatedMessage { get; } = item.DeprecatedMessage ?? string.Empty;
}

public sealed partial class StackImportActionViewModel(RuntimeStackImportActionDescriptor action) : ViewModelBase
{
    public string ActionId { get; } = action.ActionId;

    public string DisplayName { get; } = action.DisplayName;

    public string Subtitle { get; } = $"{action.ContributorId} · {action.Kind}";

    public string Description { get; } = string.IsNullOrWhiteSpace(action.Description) ? "No description provided." : action.Description;

    [ObservableProperty]
    private bool _isSelected = action.DefaultSelected;
}

public sealed partial class StackRequiredInputValueViewModel(RuntimeStackRequiredInputDescriptor input, string? currentValue, Action valueChanged) : ViewModelBase
{
    public string InputId { get; } = input.InputId;

    public string Label { get; } = input.Label;

    public string ContributorId { get; } = input.ContributorId;

    public bool Required { get; } = input.Required;

    public string Description { get; } = string.IsNullOrWhiteSpace(input.Description) ? "Provide this value locally before import." : input.Description;

    public string Placeholder { get; } = input.Required ? "Required" : "Optional";

    public bool IsMissingRequiredValue => Required && string.IsNullOrWhiteSpace(Value);

    [ObservableProperty]
    private string _value = currentValue ?? input.DefaultValue ?? string.Empty;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsMissingRequiredValue));
        valueChanged();
    }
}

public sealed partial class StackFragmentViewModel(SunderStackFragmentManifest fragment) : ViewModelBase
{
    public string FragmentId { get; } = fragment.FragmentId ?? string.Empty;

    public string DisplayName { get; } = fragment.DisplayName ?? fragment.FragmentId ?? "Stack fragment";

    public string Subtitle { get; } = BuildSubtitle(fragment);

    public string Description { get; } = string.IsNullOrWhiteSpace(fragment.Description) ? "No description provided." : fragment.Description;

    [ObservableProperty]
    private bool _isSelected = fragment.DefaultSelected != false;

    private static string BuildSubtitle(SunderStackFragmentManifest fragment)
    {
        var owner = fragment.OwnerPackageId ?? "unknown package";
        var selected = fragment.DefaultSelected == false ? "off by default" : "selected by default";
        return $"{owner} · {selected}";
    }
}
