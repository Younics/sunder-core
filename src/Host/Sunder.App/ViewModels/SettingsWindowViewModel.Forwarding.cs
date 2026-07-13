using System.ComponentModel;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsWindowViewModel
{
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

    private void ApplyUpdateSettings() => _updates.LoadSettings();

    private async Task<bool> SaveUpdateSettingsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _updates.SaveSettingsAsync();
            if (_disposed)
            {
                return false;
            }

            StatusText = result.StatusText;
            return result.Success;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }
}
