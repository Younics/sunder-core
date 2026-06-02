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
    private readonly LocalStackLibraryService _library;
    private readonly IStackArchivePicker _archivePicker;
    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _applyPackageLifecycleChangesAsync;
    private readonly Func<Uri, IRegistryApiClient> _registryClientFactory;
    private readonly Func<Uri, RegistryAuthToken?> _registryTokenProvider;
    private IReadOnlyList<LocalStackLibraryItem> _allStacks = [];
    private IReadOnlyList<SunderStackPackageRequirement> _selectedPackageRequirements = [];
    private int _selectionVersion;
    private int _registrySelectionVersion;
    private bool _selectedInstallPlanReady;
    private bool _selectedInstallPlanHasErrors;
    private bool _disposed;

    public StacksWindowViewModel(
        LocalStackLibraryService library,
        IStackArchivePicker archivePicker,
        IRuntimeApiClient runtimeApiClient,
        RegistryPackageInstallService? registryInstallService = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? applyPackageLifecycleChangesAsync = null,
        Func<Uri, IRegistryApiClient>? registryClientFactory = null,
        Func<Uri, RegistryAuthToken?>? registryTokenProvider = null)
    {
        _library = library;
        _archivePicker = archivePicker;
        _runtimeApiClient = runtimeApiClient;
        _registryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        _applyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync ?? ((_, _) => Task.CompletedTask);
        _registryClientFactory = registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl));
        _registryTokenProvider = registryTokenProvider ?? (registryUrl => RegistryAuthStore.Load().GetToken(registryUrl));
        RegistryUrlText = RegistryUrlHelper.DefaultRegistryUrl.ToString();
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

    public ObservableCollection<StackExportItemViewModel> ExportItems { get; } = [];

    public ObservableCollection<string> ExportWarnings { get; } = [];

    public ObservableCollection<string> ExportErrors { get; } = [];

    public ObservableCollection<RegistryStackSearchItemViewModel> RegistryStacks { get; } = [];

    public ObservableCollection<RegistryStackPackageRequirementViewModel> RegistrySelectedPackages { get; } = [];

    public ObservableCollection<RegistryStackFragmentViewModel> RegistrySelectedFragments { get; } = [];

    public ObservableCollection<string> RegistrySelectedRequiredInputs { get; } = [];

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
    private string _selectedStackSafetyText = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackTitle = "No Marketplace Stack selected";

    [ObservableProperty]
    private string _selectedRegistryStackSubtitle = "Search Marketplace Stacks to inspect shared setup presets.";

    [ObservableProperty]
    private string _selectedRegistryStackSummary = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackMetadata = string.Empty;

    [ObservableProperty]
    private string _selectedRegistryStackSafetyText = string.Empty;

    [ObservableProperty]
    private bool _isCreatingStack;

    [ObservableProperty]
    private string _newStackId = string.Empty;

    [ObservableProperty]
    private string _newStackName = string.Empty;

    [ObservableProperty]
    private string _newStackSummary = string.Empty;

    [ObservableProperty]
    private bool _includePrivateText = true;

    [ObservableProperty]
    private bool _includeMachineSpecificValues;

    [ObservableProperty]
    private bool _includeExecutableCommands = true;

    [ObservableProperty]
    private bool _includeNetworkEndpoints = true;

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

    public bool HasRegistrySelectedSummary => !string.IsNullOrWhiteSpace(SelectedRegistryStackSummary);

    public bool HasRegistrySelectedPackages => RegistrySelectedPackages.Count > 0;

    public bool HasRegistrySelectedFragments => RegistrySelectedFragments.Count > 0;

    public bool HasRegistrySelectedRequiredInputs => RegistrySelectedRequiredInputs.Count > 0;

    public bool HasSelectedSummary => !string.IsNullOrWhiteSpace(SelectedStackSummary);

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

    public bool HasExportItems => ExportItems.Count > 0;

    public bool HasExportWarnings => ExportWarnings.Count > 0;

    public bool HasExportErrors => ExportErrors.Count > 0;

    public bool ShowCreateStackPanel => IsCreatingStack;

    public bool ShowNoExportItems => IsCreatingStack && !HasExportItems && !HasExportErrors && !IsBusy;

    public bool ShowSelectedInstallPlan => HasSelection && _selectedPackageRequirements.Count > 0;

    public bool ShowNoSelectedInstallPlanChanges => ShowSelectedInstallPlan
                                                   && _selectedInstallPlanReady
                                                   && !HasSelectedInstallPlanItems
                                                   && !HasSelectedInstallPlanErrors;

    public bool ShowSelectedImportPreview => HasSelection && HasSelectedFragments;

    public bool ShowSelectedDetails => IsLocalMode && HasSelection && !IsCreatingStack;

    public bool ShowRegistrySelectedDetails => IsMarketplaceMode && HasRegistrySelection && !IsCreatingStack;

    public bool ShowNoSelection => IsLocalMode && !HasSelection && !IsCreatingStack;

    public bool ShowNoRegistrySelection => IsMarketplaceMode && !HasRegistrySelection && !IsCreatingStack;

    public bool CanRefresh => !IsBusy;

    public bool CanImportStack => !IsBusy;

    public bool CanSearchRegistryStacks => !IsBusy && HasRegistryUrlText;

    public bool CanImportSelectedRegistryStack => !IsBusy && SelectedRegistryStack is not null;

    public bool CanUseSelectedRegistryStack => !IsBusy && SelectedRegistryStack is not null;

    public bool CanDeleteSelectedRegistryStack => !IsBusy && SelectedRegistryStack is not null;

    public bool CanCreateStack => !IsBusy;

    public bool CanRefreshStackExportItems => !IsBusy && IsCreatingStack;

    public bool CanSaveCreatedStack => !IsBusy
                                           && IsCreatingStack
                                           && !string.IsNullOrWhiteSpace(NewStackId)
                                           && !string.IsNullOrWhiteSpace(NewStackName)
                                           && ExportItems.Any(item => item.IsSelected);

    public bool CanCancelCreateStack => !IsBusy && IsCreatingStack;

    public bool CanExportSelectedStack => !IsBusy && SelectedStack is not null;

    public bool ShowPublishSelectedStack => ShowSelectedDetails && SelectedStack?.IsPublished != true;

    public bool ShowUnpublishSelectedStack => ShowSelectedDetails && SelectedStack?.IsPublished == true;

    public bool CanPublishSelectedStack => !IsBusy && ShowPublishSelectedStack;

    public bool CanUnpublishSelectedStack => !IsBusy && ShowUnpublishSelectedStack;

    public bool CanRemoveSelectedStack => !IsBusy && SelectedStack is not null;

    public bool CanUseSelectedStack => !IsBusy && SelectedStack is not null;

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
        OnPropertyChanged(nameof(ShowNoExportItems));
        NotifyCommandStateChanged();
    }

    partial void OnIsCreatingStackChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCreateStackPanel));
        OnPropertyChanged(nameof(ShowSelectedDetails));
        OnPropertyChanged(nameof(ShowRegistrySelectedDetails));
        OnPropertyChanged(nameof(ShowNoSelection));
        OnPropertyChanged(nameof(ShowNoRegistrySelection));
        NotifyExportStateChanged();
        NotifyCommandStateChanged();
    }

    partial void OnNewStackIdChanged(string value)
    {
        NotifyExportStateChanged();
    }

    partial void OnNewStackNameChanged(string value)
    {
        NotifyExportStateChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        RebuildStackList(SelectedStack?.StackId);
    }

    partial void OnRegistrySearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRegistrySearchText));
    }

    partial void OnSelectedRegistryStackChanged(RegistryStackSearchItemViewModel? value)
    {
        foreach (var stack in RegistryStacks)
        {
            stack.IsSelected = ReferenceEquals(stack, value);
        }

        ApplySelectedRegistryStackSummary(value);
        var selectionVersion = ++_registrySelectionVersion;
        if (value is not null)
        {
            _ = LoadSelectedRegistryStackDetailsAsync(value.StackId, selectionVersion);
        }

        NotifyRegistrySelectionChanged();
        NotifyRegistryStackStateChanged();
    }

    partial void OnRegistryUrlTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRegistryUrlText));
        NotifyRegistryStackStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeSelectedLocalDetails();
        _runtimeApiClient.Dispose();
    }

    public async Task InitializeAsync()
    {
        if (_allStacks.Count == 0)
        {
            await RefreshAsync();
        }
    }

    public CreateStackWizardViewModel CreateCreateStackWizardViewModel()
        => new(_library, _runtimeApiClient);

    public UseStackWizardViewModel? CreateUseStackWizardViewModel()
        => SelectedStack is null
            ? null
            : new UseStackWizardViewModel(
                _library,
                SelectedStack.Item,
                _runtimeApiClient,
                _registryInstallService,
                _applyPackageLifecycleChangesAsync,
                _registryClientFactory,
                RegistryUrlText);

    public async Task RefreshAfterCreatedStackAsync(string? stackId)
    {
        _allStacks = await _library.ListAsync();
        IsCreatingStack = false;
        RebuildStackList(stackId);
        StatusText = string.IsNullOrWhiteSpace(stackId)
            ? "Created local Stack."
            : $"Created local Stack '{stackId}'.";
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
                await SelectLinkedStackAsync(request, cancellationToken);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
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
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void ClearRegistrySearch()
    {
        RegistrySearchText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSearchRegistryStacks))]
    private async Task SearchRegistryStacksAsync()
    {
        if (!TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        IsBusy = true;
        SelectedRegistryStack = null;
        RegistryStacks.Clear();
        NotifyRegistryStackStateChanged();
        try
        {
            StatusText = "Searching Registry Stacks...";
            using var registryClient = _registryClientFactory(registryUrl);
            var results = await registryClient.SearchStacksAsync(RegistrySearchText, 0, 50);
            foreach (var stack in results)
            {
                RegistryStacks.Add(new RegistryStackSearchItemViewModel(stack));
            }

            SelectedRegistryStack = RegistryStacks.FirstOrDefault();
            StatusText = RegistryStacks.Count == 0
                ? "No Registry Stacks matched the search."
                : $"Found {RegistryStacks.Count} Registry Stack{(RegistryStacks.Count == 1 ? string.Empty : "s")}.";
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

    [RelayCommand(CanExecute = nameof(CanCreateStack))]
    private async Task CreateStackAsync()
    {
        if (string.IsNullOrWhiteSpace(NewStackId))
        {
            NewStackId = "sunder-stack-" + DateTimeOffset.Now.ToString("yyyyMMddHHmm");
        }

        if (string.IsNullOrWhiteSpace(NewStackName))
        {
            NewStackName = "New Stack";
        }

        IsCreatingStack = true;
        await RefreshStackExportItemsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshStackExportItems))]
    private async Task RefreshStackExportItemsAsync()
    {
        IsBusy = true;
        ClearExportDiscovery();
        try
        {
            StatusText = "Discovering setup items from active packages...";
            var discovery = await _runtimeApiClient.ListStackExportItemsAsync();
            foreach (var item in discovery.Items)
            {
                ExportItems.Add(new StackExportItemViewModel(item, NotifyExportItemSelectionChanged));
            }

            foreach (var warning in discovery.Warnings)
            {
                ExportWarnings.Add(warning);
            }

            foreach (var error in discovery.Errors)
            {
                ExportErrors.Add(error);
            }

            StatusText = discovery.Errors.Count > 0
                ? discovery.Errors[0]
                : ExportItems.Count == 0
                    ? "No active packages currently expose Stack setup items."
                    : $"Discovered {ExportItems.Count} setup item{(ExportItems.Count == 1 ? string.Empty : "s")} for export.";
        }
        catch (Exception ex)
        {
            ExportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyExportStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveCreatedStack))]
    private async Task SaveCreatedStackAsync()
    {
        var selectedItems = ExportItems
            .Where(item => item.IsSelected)
            .Select(item => new RuntimeStackExportSelection(item.ContributorId, item.ItemId))
            .ToArray();
        if (selectedItems.Length == 0)
        {
            StatusText = "Select at least one setup item to export.";
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "create", Guid.NewGuid().ToString("N"), BuildStackFileName(NewStackId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        IsBusy = true;
        ExportWarnings.Clear();
        ExportErrors.Clear();
        NotifyExportStateChanged();
        try
        {
            StatusText = "Writing Stack archive...";
            var result = await _runtimeApiClient.ExportStackAsync(new RuntimeStackExportRequest(
                NewStackId.Trim(),
                NewStackName.Trim(),
                string.IsNullOrWhiteSpace(NewStackSummary) ? null : NewStackSummary.Trim(),
                path,
                selectedItems,
                new RuntimeStackExportOptions(
                    IncludePrivateText,
                    IncludeMachineSpecificValues,
                    IncludeExecutableCommands,
                    IncludeNetworkEndpoints)));

            foreach (var warning in result.Warnings)
            {
                ExportWarnings.Add(warning);
            }

            foreach (var error in result.Errors)
            {
                ExportErrors.Add(error);
            }

            if (!result.Success)
            {
                StatusText = ExportErrors.FirstOrDefault() ?? "Stack export failed.";
                return;
            }

            var imported = await _library.ImportAsync(result.StackPath ?? path);
            _allStacks = await _library.ListAsync();
            IsCreatingStack = false;
            RebuildStackList(imported.StackId);
            StatusText = $"Created local Stack '{imported.Name}'. Review it before use.";
        }
        catch (Exception ex)
        {
            ExportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            var tempDirectory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(tempDirectory))
            {
                TryDeleteDirectory(tempDirectory);
            }

            NotifyExportStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelCreateStack))]
    private void CancelCreateStack()
    {
        IsCreatingStack = false;
        ClearExportDiscovery();
        StatusText = SelectedStack is null
            ? "Import a .sunderstack file or select a local Stack."
            : $"Selected '{SelectedStack.Name}'.";
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

    private async Task SelectLinkedStackAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StackId))
        {
            return;
        }

        if (_allStacks.Count == 0)
        {
            await RefreshAsync();
        }

        RebuildStackList(request.StackId);
        if (SelectedStack is not null)
        {
            StatusText = request.Kind == AppLaunchRequestKind.StackUse
                ? $"Opened '{SelectedStack.Name}' from Stack use link. Review it before use."
                : $"Opened '{SelectedStack.Name}' from Stack link.";
            return;
        }

        var registryUrl = request.RegistryUrl ?? (TryResolveRegistryUrlForRegistryAction(out var configuredRegistryUrl) ? configuredRegistryUrl : null);
        if (registryUrl is null)
        {
            return;
        }

        await ImportRegistryStackAsync(request.StackId, registryUrl, request.Kind, cancellationToken);
    }

    private async Task<LocalStackLibraryItem?> ImportRegistryStackAsync(
        string stackId,
        Uri registryUrl,
        AppLaunchRequestKind launchKind,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "registry", Guid.NewGuid().ToString("N"));
        var tempPath = Path.Combine(tempDirectory, BuildStackFileName(stackId));
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
            SelectedStackSafetyText = string.Empty;
            NotifySelectedDetailsChanged();
            return;
        }

        SelectedStackTitle = value.Name;
        SelectedStackSubtitle = value.StackId;
        SelectedStackSummary = value.Summary;
        SelectedStackMetadata = $"{value.PackageCount} package{(value.PackageCount == 1 ? string.Empty : "s")} · {value.FragmentCount} setup fragment{(value.FragmentCount == 1 ? string.Empty : "s")} · {value.UpdatedText}";
        SelectedStackPath = value.LocalPath;
        SelectedStackSafetyText = string.Empty;
        PopulateSelectedLocalDetails(value.Item, new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase));
        var selectionVersion = _selectionVersion;
        if (value.Item.Details?.Count > 0)
        {
            _ = RefreshSelectedLocalDetailIconsAsync(value.Item, selectionVersion);
        }

        NotifySelectedDetailsChanged();
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
        SelectedStackSafetyText = string.Empty;
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

    private void ApplySelectedRegistryStackSummary(RegistryStackSearchItemViewModel? value)
    {
        RegistrySelectedPackages.Clear();
        RegistrySelectedFragments.Clear();
        RegistrySelectedRequiredInputs.Clear();

        if (value is null)
        {
            SelectedRegistryStackTitle = "No Marketplace Stack selected";
            SelectedRegistryStackSubtitle = "Search Marketplace Stacks to inspect shared setup presets.";
            SelectedRegistryStackSummary = string.Empty;
            SelectedRegistryStackMetadata = string.Empty;
            SelectedRegistryStackSafetyText = string.Empty;
            NotifyRegistrySelectedDetailsChanged();
            return;
        }

        SelectedRegistryStackTitle = value.Name;
        SelectedRegistryStackSubtitle = value.StackId;
        SelectedRegistryStackSummary = value.Summary;
        SelectedRegistryStackMetadata = $"{value.PackageCount} package{(value.PackageCount == 1 ? string.Empty : "s")} · {value.FragmentCount} setup fragment{(value.FragmentCount == 1 ? string.Empty : "s")} · {value.UpdatedText}";
        SelectedRegistryStackSafetyText = "Reading Stack safety metadata...";
        NotifyRegistrySelectedDetailsChanged();
    }

    private async Task LoadSelectedRegistryStackDetailsAsync(string stackId, int selectionVersion)
    {
        if (!TryResolveRegistryUrlForRegistryAction(out var registryUrl))
        {
            return;
        }

        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var details = await registryClient.GetStackAsync(stackId);
            if (selectionVersion != _registrySelectionVersion)
            {
                return;
            }

            if (details is null)
            {
                StatusText = $"Registry Stack '{stackId}' was not found.";
                return;
            }

            ApplySelectedRegistryStackDetails(details);
        }
        catch (Exception ex)
        {
            if (selectionVersion == _registrySelectionVersion)
            {
                StatusText = ex.Message;
            }
        }
    }

    private void ApplySelectedRegistryStackDetails(RegistryStackDetails details)
    {
        SelectedRegistryStackTitle = details.Name;
        SelectedRegistryStackSubtitle = details.StackId;
        SelectedRegistryStackSummary = string.IsNullOrWhiteSpace(details.Summary) ? string.Empty : details.Summary;
        SelectedRegistryStackMetadata = $"{details.Packages.Count} package{(details.Packages.Count == 1 ? string.Empty : "s")} · {details.Fragments.Count} setup fragment{(details.Fragments.Count == 1 ? string.Empty : "s")} · Updated {details.UpdatedAtUtc.LocalDateTime:g}";

        RegistrySelectedPackages.Clear();
        foreach (var package in details.Packages)
        {
            RegistrySelectedPackages.Add(new RegistryStackPackageRequirementViewModel(package));
        }

        RegistrySelectedFragments.Clear();
        foreach (var fragment in details.Fragments)
        {
            RegistrySelectedFragments.Add(new RegistryStackFragmentViewModel(fragment));
        }

        RegistrySelectedRequiredInputs.Clear();
        foreach (var input in details.RequiredInputs)
        {
            RegistrySelectedRequiredInputs.Add($"{input.Label} ({input.Kind})");
        }

        SelectedRegistryStackSafetyText = BuildRegistrySafetyText(details.Safety);
        NotifyRegistrySelectedDetailsChanged();
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
                    ? "No setup actions are needed for the selected fragments."
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
                ? "Stack package requirements are already installed. Setup fragment import will be enabled when package contributors are wired."
                : $"Installed {result.PlanItems.Count} Stack package change{(result.PlanItems.Count == 1 ? string.Empty : "s")}. Setup fragment import will be enabled when package contributors are wired."
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

        StatusText = result.Success
            ? $"Imported {result.ImportedItems.Count} Stack setup item{(result.ImportedItems.Count == 1 ? string.Empty : "s")}."
            : result.Errors.FirstOrDefault() ?? "Stack setup import failed.";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
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

    private static string BuildSafetyText(SunderStackManifest manifest)
    {
        var flags = new List<string>();
        AppendSafetyFlags(flags, "Stack", manifest.Safety);
        foreach (var fragment in manifest.Fragments ?? [])
        {
            AppendSafetyFlags(flags, fragment.DisplayName ?? fragment.FragmentId ?? "Fragment", fragment.Safety);
        }

        return flags.Count == 0
            ? "No sensitive content flags declared. Review package-provided fragments before import."
            : string.Join(Environment.NewLine, flags);
    }

    private static string BuildRegistrySafetyText(RegistryStackSafety safety)
    {
        var flags = new List<string>();
        AppendRegistrySafetyFlags(flags, "Stack", safety);
        return flags.Count == 0
            ? "No sensitive content flags declared. Review package-provided fragments before use."
            : string.Join(Environment.NewLine, flags);
    }

    private static void AppendRegistrySafetyFlags(ICollection<string> flags, string label, RegistryStackSafety safety)
    {
        if (safety.ContainsSecrets)
        {
            flags.Add($"{label}: declares raw secrets.");
        }

        if (safety.ContainsSecretReferences)
        {
            flags.Add($"{label}: references secrets that must be supplied locally.");
        }

        if (safety.ContainsLocalPaths)
        {
            flags.Add($"{label}: contains local file paths.");
        }

        if (safety.ContainsPrivateText)
        {
            flags.Add($"{label}: contains private text.");
        }

        if (safety.ContainsExecutableCommands)
        {
            flags.Add($"{label}: contains executable commands.");
        }

        if (safety.ContainsNetworkEndpoints)
        {
            flags.Add($"{label}: contains network endpoints.");
        }

        if (safety.ContainsMachineSpecificValues)
        {
            flags.Add($"{label}: contains machine-specific values.");
        }
    }

    private static void AppendSafetyFlags(ICollection<string> flags, string label, SunderStackSafetyManifest? safety)
    {
        if (safety is null)
        {
            return;
        }

        if (safety.ContainsSecrets == true)
        {
            flags.Add($"{label}: declares raw secrets.");
        }

        if (safety.ContainsSecretReferences == true)
        {
            flags.Add($"{label}: references secrets that must be supplied locally.");
        }

        if (safety.ContainsLocalPaths == true)
        {
            flags.Add($"{label}: contains local file paths.");
        }

        if (safety.ContainsPrivateText == true)
        {
            flags.Add($"{label}: contains private text.");
        }

        if (safety.ContainsExecutableCommands == true)
        {
            flags.Add($"{label}: contains executable commands.");
        }

        if (safety.ContainsNetworkEndpoints == true)
        {
            flags.Add($"{label}: contains network endpoints.");
        }

        if (safety.ContainsMachineSpecificValues == true)
        {
            flags.Add($"{label}: contains machine-specific values.");
        }
    }

    private static bool MatchesSearch(LocalStackLibraryItem item, string query)
        => string.IsNullOrWhiteSpace(query)
           || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || item.StackId.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (item.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private static string BuildStackFileName(string stackId)
    {
        var fileName = string.IsNullOrWhiteSpace(stackId)
            ? "sunder-stack"
            : string.Concat(stackId.Trim().Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? char.ToLowerInvariant(character) : '-'));
        return fileName.EndsWith(".sunderstack", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".sunderstack";
    }

    private void ClearExportDiscovery()
    {
        ExportItems.Clear();
        ExportWarnings.Clear();
        ExportErrors.Clear();
        NotifyExportStateChanged();
    }

    private void NotifyRegistryStackStateChanged()
    {
        OnPropertyChanged(nameof(HasRegistryStacks));
        OnPropertyChanged(nameof(ShowNoRegistryStacks));
        OnPropertyChanged(nameof(CanSearchRegistryStacks));
        OnPropertyChanged(nameof(CanImportSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedRegistryStack));
        OnPropertyChanged(nameof(CanDeleteSelectedRegistryStack));
        SearchRegistryStacksCommand.NotifyCanExecuteChanged();
        ImportSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        DeleteSelectedRegistryStackCommand.NotifyCanExecuteChanged();
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
        NotifyRegistrySelectedDetailsChanged();
    }

    private void NotifyRegistrySelectedDetailsChanged()
    {
        OnPropertyChanged(nameof(HasRegistrySelectedSummary));
        OnPropertyChanged(nameof(HasRegistrySelectedPackages));
        OnPropertyChanged(nameof(HasRegistrySelectedFragments));
        OnPropertyChanged(nameof(HasRegistrySelectedRequiredInputs));
    }

    private void NotifyExportItemSelectionChanged()
    {
        NotifyExportStateChanged();
    }

    private void NotifyExportStateChanged()
    {
        OnPropertyChanged(nameof(HasExportItems));
        OnPropertyChanged(nameof(HasExportWarnings));
        OnPropertyChanged(nameof(HasExportErrors));
        OnPropertyChanged(nameof(ShowNoExportItems));
        OnPropertyChanged(nameof(CanSaveCreatedStack));
        SaveCreatedStackCommand.NotifyCanExecuteChanged();
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

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowSelectedDetails));
        OnPropertyChanged(nameof(ShowPublishSelectedStack));
        OnPropertyChanged(nameof(ShowUnpublishSelectedStack));
        OnPropertyChanged(nameof(ShowNoSelection));
        NotifyCommandStateChanged();
    }

    private void NotifySelectedDetailsChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSummary));
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
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanImportStack));
        OnPropertyChanged(nameof(CanSearchRegistryStacks));
        OnPropertyChanged(nameof(CanImportSelectedRegistryStack));
        OnPropertyChanged(nameof(CanUseSelectedRegistryStack));
        OnPropertyChanged(nameof(CanDeleteSelectedRegistryStack));
        OnPropertyChanged(nameof(CanCreateStack));
        OnPropertyChanged(nameof(CanRefreshStackExportItems));
        OnPropertyChanged(nameof(CanSaveCreatedStack));
        OnPropertyChanged(nameof(CanCancelCreateStack));
        OnPropertyChanged(nameof(CanExportSelectedStack));
        OnPropertyChanged(nameof(CanPublishSelectedStack));
        OnPropertyChanged(nameof(CanUnpublishSelectedStack));
        OnPropertyChanged(nameof(CanRemoveSelectedStack));
        OnPropertyChanged(nameof(CanUseSelectedStack));
        RefreshCommand.NotifyCanExecuteChanged();
        ImportStackCommand.NotifyCanExecuteChanged();
        SearchRegistryStacksCommand.NotifyCanExecuteChanged();
        ImportSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        DeleteSelectedRegistryStackCommand.NotifyCanExecuteChanged();
        CreateStackCommand.NotifyCanExecuteChanged();
        RefreshStackExportItemsCommand.NotifyCanExecuteChanged();
        SaveCreatedStackCommand.NotifyCanExecuteChanged();
        CancelCreateStackCommand.NotifyCanExecuteChanged();
        ExportSelectedStackCommand.NotifyCanExecuteChanged();
        PublishSelectedStackCommand.NotifyCanExecuteChanged();
        UnpublishSelectedStackCommand.NotifyCanExecuteChanged();
        RemoveSelectedStackCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<string> GetSelectedFragmentIds()
        => SelectedFragments
            .Where(fragment => fragment.IsSelected)
            .Select(fragment => fragment.FragmentId)
            .ToArray();

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

