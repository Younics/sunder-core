using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed partial class AppUpdatePromptViewModel(AppUpdatePromptCoordinator updatePromptCoordinator) : ViewModelBase, IDisposable
{
    private readonly LatestAsyncRequest _request = new();
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

    public async Task CheckForStartupPromptAsync(Action<Action> runOnUiThread, CancellationToken cancellationToken)
    {
        using var request = _request.Start(cancellationToken);
        var update = await updatePromptCoordinator.CheckForStartupPromptAsync(request.Token);
        if (update is not null && request.IsCurrent)
        {
            runOnUiThread(() =>
            {
                if (request.IsCurrent)
                {
                    ShowPrompt(update);
                }
            });
        }
    }

    public async Task InstallAvailableUpdateAsync(Action<Action> runOnUiThread, CancellationToken cancellationToken)
    {
        if (_availableAppUpdate is null || IsUpdateActionBusy)
        {
            return;
        }

        using var request = _request.Start(cancellationToken);
        IsUpdateActionBusy = true;
        UpdatePromptStatus = "Downloading update...";
        var failureStatus = await updatePromptCoordinator.InstallUpdateAndRestartAsync(
            _availableAppUpdate,
            progress => runOnUiThread(() =>
            {
                if (request.IsCurrent)
                {
                    UpdatePromptStatus = $"Downloading update... {progress}%";
                }
            }),
            request.Token);
        if (failureStatus is not null && request.IsCurrent)
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

    public void Dispose() => _request.Dispose();
}
