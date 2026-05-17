using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed partial class AppUpdatePromptViewModel(AppUpdatePromptCoordinator updatePromptCoordinator) : ViewModelBase
{
    [ObservableProperty]
    private bool _showUpdatePrompt;

    [ObservableProperty]
    private bool _isUpdateActionBusy;

    [ObservableProperty]
    private string _updatePromptMessage = string.Empty;

    [ObservableProperty]
    private string _updatePromptStatus = string.Empty;

    private SunderUpdateInfo? _availableAppUpdate;

    public bool CanInstallAppUpdate => ShowUpdatePrompt && !IsUpdateActionBusy;

    partial void OnShowUpdatePromptChanged(bool value) => OnPropertyChanged(nameof(CanInstallAppUpdate));

    partial void OnIsUpdateActionBusyChanged(bool value) => OnPropertyChanged(nameof(CanInstallAppUpdate));

    public async Task CheckForStartupPromptAsync(Action<Action> runOnUiThread)
    {
        var update = await updatePromptCoordinator.CheckForStartupPromptAsync();
        if (update is not null)
        {
            runOnUiThread(() => ShowPrompt(update));
        }
    }

    public async Task InstallAvailableUpdateAsync(Action<Action> runOnUiThread)
    {
        if (_availableAppUpdate is null || IsUpdateActionBusy)
        {
            return;
        }

        IsUpdateActionBusy = true;
        UpdatePromptStatus = "Downloading update...";
        var failureStatus = await updatePromptCoordinator.InstallUpdateAndRestartAsync(
            _availableAppUpdate,
            progress => runOnUiThread(() => UpdatePromptStatus = $"Downloading update... {progress}%"));
        if (failureStatus is not null)
        {
            UpdatePromptStatus = failureStatus;
            IsUpdateActionBusy = false;
        }
    }

    public void DismissPrompt()
    {
        if (IsUpdateActionBusy)
        {
            return;
        }

        _availableAppUpdate = null;
        ShowUpdatePrompt = false;
        UpdatePromptMessage = string.Empty;
        UpdatePromptStatus = string.Empty;
    }

    private void ShowPrompt(SunderUpdateInfo update)
    {
        _availableAppUpdate = update;
        UpdatePromptMessage = $"A new version of Sunder ({update.Version}) is now available to install.";
        UpdatePromptStatus = "Install now or skip until the next app start.";
        ShowUpdatePrompt = true;
        IsUpdateActionBusy = false;
    }
}