public sealed class RegistryStackFragmentViewModel(RegistryStackFragmentSummary fragment)
{
    public string FragmentId { get; } = fragment.FragmentId;

    public string DisplayName { get; } = fragment.DisplayName;

    public string Subtitle { get; } = BuildSubtitle(fragment);

    public string Description { get; } = string.IsNullOrWhiteSpace(fragment.Description) ? "No description provided." : fragment.Description;

    private static string BuildSubtitle(RegistryStackFragmentSummary fragment)
    {
        var selected = fragment.DefaultSelected ? "selected by default" : "off by default";
        return $"{fragment.OwnerPackageId} · {fragment.SchemaId} v{fragment.SchemaVersion} · {selected}";
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

    public string ValuePreview { get; } = ShortenSingleLine(value.Value, 180);

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

    private static string ShortenSingleLine(string value, int maxLength)
    {
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(4, maxLength - 3)] + "...";
    }
}

public sealed class StackPackageRequirementViewModel(SunderStackPackageRequirement package)
{
    public string PackageId { get; } = package.PackageId ?? "unknown";

    public string InstallText { get; } = BuildInstallText(package);

    private static string BuildInstallText(SunderStackPackageRequirement package)
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

        parts.Add(package.Required == true ? "required" : "optional");
        return string.Join(" · ", parts);
    }
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

    public string Kind { get; } = input.Kind;

    public string ContributorId { get; } = input.ContributorId;

    public bool Required { get; } = input.Required;

    public bool IsSecret { get; } = string.Equals(input.Kind, "Secret", StringComparison.OrdinalIgnoreCase);

    public bool IsPlainText => !IsSecret;

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

public sealed partial class StackExportItemViewModel(RuntimeStackExportItemDescriptor item, Action selectionChanged) : ViewModelBase
{
    public string ContributorId { get; } = item.ContributorId;

    public string OwnerPackageId { get; } = item.OwnerPackageId;

    public string ItemId { get; } = item.ItemId;

    public string DisplayName { get; } = item.DisplayName;

    public string Subtitle { get; } = BuildSubtitle(item);

    public string Description { get; } = string.IsNullOrWhiteSpace(item.Description) ? "No description provided." : item.Description;

    [ObservableProperty]
    private bool _isSelected = item.DefaultSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        selectionChanged();
    }

    private static string BuildSubtitle(RuntimeStackExportItemDescriptor item)
    {
        var sensitivity = item.Sensitivities.Count == 0 ? "public" : string.Join(", ", item.Sensitivities);
        return $"{item.OwnerPackageId} · {item.Kind} · {sensitivity}";
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
