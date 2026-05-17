using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly SettingsCliViewModel _cli;
    private readonly SettingsUpdateViewModel _updates;
    private readonly SettingsPackageSectionsViewModel _packageSettings;
    private readonly SettingsPackageSectionRefreshCoordinator _packageSectionRefresh;
    private readonly SettingsSectionSelectionState _selection = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public SettingsWindowViewModel(
        IRuntimeApiClient runtimeApiClient,
        PackageViewHostService packageViewHostService,
        CliInstallationService cliInstallationService,
        SunderUpdateService? updateService = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        double backgroundProcessPopoverWidth = ShellState.DefaultBackgroundProcessPopoverWidth,
        double backgroundProcessPopoverHeight = ShellState.DefaultBackgroundProcessPopoverHeight,
        Action<double, double>? persistBackgroundProcessPopoverSize = null)
    {
        _runtimeApiClient = runtimeApiClient;
        var resolvedUpdateService = updateService ?? new SunderUpdateService();
        _cli = new SettingsCliViewModel(new SettingsCliCoordinator(cliInstallationService));
        _cli.PropertyChanged += Cli_OnPropertyChanged;
        _updates = new SettingsUpdateViewModel(new SettingsUpdateCoordinator(resolvedUpdateService));
        _updates.PropertyChanged += Updates_OnPropertyChanged;
        _packageSettings = new SettingsPackageSectionsViewModel(
            new SettingsPackageSectionLoader(runtimeApiClient, packageViewHostService),
            new SettingsPackageSelectionCoordinator(runtimeApiClient, packageViewHostService));
        _packageSectionRefresh = new SettingsPackageSectionRefreshCoordinator(
            _packageSettings,
            () => _disposed,
            () => _selection.SelectedSection?.PackageId,
            PreservePackageSelectionAfterRefresh,
            () => OnPropertyChanged(nameof(HasPackageSections)),
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
        _packageSectionRefresh.RefreshAsync(preserveSelection: false, _disposeCts.Token);
    }

    public ObservableCollection<SettingsSectionItemViewModel> CoreSections { get; }

    public ObservableCollection<SettingsSectionItemViewModel> PackageSections => _packageSettings.PackageSections;

    public ObservableCollection<string> SelectedCoreLines { get; }

    public ObservableCollection<SettingsFieldSectionViewModel> SelectedPackageSections => _packageSettings.SelectedPackageSections;

    public BackgroundProcessMonitorViewModel BackgroundProcesses { get; }

    public bool HasPackageSections => _packageSettings.HasPackageSections;

    public bool IsCoreSelection => !IsPackageSelection;

    public bool ShowPlainCoreSelection => IsCoreSelection && !IsCliSelection && !IsUpdatesSelection;

    public bool ShowCliSettings => IsCliSelection;

    public bool ShowUpdateSettings => IsUpdatesSelection;

    public bool HasHostedSettingsView => HostedSettingsView is not null;

    public bool ShowGenericPackageSettings => IsPackageSelection && !HasHostedSettingsView;

    public bool ShowScrollableSelectionContent => !HasHostedSettingsView;

    public bool ShowApplySaveButtons => !HasHostedSettingsView && !IsCliSelection;

    public bool HasCliWarning => _cli.HasWarning;

    public bool HasCliPathInstructions => _cli.HasPathInstructions;

    public string CliStatusText => _cli.StatusText;

    public string CliStatusDescription => _cli.StatusDescription;

    public string CliPlatformText => _cli.PlatformText;

    public string CliBundledPath => _cli.BundledPath;

    public string CliInstalledPath => _cli.InstalledPath;

    public string CliShimPath => _cli.ShimPath;

    public string CliWarningText => _cli.WarningText;

    public string CliPathInstructions => _cli.PathInstructions;

    public bool CanInstallOrRepairCli => _cli.CanInstallOrRepair;

    public bool CanUninstallCli => _cli.CanUninstall;

    public bool DownloadUpdatesAutomatically
    {
        get => _updates.DownloadUpdatesAutomatically;
        set => _updates.DownloadUpdatesAutomatically = value;
    }

    public string UpdateCurrentVersionText => _updates.CurrentVersionText;

    public string UpdateSourceText => _updates.SourceText;

    public string UpdateStatusText => _updates.StatusText;

    public bool CanCheckForAppUpdates => _updates.CanCheckForAppUpdates;

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

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private object? _hostedSettingsView;

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

    partial void OnHostedSettingsViewChanged(object? value)
    {
        OnPropertyChanged(nameof(HasHostedSettingsView));
        OnPropertyChanged(nameof(ShowScrollableSelectionContent));
        OnPropertyChanged(nameof(ShowGenericPackageSettings));
        OnPropertyChanged(nameof(ShowApplySaveButtons));
    }

    public async Task SelectSectionAsync(SettingsSectionItemViewModel item)
    {
        if (_disposed)
        {
            return;
        }

        var selectionVersion = _selection.Select(item);

        if (item.IsPackage)
        {
            await ApplyPackageSelectionAsync(item, selectionVersion);
            return;
        }

        ApplyCoreSelection(item);
        if (IsCliSelection)
        {
            await RefreshCliStatusAsync(showSuccessStatus: false);
        }
    }

    public async Task<bool> ApplyAsync()
    {
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
        IsBusy = true;
        try
        {
            var values = PackageConfigurationFormSerializer.Serialize(SelectedPackageSections);
            await _runtimeApiClient.SavePackageConfigurationValuesAsync(packageId, values);
            if (IsCurrentSelection(selectedSection, selectionVersion))
            {
                StatusText = $"Applied settings for {selectedTitle}.";
                return true;
            }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        await _packageSectionRefresh.CurrentLoadTask.WaitAsync(cancellationToken);
        var item = _packageSettings.FindSection(packageId);
        if (item is null)
        {
            using var reloadCts = CreateLifetimeCancellationTokenSource(cancellationToken, out var reloadToken);
            await _packageSectionRefresh.RefreshAsync(preserveSelection: true, reloadToken).WaitAsync(cancellationToken);
            item = _packageSettings.FindSection(packageId);
        }

        if (item is null)
        {
            StatusText = $"Package settings were not found for '{packageId}'.";
            return false;
        }

        await SelectSectionAsync(item);
        if (!IsCurrentSelection(item))
        {
            return false;
        }

        await NotifyHostedSettingsNavigatedAsync(packageId, parameters ?? new Dictionary<string, string?>(), cancellationToken);
        return true;
    }

    public async Task RefreshPackageSectionsAsync(CancellationToken cancellationToken = default)
    {
        using var refreshCts = CreateLifetimeCancellationTokenSource(cancellationToken, out var refreshToken);
        await _packageSectionRefresh.RefreshAsync(preserveSelection: true, refreshToken).WaitAsync(cancellationToken);
    }

    public async Task RefreshCliStatusAsync(bool showSuccessStatus = true)
    {
        IsBusy = true;
        try
        {
            var statusText = await _cli.RefreshStatusAsync(showSuccessStatus);
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InstallOrRepairCliAsync()
    {
        IsBusy = true;
        try
        {
            var statusText = await _cli.InstallOrRepairAsync();
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UninstallCliAsync()
    {
        IsBusy = true;
        try
        {
            var statusText = await _cli.UninstallAsync();
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MarkCliPathInstructionsCopied()
    {
        StatusText = "CLI PATH instructions copied.";
    }

    public async Task CheckForAppUpdatesAsync()
    {
        IsBusy = true;
        try
        {
            var statusText = await _updates.CheckForUpdatesAsync();
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                StatusText = statusText;
            }
        }
        finally
        {
            IsBusy = false;
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
        _packageSectionRefresh.Invalidate();
        _disposeCts.Cancel();
        _cli.PropertyChanged -= Cli_OnPropertyChanged;
        _updates.PropertyChanged -= Updates_OnPropertyChanged;
        if (!ReferenceEquals(BackgroundProcesses, BackgroundProcessMonitorViewModel.Empty))
        {
            BackgroundProcesses.Dispose();
        }

        _runtimeApiClient.Dispose();
    }

    private async Task NotifyHostedSettingsNavigatedAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken)
    {
        if (HostedSettingsView is null)
        {
            return;
        }

        var context = new PackageViewNavigationContext(packageId, parameters);
        if (HostedSettingsView is IPackageViewNavigationTarget viewTarget)
        {
            await viewTarget.OnNavigatedToAsync(context, cancellationToken);
        }

        if (HostedSettingsView is Avalonia.Controls.Control { DataContext: IPackageViewNavigationTarget dataContextTarget })
        {
            await dataContextTarget.OnNavigatedToAsync(context, cancellationToken);
        }
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
            _selection.PreserveSelection(CoreSections[0]);
            ApplyCoreSelection(CoreSections[0]);
            return;
        }

        _selection.PreserveSelection(refreshedSelection);

        _packageSettings.TryGetSchema(selectedPackageId, out var schema);
        ApplyPackageSelectionHeader(refreshedSelection, schema);
    }

    private void ApplyCoreSelection(SettingsSectionItemViewModel item)
    {
        IsBusy = false;
        HostedSettingsView = null;
        IsPackageSelection = false;
        IsCliSelection = string.Equals(item.Id, "cli", StringComparison.OrdinalIgnoreCase);
        IsUpdatesSelection = string.Equals(item.Id, "updates", StringComparison.OrdinalIgnoreCase);
        SelectedTitle = item.Title;
        SelectedDescription = item.Description;
        DetailsTitle = item.Title;
        DetailsText = item.Description;
        StatusText = string.Empty;

        SelectedPackageSections.Clear();
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

    private async Task ApplyPackageSelectionAsync(SettingsSectionItemViewModel item, int selectionVersion)
    {
        if (item.PackageId is null)
        {
            ApplyCoreSelection(CoreSections[0]);
            return;
        }

        IsBusy = true;
        try
        {
            var hasSchema = _packageSettings.TryGetSchema(item.PackageId, out var schema);
            ApplyPackageSelectionHeader(item, schema);
            SelectedCoreLines.Clear();
            _packageSettings.ClearSelectedSections();
            HostedSettingsView = null;

            var result = await _packageSettings.LoadSelectionAsync(
                item.PackageId,
                hasSchema ? schema : null,
                _disposeCts.Token);
            if (!IsCurrentSelection(item, selectionVersion))
            {
                return;
            }

            HostedSettingsView = result.HostedSettingsView;
            _packageSettings.ApplySelectionResult(result);

            StatusText = result.StatusText;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentSelection(item, selectionVersion))
            {
                StatusText = ex.Message;
                ApplyCoreSelection(CoreSections[0]);
            }
        }
        finally
        {
            if (IsCurrentSelection(item, selectionVersion))
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyPackageSelectionHeader(
        SettingsSectionItemViewModel item,
        PackageConfigurationSchemaDescriptor? schema)
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

    private void Cli_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsCliViewModel.StatusText):
                OnPropertyChanged(nameof(CliStatusText));
                break;
            case nameof(SettingsCliViewModel.StatusDescription):
                OnPropertyChanged(nameof(CliStatusDescription));
                break;
            case nameof(SettingsCliViewModel.PlatformText):
                OnPropertyChanged(nameof(CliPlatformText));
                break;
            case nameof(SettingsCliViewModel.BundledPath):
                OnPropertyChanged(nameof(CliBundledPath));
                break;
            case nameof(SettingsCliViewModel.InstalledPath):
                OnPropertyChanged(nameof(CliInstalledPath));
                break;
            case nameof(SettingsCliViewModel.ShimPath):
                OnPropertyChanged(nameof(CliShimPath));
                break;
            case nameof(SettingsCliViewModel.WarningText):
                OnPropertyChanged(nameof(CliWarningText));
                break;
            case nameof(SettingsCliViewModel.PathInstructions):
                OnPropertyChanged(nameof(CliPathInstructions));
                break;
            case nameof(SettingsCliViewModel.HasWarning):
                OnPropertyChanged(nameof(HasCliWarning));
                break;
            case nameof(SettingsCliViewModel.HasPathInstructions):
                OnPropertyChanged(nameof(HasCliPathInstructions));
                break;
            case nameof(SettingsCliViewModel.CanInstallOrRepair):
                OnPropertyChanged(nameof(CanInstallOrRepairCli));
                break;
            case nameof(SettingsCliViewModel.CanUninstall):
                OnPropertyChanged(nameof(CanUninstallCli));
                break;
        }
    }

    private void Updates_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsUpdateViewModel.DownloadUpdatesAutomatically):
                OnPropertyChanged(nameof(DownloadUpdatesAutomatically));
                break;
            case nameof(SettingsUpdateViewModel.CurrentVersionText):
                OnPropertyChanged(nameof(UpdateCurrentVersionText));
                break;
            case nameof(SettingsUpdateViewModel.SourceText):
                OnPropertyChanged(nameof(UpdateSourceText));
                break;
            case nameof(SettingsUpdateViewModel.StatusText):
                OnPropertyChanged(nameof(UpdateStatusText));
                break;
            case nameof(SettingsUpdateViewModel.CanCheckForAppUpdates):
                OnPropertyChanged(nameof(CanCheckForAppUpdates));
                break;
        }
    }

    private void ApplyUpdateSettings()
    {
        _updates.LoadSettings();
    }

    private async Task<bool> SaveUpdateSettingsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _updates.SaveSettingsAsync();
            StatusText = result.StatusText;
            return result.Success;
        }
        finally
        {
            IsBusy = false;
        }
    }

}
