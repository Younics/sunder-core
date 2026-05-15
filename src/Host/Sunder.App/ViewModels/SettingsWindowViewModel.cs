using System.Collections.ObjectModel;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IRuntimeApiClient _runtimeApiClient;
    private readonly PackageViewHostService _packageViewHostService;
    private readonly CliInstallationService _cliInstallationService;
    private readonly SunderUpdateService _updateService;
    private readonly Dictionary<string, PackageConfigurationSchemaDescriptor> _schemasByPackageId = new(StringComparer.OrdinalIgnoreCase);
    private SettingsSectionItemViewModel? _selectedSection;
    private Task _packageSectionsLoadTask;

    public SettingsWindowViewModel(
        IRuntimeApiClient runtimeApiClient,
        PackageViewHostService packageViewHostService,
        CliInstallationService cliInstallationService,
        SunderUpdateService? updateService = null)
    {
        _runtimeApiClient = runtimeApiClient;
        _packageViewHostService = packageViewHostService;
        _cliInstallationService = cliInstallationService;
        _updateService = updateService ?? new SunderUpdateService();

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

        PackageSections = [];
        SelectedCoreLines = [];
        SelectedPackageSections = [];

        _selectedSection = CoreSections[0];
        _selectedSection.IsSelected = true;
        ApplyCoreSelection(_selectedSection);
        _packageSectionsLoadTask = LoadPackageSectionsAsync(preserveSelection: false);
    }

    public ObservableCollection<SettingsSectionItemViewModel> CoreSections { get; }

    public ObservableCollection<SettingsSectionItemViewModel> PackageSections { get; }

    public ObservableCollection<string> SelectedCoreLines { get; }

    public ObservableCollection<SettingsFieldSectionViewModel> SelectedPackageSections { get; }

    public bool HasPackageSections => PackageSections.Count > 0;

    public bool IsCoreSelection => !IsPackageSelection;

    public bool ShowPlainCoreSelection => IsCoreSelection && !IsCliSelection && !IsUpdatesSelection;

    public bool ShowCliSettings => IsCliSelection;

    public bool ShowUpdateSettings => IsUpdatesSelection;

    public bool HasHostedSettingsView => HostedSettingsView is not null;

    public bool ShowGenericPackageSettings => IsPackageSelection && !HasHostedSettingsView;

    public bool ShowScrollableSelectionContent => !HasHostedSettingsView;

    public bool ShowApplySaveButtons => !HasHostedSettingsView && !IsCliSelection;

    public bool HasCliWarning => !string.IsNullOrWhiteSpace(CliWarningText);

    public bool HasCliPathInstructions => !string.IsNullOrWhiteSpace(CliPathInstructions);

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

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliStatusText = "Not checked";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliStatusDescription = "Open this section to check the terminal command install.";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliPlatformText = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliBundledPath = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliInstalledPath = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliShimPath = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliWarningText = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _cliPathInstructions = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _canInstallOrRepairCli;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _canUninstallCli;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _downloadUpdatesAutomatically;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _updateCurrentVersionText = "Unknown";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _updateSourceText = "Not configured";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _updateStatusText = "Open this section to check app update status.";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _canCheckForAppUpdates;

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

    partial void OnCliWarningTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasCliWarning));
    }

    partial void OnCliPathInstructionsChanged(string value)
    {
        OnPropertyChanged(nameof(HasCliPathInstructions));
    }

    public async Task SelectSectionAsync(SettingsSectionItemViewModel item)
    {
        if (_selectedSection is not null)
        {
            _selectedSection.IsSelected = false;
        }

        _selectedSection = item;
        _selectedSection.IsSelected = true;

        if (item.IsPackage)
        {
            await ApplyPackageSelectionAsync(item);
            return;
        }

        ApplyCoreSelection(item);
        if (IsCliSelection)
        {
            await RefreshCliStatusAsync(showSuccessStatus: false);
        }
    }

    public async Task ApplyAsync()
    {
        if (IsUpdatesSelection)
        {
            await SaveUpdateSettingsAsync();
            return;
        }

        if (!ShowGenericPackageSettings || _selectedSection?.PackageId is null)
        {
            StatusText = "Nothing to apply for the selected section yet.";
            return;
        }

        IsBusy = true;
        try
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in SelectedPackageSections.SelectMany(section => section.Fields))
            {
                if (field is SecretSettingsFieldViewModel secretField)
                {
                    if (!string.IsNullOrWhiteSpace(secretField.Value))
                    {
                        values[field.Key] = secretField.Value;
                    }

                    continue;
                }

                values[field.Key] = field.GetPersistedValue();
            }

            await _runtimeApiClient.SavePackageConfigurationValuesAsync(_selectedSection.PackageId, values);
            StatusText = $"Applied settings for {_selectedSection.Title}.";
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

    public async Task SaveAsync()
    {
        await ApplyAsync();
    }

    public async Task<bool> SelectPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        await _packageSectionsLoadTask;
        var item = FindPackageSection(packageId);
        if (item is null)
        {
            _packageSectionsLoadTask = LoadPackageSectionsAsync(preserveSelection: true, cancellationToken);
            await _packageSectionsLoadTask;
            item = FindPackageSection(packageId);
        }

        if (item is null)
        {
            StatusText = $"Package settings were not found for '{packageId}'.";
            return false;
        }

        await SelectSectionAsync(item);
        await NotifyHostedSettingsNavigatedAsync(packageId, parameters ?? new Dictionary<string, string?>(), cancellationToken);
        return true;
    }

    public async Task RefreshPackageSectionsAsync(CancellationToken cancellationToken = default)
    {
        _packageSectionsLoadTask = LoadPackageSectionsAsync(preserveSelection: true, cancellationToken);
        await _packageSectionsLoadTask;
    }

    public async Task RefreshCliStatusAsync(bool showSuccessStatus = true)
    {
        IsBusy = true;
        try
        {
            ApplyCliStatus(await _cliInstallationService.GetStatusAsync());
            if (showSuccessStatus)
            {
                StatusText = "CLI status refreshed.";
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            CliWarningText = ex.Message;
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
            var result = await _cliInstallationService.EnsureInstalledAsync();
            ApplyCliStatus(result.Status);
            StatusText = result.Status.Summary;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            CliWarningText = ex.Message;
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
            ApplyCliStatus(await _cliInstallationService.UninstallAsync());
            StatusText = "CLI user install removed. Existing PATH entries were left unchanged.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            CliWarningText = ex.Message;
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
        CanCheckForAppUpdates = false;
        UpdateStatusText = "Checking GitHub Releases for app updates...";
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            ApplyUpdateRuntimeStatus(result.RuntimeStatus);
            UpdateStatusText = result.Message;
        }
        catch (Exception ex)
        {
            UpdateStatusText = ex.Message;
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CanCheckForAppUpdates = _updateService.GetRuntimeStatus().CanCheckForUpdates;
        }
    }

    public void Dispose()
    {
        _runtimeApiClient.Dispose();
    }

    private async Task LoadPackageSectionsAsync(bool preserveSelection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsBusy = true;
        var selectedPackageId = preserveSelection ? _selectedSection?.PackageId : null;
        try
        {
            var schemas = await _runtimeApiClient.GetConfigurationSchemasAsync(cancellationToken);
            PackageSections.Clear();
            _schemasByPackageId.Clear();

            foreach (var schema in schemas)
            {
                _schemasByPackageId[schema.PackageId] = schema;
                PackageSections.Add(new SettingsSectionItemViewModel(
                    schema.PackageId,
                    schema.PackageDisplayName,
                    schema.Summary ?? $"Configure {schema.PackageDisplayName}.",
                    true,
                    schema.PackageId
                ));
            }

            foreach (var settingsViewPackage in _packageViewHostService.ListSettingsViewPackages())
            {
                if (_schemasByPackageId.ContainsKey(settingsViewPackage.PackageId))
                {
                    continue;
                }

                PackageSections.Add(new SettingsSectionItemViewModel(
                    settingsViewPackage.PackageId,
                    settingsViewPackage.DisplayName,
                    settingsViewPackage.Summary ?? $"Configure {settingsViewPackage.DisplayName}.",
                    true,
                    settingsViewPackage.PackageId));
            }

            var orderedSections = PackageSections
                .OrderBy(section => section.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            PackageSections.Clear();
            foreach (var section in orderedSections)
            {
                PackageSections.Add(section);
            }

            OnPropertyChanged(nameof(HasPackageSections));
            PreservePackageSelectionAfterRefresh(selectedPackageId);
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

    private SettingsSectionItemViewModel? FindPackageSection(string packageId)
        => PackageSections.FirstOrDefault(section => string.Equals(section.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

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

        var refreshedSelection = FindPackageSection(selectedPackageId);
        if (refreshedSelection is null)
        {
            if (_selectedSection is not null)
            {
                _selectedSection.IsSelected = false;
            }

            _selectedSection = CoreSections[0];
            _selectedSection.IsSelected = true;
            ApplyCoreSelection(_selectedSection);
            return;
        }

        if (_selectedSection is not null)
        {
            _selectedSection.IsSelected = false;
        }

        _selectedSection = refreshedSelection;
        _selectedSection.IsSelected = true;

        _schemasByPackageId.TryGetValue(selectedPackageId, out var schema);
        SelectedTitle = schema?.PackageDisplayName ?? refreshedSelection.Title;
        SelectedDescription = schema?.Summary ?? refreshedSelection.Description;
        DetailsTitle = schema?.PackageDisplayName ?? refreshedSelection.Title;
        DetailsText = $"Package id: {selectedPackageId}";
        IsCliSelection = false;
        IsUpdatesSelection = false;
        IsPackageSelection = true;
    }

    private void ApplyCoreSelection(SettingsSectionItemViewModel item)
    {
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
            foreach (var line in GetCoreSectionLines(item.Id))
            {
                SelectedCoreLines.Add(line);
            }
        }
    }

    private async Task ApplyPackageSelectionAsync(SettingsSectionItemViewModel item)
    {
        if (item.PackageId is null)
        {
            ApplyCoreSelection(CoreSections[0]);
            return;
        }

        IsBusy = true;
        try
        {
            var hasSchema = _schemasByPackageId.TryGetValue(item.PackageId, out var schema);
            SelectedTitle = schema?.PackageDisplayName ?? item.Title;
            SelectedDescription = schema?.Summary ?? item.Description;
            DetailsTitle = schema?.PackageDisplayName ?? item.Title;
            DetailsText = $"Package id: {item.PackageId}";
            IsCliSelection = false;
            IsUpdatesSelection = false;
            IsPackageSelection = true;
            SelectedCoreLines.Clear();
            SelectedPackageSections.Clear();
            HostedSettingsView = null;

            if (_packageViewHostService.HasSettingsView(item.PackageId))
            {
                HostedSettingsView = _packageViewHostService.GetOrCreateSettingsView(item.PackageId);
                StatusText = HostedSettingsView is null
                    ? "Package settings view is unavailable."
                    : string.Empty;
                return;
            }

            if (!hasSchema || schema is null)
            {
                StatusText = "This package does not provide configurable settings.";
                return;
            }

            var values = await _runtimeApiClient.GetPackageConfigurationValuesAsync(item.PackageId);
            var valueMap = values?.Values ?? new Dictionary<string, string?>();
            var storedSecretKeys = values?.StoredSecretKeys ?? [];

            foreach (var section in schema.Sections)
            {
                var fieldViewModels = section.Fields
                    .Select(field => CreateFieldViewModel(
                        field,
                        valueMap.TryGetValue(field.Key, out var currentValue) ? currentValue : field.DefaultValue,
                        storedSecretKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase)))
                    .ToArray();

                SelectedPackageSections.Add(new SettingsFieldSectionViewModel(section.Title, section.Description, fieldViewModels));
            }

            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            ApplyCoreSelection(CoreSections[0]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static SettingsFieldViewModel CreateFieldViewModel(
        PackageConfigurationFieldDescriptor field,
        string? value,
        bool hasStoredSecretValue)
    {
        return field.Kind switch
        {
            PackageConfigurationFieldKind.Secret => new SecretSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Placeholder,
                hasStoredSecretValue,
                null
            ),
            PackageConfigurationFieldKind.Boolean => new BooleanSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                bool.TryParse(value, out var parsedBoolean) && parsedBoolean
            ),
            PackageConfigurationFieldKind.Select => new SelectSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Options.Select(option => new SettingsOptionItem(option.Value, option.Label)).ToArray(),
                value
            ),
            _ => new TextSettingsFieldViewModel(
                field.Key,
                field.Label,
                field.Description,
                field.IsRequired,
                field.Placeholder,
                value
            ),
        };
    }

    private void ApplyCliStatus(CliInstallationStatus status)
    {
        CliStatusText = status.Summary;
        CliStatusDescription = status.IsFullyInstalled
            ? "Sunder installs the command shim for this user but does not verify shell-profile PATH entries. Use the PATH instructions below, then run sunder --help in your terminal to confirm."
            : status.Warning ?? "The sunder command is not fully configured.";
        CliPlatformText = status.PlatformName;
        CliBundledPath = status.Paths.BundledCliPath ?? "Not found";
        CliInstalledPath = status.Paths.InstalledCliPath;
        CliShimPath = status.Paths.ShimPath;
        CliWarningText = status.Warning ?? string.Empty;
        CliPathInstructions = status.PathInstructions;
        CanInstallOrRepairCli = status.CanInstallOrRepair;
        CanUninstallCli = status.IsInstalled || status.IsShimInstalled;
    }

    private void ApplyUpdateSettings()
    {
        var settings = _updateService.LoadSettings();
        DownloadUpdatesAutomatically = settings.DownloadUpdatesAutomatically;
        ApplyUpdateRuntimeStatus(_updateService.GetRuntimeStatus());
    }

    private void ApplyUpdateRuntimeStatus(SunderUpdateRuntimeStatus status)
    {
        UpdateCurrentVersionText = status.CurrentVersion;
        UpdateSourceText = status.Source;
        UpdateStatusText = status.Message;
        CanCheckForAppUpdates = status.CanCheckForUpdates;
    }

    private async Task SaveUpdateSettingsAsync()
    {
        IsBusy = true;
        try
        {
            await _updateService.SaveSettingsAsync(new AppUpdateSettings
            {
                DownloadUpdatesAutomatically = DownloadUpdatesAutomatically,
            });
            StatusText = DownloadUpdatesAutomatically
                ? "Sunder will download app updates in the background and apply them on the next start."
                : "Automatic app update downloads are disabled.";
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

    private static IReadOnlyList<string> GetCoreSectionLines(string sectionId)
        => sectionId switch
        {
            "appearance" =>
            [
                "Appearance keeps host-level theme and presentation settings.",
                "Package-owned UI should still consume Sunder semantic theme tokens instead of mutating shell chrome.",
            ],
            "runtime" =>
            [
                "Runtime settings will control shell behavior, loading preferences, and runtime diagnostics.",
                "Package-specific configuration is rendered below the Packages separator when a package contributes a config schema.",
            ],
            "updates" =>
            [
                "Package updates and managed rollout workflows are planned but not implemented in this slice.",
            ],
            "notifications" =>
            [
                "Notification preferences will later control package alerts, runtime warnings, and task completion signals.",
            ],
            "privacy" =>
            [
                "Sunder remains local-first. Package-specific secrets and trust decisions will continue to evolve here.",
            ],
            "advanced" =>
            [
                "Advanced settings will eventually host diagnostic and power-user controls for package developers and operators.",
            ],
            _ =>
            [
                "Select a core section or a package entry to configure it.",
            ],
        };
}
