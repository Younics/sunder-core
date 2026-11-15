using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsNotificationsViewModel(NotificationCenterService notifications) : ViewModelBase
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _showToasts;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _showInformation;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _showSuccess;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _showWarnings;

    public void LoadSettings()
    {
        var settings = notifications.Preferences;
        ShowToasts = settings.ShowToasts;
        ShowInformation = settings.ShowInformation;
        ShowSuccess = settings.ShowSuccess;
        ShowWarnings = settings.ShowWarnings;
    }

    public void Save() => notifications.SavePreferences(new NotificationPreferences(ShowToasts, ShowInformation, ShowSuccess, ShowWarnings));
}
