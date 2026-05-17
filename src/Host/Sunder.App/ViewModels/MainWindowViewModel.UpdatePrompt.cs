using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel
{
    public bool CanInstallAppUpdate => _appUpdatePrompt.CanInstallAppUpdate;

    public bool ShowUpdatePrompt => _appUpdatePrompt.ShowUpdatePrompt;

    public bool IsUpdateActionBusy => _appUpdatePrompt.IsUpdateActionBusy;

    public string UpdatePromptMessage => _appUpdatePrompt.UpdatePromptMessage;

    public string UpdatePromptStatus => _appUpdatePrompt.UpdatePromptStatus;

    public async Task CheckForAppUpdatesOnStartupAsync()
        => await _appUpdatePrompt.CheckForStartupPromptAsync(RunOnUiThread);

    [RelayCommand]
    private async Task InstallAvailableAppUpdateAsync()
        => await _appUpdatePrompt.InstallAvailableUpdateAsync(RunOnUiThread);

    [RelayCommand]
    private void DismissAppUpdatePrompt()
        => _appUpdatePrompt.DismissPrompt();

    private void AppUpdatePrompt_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
