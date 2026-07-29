using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IRuntimePackageSettingsClient _runtimeApiClient;
    private readonly SettingsCliViewModel _cli;
    private readonly SettingsUpdateViewModel _updates;
    private readonly SettingsPackageSectionsViewModel _packageSettings;
    private readonly SettingsPackageSectionRefreshCoordinator _packageSectionRefresh;
    private readonly PackageViewHostService _packageViewHostService;
    private readonly SettingsSectionSelectionState _selection = new();
    private readonly LatestAsyncRequest _selectionLoadRequest = new();
    private readonly OwnedTaskObserver _tasks = new(nameof(SettingsWindowViewModel));
    private readonly CancellationTokenSource _disposeCts = new();
    private SettingsSectionItemViewModel? _presentedSection;
    private bool _disposed;

    public SettingsWindowViewModel(
        IRuntimePackageSettingsClient runtimeApiClient,
        PackageViewHostService packageViewHostService,
        CliInstallationService cliInstallationService,
        SunderUpdateService? updateService = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        double backgroundProcessPopoverWidth = ShellState.DefaultBackgroundProcessPopoverWidth,
        double backgroundProcessPopoverHeight = ShellState.DefaultBackgroundProcessPopoverHeight,
        Action<double, double>? persistBackgroundProcessPopoverSize = null)
    {
        _runtimeApiClient = runtimeApiClient;
        _packageViewHostService = packageViewHostService;
        var resolvedUpdateService = updateService ?? new SunderUpdateService();
        _cli = new SettingsCliViewModel(new SettingsCliCoordinator(cliInstallationService));
        _updates = new SettingsUpdateViewModel(new SettingsUpdateCoordinator(resolvedUpdateService));
        _packageSettings = new SettingsPackageSectionsViewModel(
            new SettingsPackageSectionLoader(runtimeApiClient, packageViewHostService),
            new SettingsPackageSelectionCoordinator(runtimeApiClient, packageViewHostService));
        _packageSectionRefresh = new SettingsPackageSectionRefreshCoordinator(
            _packageSettings,
            () => _disposed,
            () => _selection.SelectedSection?.PackageId,
            PreservePackageSelectionAfterRefresh,
            () => OnPropertyChanged(nameof(PackageSettings)),
            value => IsBusy = value,
            value => StatusText = value);
        BackgroundProcesses = backgroundProcessQueue is null
            ? BackgroundProcessMonitorViewModel.Empty
            : new BackgroundProcessMonitorViewModel(
                backgroundProcessQueue,
                BackgroundProcessIndicator.Settings,
                "No background processes.",
                backgroundProcessPopoverWidth,
                backgroundProcessPopoverHeight,
                persistBackgroundProcessPopoverSize);

        CoreSections =
        [
            new("appearance", "Appearance", "Theme, startup behavior, and shell presentation.", false),
            new("runtime", "Runtime", "Shell composition, package loading, and local runtime behavior.", false),
            new("cli", "CLI", "Terminal command installation and shell profile instructions.", false),
            new("updates", "Updates", "Package updates and future managed rollout behavior.", false),
            new("notifications", "Notifications", "Notification preferences and attention management.", false),
            new("privacy", "Privacy", "Local-first data handling and package trust decisions.", false),
            new("advanced", "Advanced", "Diagnostic and power-user controls.", false),
        ];

        SelectedCoreLines = [];

        _selection.PreserveSelection(CoreSections[0]);
        ApplyCoreSelection(CoreSections[0]);
        _tasks.Observe(
            _packageSectionRefresh.RefreshAsync(preserveSelection: false, _disposeCts.Token),
            "loading package settings sections");
    }

    public ObservableCollection<SettingsSectionItemViewModel> CoreSections { get; }

    public SettingsCliViewModel Cli => _cli;

    public SettingsUpdateViewModel Updates => _updates;

    public SettingsPackageSectionsViewModel PackageSettings => _packageSettings;

    public ObservableCollection<string> SelectedCoreLines { get; }

    public BackgroundProcessMonitorViewModel BackgroundProcesses { get; }

    public bool IsCoreSelection => !IsPackageSelection;

    public bool ShowPlainCoreSelection => IsCoreSelection && !IsCliSelection && !IsUpdatesSelection;

    public bool ShowCliSettings => IsCliSelection;

    public bool ShowUpdateSettings => IsUpdatesSelection;

    public bool HasHostedSettingsView => HostedSettingsView is not null;

    public bool HasStagedHostedSettingsView => StagedHostedSettingsView is not null;

    public bool ShowGenericPackageSettings => IsPackageSelection && !HasHostedSettingsView;

    public bool ShowScrollableSelectionContent => !HasHostedSettingsView;

    public bool ShowApplySaveButtons => !HasHostedSettingsView && !IsCliSelection;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _selectedTitle = "Appearance";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _selectedDescription = "Theme, startup behavior, and shell presentation.";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _detailsTitle = "Selection Details";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _detailsText = "Use the settings categories to inspect host-level and package-level configuration.";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isPackageSelection;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isCliSelection;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isUpdatesSelection;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isBusy;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _statusText = string.Empty;

    private object? _hostedSettingsView;

    public object? HostedSettingsView
    {
        get => _hostedSettingsView;
        private set
        {
            if (ReferenceEquals(_hostedSettingsView, value))
            {
                return;
            }

            HostedPackageViewBoundary.ReleaseHostedView(_hostedSettingsView);
            if (!SetProperty(ref _hostedSettingsView, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasHostedSettingsView));
            OnPropertyChanged(nameof(ShowScrollableSelectionContent));
            OnPropertyChanged(nameof(ShowGenericPackageSettings));
            OnPropertyChanged(nameof(ShowApplySaveButtons));
        }
    }

    private object? _stagedHostedSettingsView;

    public object? StagedHostedSettingsView
    {
        get => _stagedHostedSettingsView;
        private set
        {
            if (SetProperty(ref _stagedHostedSettingsView, value))
            {
                OnPropertyChanged(nameof(HasStagedHostedSettingsView));
            }
        }
    }

    partial void OnIsPackageSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCoreSelection));
        OnPropertyChanged(nameof(ShowPlainCoreSelection));
        OnPropertyChanged(nameof(ShowGenericPackageSettings));
        OnPropertyChanged(nameof(ShowApplySaveButtons));
    }

    partial void OnIsCliSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlainCoreSelection));
        OnPropertyChanged(nameof(ShowCliSettings));
        OnPropertyChanged(nameof(ShowApplySaveButtons));
    }

    partial void OnIsUpdatesSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlainCoreSelection));
        OnPropertyChanged(nameof(ShowUpdateSettings));
    }

    public async Task<bool> ApplyAsync()
    {
        if (_disposed)
        {
            return false;
        }

        if (IsUpdatesSelection)
        {
            return await SaveUpdateSettingsAsync();
        }

        var selectedSection = _selection.SelectedSection;
        if (!ShowGenericPackageSettings || selectedSection?.PackageId is null)
        {
            StatusText = "Nothing to apply for the selected section yet.";
            return true;
        }

        var packageId = selectedSection.PackageId;
        var selectedTitle = SelectedTitle;
        var selectionVersion = _selection.Version;
        var cancellationToken = _disposeCts.Token;
        IsBusy = true;
        try
        {
            var values = PackageConfigurationFormSerializer.Serialize(PackageSettings.SelectedPackageSections);
            await _runtimeApiClient.SavePackageSettingsValuesAsync(packageId, values, cancellationToken);
            if (IsCurrentSelection(selectedSection, selectionVersion))
            {
                StatusText = $"Applied settings for {selectedTitle}.";
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsCurrentSelection(selectedSection, selectionVersion))
            {
                StatusText = ex.Message;
            }

            return false;
        }
        finally
        {
            if (IsCurrentSelection(selectedSection, selectionVersion))
            {
                IsBusy = false;
            }
        }
    }

    public async Task<bool> SaveAsync()
    {
        return await ApplyAsync();
    }

    [RelayCommand]
    private async Task ApplyFromUiAsync()
        => await ApplyAsync();

    [RelayCommand]
    private async Task InstallOrRepairCliFromUiAsync()
        => await InstallOrRepairCliAsync();

    [RelayCommand]
    private async Task RefreshCliStatusFromUiAsync()
        => await RefreshCliStatusAsync();

    [RelayCommand]
    private async Task UninstallCliFromUiAsync()
        => await UninstallCliAsync();

    [RelayCommand]
    private async Task CheckForUpdatesFromUiAsync()
        => await CheckForAppUpdatesAsync();

    public async Task<bool> SelectPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        cancellationToken = lifetimeCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var parameterSnapshot = SnapshotNavigationParameters(parameters);

        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        await _packageSectionRefresh.CurrentLoadTask.WaitAsync(cancellationToken);
        var item = _packageSettings.FindSection(packageId);
        if (item is null)
        {
            await _packageSectionRefresh.RefreshAsync(preserveSelection: true, cancellationToken).WaitAsync(cancellationToken);
            item = _packageSettings.FindSection(packageId);
        }

        if (item is null)
        {
            StatusText = $"Package settings were not found for '{packageId}'.";
            return false;
        }

        return await SelectSectionCoreAsync(item, parameterSnapshot, cancellationToken);
    }

    public async Task RefreshPackageSectionsAsync(CancellationToken cancellationToken = default)
    {
        using var refreshCts = CreateLifetimeCancellationTokenSource(cancellationToken, out var refreshToken);
        var applied = await _packageSectionRefresh.RefreshAsync(preserveSelection: true, refreshToken).WaitAsync(cancellationToken);
        if (!applied)
        {
            return;
        }
        refreshToken.ThrowIfCancellationRequested();
        var selectedSection = _selection.SelectedSection;
        if (selectedSection?.PackageId is not null
            && HostedSettingsView is null
            && _packageViewHostService.HasSettingsView(selectedSection.PackageId))
        {
            await ApplyPackageSelectionAsync(
                selectedSection,
                _selection.Version,
                selectedSection,
                new Dictionary<string, string?>(),
                refreshToken);
        }
    }

    internal void DetachHostedPackageSettingsView()
    {
        _selectionLoadRequest.Invalidate();
        ReleaseStagedHostedSettingsView();
        HostedSettingsView = null;
    }

    public async Task RefreshCliStatusAsync(bool showSuccessStatus = true)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var statusText = await _cli.RefreshStatusAsync(showSuccessStatus);
            if (!_disposed && !string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    public async Task InstallOrRepairCliAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var statusText = await _cli.InstallOrRepairAsync();
            if (!_disposed && !string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    public async Task UninstallCliAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var statusText = await _cli.UninstallAsync();
            if (!_disposed && !string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    public void MarkCliPathInstructionsCopied()
    {
        StatusText = "CLI PATH instructions copied.";
    }

    public async Task CheckForAppUpdatesAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var statusText = await _updates.CheckForUpdatesAsync();
            if (!_disposed && !string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _selection.Dispose();
        _selectionLoadRequest.Dispose();
        _packageSectionRefresh.Dispose();
        _disposeCts.Cancel();
        _tasks.Dispose();
        ReleaseStagedHostedSettingsView();
        HostedSettingsView = null;
        if (!ReferenceEquals(BackgroundProcesses, BackgroundProcessMonitorViewModel.Empty))
        {
            BackgroundProcesses.Dispose();
        }

        _runtimeApiClient.Dispose();
        _disposeCts.Dispose();
    }

    private void PreservePackageSelectionAfterRefresh(string? selectedPackageId)
    {
        if (string.IsNullOrWhiteSpace(selectedPackageId))
        {
            return;
        }

        var refreshedSelection = _packageSettings.FindSection(selectedPackageId);
        if (refreshedSelection is null)
        {
            HostedSettingsView = null;
            _selection.PreserveSelection(CoreSections[0]);
            ApplyCoreSelection(CoreSections[0]);
            return;
        }

        _selection.PreserveSelection(refreshedSelection);
        _presentedSection = refreshedSelection;

        _packageSettings.TryGetSchema(selectedPackageId, out var schema);
        ApplyPackageSelectionHeader(refreshedSelection, schema);
    }

    private void ApplyCoreSelection(SettingsSectionItemViewModel item)
    {
        _presentedSection = item;
        IsBusy = false;
        ReleaseStagedHostedSettingsView();
        HostedSettingsView = null;
        IsPackageSelection = false;
        IsCliSelection = string.Equals(item.Id, "cli", StringComparison.OrdinalIgnoreCase);
        IsUpdatesSelection = string.Equals(item.Id, "updates", StringComparison.OrdinalIgnoreCase);
        SelectedTitle = item.Title;
        SelectedDescription = item.Description;
        DetailsTitle = item.Title;
        DetailsText = item.Description;
        StatusText = string.Empty;

        PackageSettings.SelectedPackageSections.Clear();
        SelectedCoreLines.Clear();

        if (IsUpdatesSelection)
        {
            ApplyUpdateSettings();
            return;
        }

        if (!IsCliSelection)
        {
            foreach (var line in CoreSettingsContentProvider.GetLines(item.Id))
            {
                SelectedCoreLines.Add(line);
            }
        }
    }

    private async Task<bool> ApplyPackageSelectionAsync(
        SettingsSectionItemViewModel item,
        int selectionVersion,
        SettingsSectionItemViewModel? previousSelection,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken)
    {
        if (item.PackageId is null)
        {
            ApplyCoreSelection(CoreSections[0]);
            return false;
        }

        using var request = _selectionLoadRequest.Start(cancellationToken);
        IsBusy = true;
        HostedPackageViewBoundary? candidateBoundary = null;
        HostedPackageSettingsSelection? hostedSelection = null;
        var committed = false;
        try
        {
            var hasSchema = _packageSettings.TryGetSchema(item.PackageId, out var schema);

            var result = await _packageSettings.LoadSelectionAsync(
                item.PackageId,
                hasSchema ? schema : null,
                IsHostedSettingsViewInUse(item.PackageId),
                request.Token);
            if (!request.IsCurrent || !IsCurrentSelection(item, selectionVersion))
            {
                return false;
            }

            if (result is HostedPackageSettingsSelection hosted)
            {
                hostedSelection = hosted;
                candidateBoundary = hosted.View as HostedPackageViewBoundary
                    ?? throw new InvalidOperationException("The package settings view host is invalid.");
                ReleaseStagedHostedSettingsView();
                StagedHostedSettingsView = candidateBoundary;

                var context = new PackageViewNavigationContext(
                    $"settings:{item.PackageId}",
                    parameters);
                if (!await candidateBoundary.PrepareNavigationAsync(context, request.Token)
                    || candidateBoundary.IsFaulted)
                {
                    throw new InvalidOperationException(
                        candidateBoundary.FaultMessage ?? "The package settings view rejected navigation.");
                }
                if (!request.IsCurrent || !IsCurrentSelection(item, selectionVersion))
                {
                    return false;
                }
                if (!hosted.PromoteCandidate())
                {
                    throw new InvalidOperationException("The package settings view changed while navigation was being prepared.");
                }

                CommitPackageSelection(item, schema);
                StagedHostedSettingsView = null;
                HostedSettingsView = candidateBoundary;
                candidateBoundary = null;
                _packageSettings.ApplySelectionResult(result);
                StatusText = result.StatusText;
                committed = true;
                await ((HostedPackageViewBoundary)HostedSettingsView!).OnNavigationPresentedAsync(
                    context,
                    request.Token);
                if (HostedSettingsView is HostedPackageViewBoundary { IsFaulted: true } presentedBoundary)
                {
                    throw new InvalidOperationException(
                        presentedBoundary.FaultMessage ?? "The package settings view rejected presentation.");
                }
            }
            else
            {
                CommitPackageSelection(item, schema);
                ReleaseStagedHostedSettingsView();
                HostedSettingsView = null;
                _packageSettings.ApplySelectionResult(result);
                StatusText = result.StatusText;
                committed = true;
            }

            return request.IsCurrent && IsCurrentSelection(item, selectionVersion);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            if (request.IsCurrent && IsCurrentSelection(item, selectionVersion))
            {
                RestoreSelection(previousSelection);
            }
            return false;
        }
        catch (Exception ex)
        {
            if (request.IsCurrent && IsCurrentSelection(item, selectionVersion))
            {
                if (committed)
                {
                    _selection.PreserveSelection(CoreSections[0]);
                    ApplyCoreSelection(CoreSections[0]);
                }
                else
                {
                    RestoreSelection(previousSelection);
                }
                StatusText = $"Could not open package settings: {ex.Message}";
            }
            return false;
        }
        finally
        {
            if (candidateBoundary is not null)
            {
                if (ReferenceEquals(StagedHostedSettingsView, candidateBoundary))
                {
                    StagedHostedSettingsView = null;
                }
                if (!ReferenceEquals(HostedSettingsView, candidateBoundary))
                {
                    candidateBoundary.Release();
                }
            }
            hostedSelection?.Dispose();
            if (request.IsCurrent && IsCurrentSelection(item, selectionVersion))
            {
                IsBusy = false;
            }
        }
    }

    private void CommitPackageSelection(
        SettingsSectionItemViewModel item,
        PackageSettingsSchemaDescriptor? schema)
    {
        _presentedSection = item;
        ApplyPackageSelectionHeader(item, schema);
        SelectedCoreLines.Clear();
        _packageSettings.ClearSelectedSections();
    }

    private void RestoreSelection(SettingsSectionItemViewModel? previousSelection)
    {
        if (previousSelection is not null)
        {
            _selection.PreserveSelection(previousSelection);
        }
        IsBusy = false;
    }

    private void ApplyPackageSelectionHeader(
        SettingsSectionItemViewModel item,
        PackageSettingsSchemaDescriptor? schema)
    {
        SelectedTitle = schema?.PackageDisplayName ?? item.Title;
        SelectedDescription = schema?.Summary ?? item.Description;
        DetailsTitle = schema?.PackageDisplayName ?? item.Title;
        DetailsText = $"Package id: {item.PackageId}";
        IsCliSelection = false;
        IsUpdatesSelection = false;
        IsPackageSelection = true;
    }

    private CancellationTokenSource? CreateLifetimeCancellationTokenSource(
        CancellationToken cancellationToken,
        out CancellationToken lifetimeToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            lifetimeToken = _disposeCts.Token;
            return null;
        }

        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        lifetimeToken = cancellationTokenSource.Token;
        return cancellationTokenSource;
    }

    private bool IsCurrentSelection(SettingsSectionItemViewModel item, int? selectionVersion = null)
        => !_disposed && _selection.IsCurrent(item, selectionVersion);

    private void ReleaseStagedHostedSettingsView()
    {
        var staged = StagedHostedSettingsView;
        StagedHostedSettingsView = null;
        if (!ReferenceEquals(staged, HostedSettingsView))
        {
            HostedPackageViewBoundary.ReleaseHostedView(staged);
        }
    }

    private bool IsHostedSettingsViewInUse(string packageId)
        => HostedSettingsView is HostedPackageViewBoundary presented
               && string.Equals(presented.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
           || StagedHostedSettingsView is HostedPackageViewBoundary staged
               && string.Equals(staged.PackageId, packageId, StringComparison.OrdinalIgnoreCase);

}
