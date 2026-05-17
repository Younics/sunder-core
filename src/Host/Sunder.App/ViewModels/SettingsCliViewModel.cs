using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

internal sealed partial class SettingsCliViewModel(SettingsCliCoordinator coordinator) : ViewModelBase
{
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public bool HasPathInstructions => !string.IsNullOrWhiteSpace(PathInstructions);

    [ObservableProperty]
    private string _statusText = "Not checked";

    [ObservableProperty]
    private string _statusDescription = "Open this section to check the terminal command install.";

    [ObservableProperty]
    private string _platformText = string.Empty;

    [ObservableProperty]
    private string _bundledPath = string.Empty;

    [ObservableProperty]
    private string _installedPath = string.Empty;

    [ObservableProperty]
    private string _shimPath = string.Empty;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private string _pathInstructions = string.Empty;

    [ObservableProperty]
    private bool _canInstallOrRepair;

    [ObservableProperty]
    private bool _canUninstall;

    partial void OnWarningTextChanged(string value)
        => OnPropertyChanged(nameof(HasWarning));

    partial void OnPathInstructionsChanged(string value)
        => OnPropertyChanged(nameof(HasPathInstructions));

    public async Task<string?> RefreshStatusAsync(bool showSuccessStatus)
        => ApplyOperationResult(await coordinator.RefreshStatusAsync(showSuccessStatus));

    public async Task<string?> InstallOrRepairAsync()
        => ApplyOperationResult(await coordinator.InstallOrRepairAsync());

    public async Task<string?> UninstallAsync()
        => ApplyOperationResult(await coordinator.UninstallAsync());

    private string? ApplyOperationResult(SettingsCliOperationResult result)
    {
        if (result.State is not null)
        {
            ApplyState(result.State);
        }

        if (!string.IsNullOrWhiteSpace(result.WarningText))
        {
            WarningText = result.WarningText;
        }

        return string.IsNullOrWhiteSpace(result.StatusText) ? null : result.StatusText;
    }

    private void ApplyState(SettingsCliState state)
    {
        StatusText = state.StatusText;
        StatusDescription = state.StatusDescription;
        PlatformText = state.PlatformText;
        BundledPath = state.BundledPath;
        InstalledPath = state.InstalledPath;
        ShimPath = state.ShimPath;
        WarningText = state.WarningText;
        PathInstructions = state.PathInstructions;
        CanInstallOrRepair = state.CanInstallOrRepair;
        CanUninstall = state.CanUninstall;
    }
}
