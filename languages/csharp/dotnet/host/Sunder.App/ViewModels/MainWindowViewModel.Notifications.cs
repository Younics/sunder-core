using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<NotificationItemViewModel> Notifications => _notificationTray.Notifications;

    public ObservableCollection<ToastNotificationViewModel> Toasts => _notificationTray.Toasts;

    public bool HasNotifications => _notificationTray.HasNotifications;

    public bool HasNoNotifications => _notificationTray.HasNoNotifications;

    public bool HasUnreadNotifications => _notificationTray.HasUnreadNotifications;

    public void MarkNotificationsRead()
        => _notificationTray.MarkNotificationsRead();

    [RelayCommand]
    private void ClearNotifications()
        => _notificationTray.ClearNotifications();

    [RelayCommand]
    private void DismissToastNotification(ToastNotificationViewModel? toast)
        => _notificationTray.DismissToastNotification(toast);

    private void OnNotificationsChanged()
        => RunOnUiThread(_notificationTray.ReloadNotifications);

    private void OnToastQueued(AppToastNotification notification)
        => RunOnUiThread(() => _notificationTray.AddToast(notification));

    private void NotificationTray_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
