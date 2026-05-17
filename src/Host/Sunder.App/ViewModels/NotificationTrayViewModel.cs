using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed partial class NotificationTrayViewModel(NotificationCenterService notificationCenter) : ViewModelBase, IDisposable
{
    private bool _disposed;

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];

    public ObservableCollection<ToastNotificationViewModel> Toasts { get; } = [];

    public bool HasNotifications => Notifications.Count > 0;

    public bool HasNoNotifications => !HasNotifications;

    [ObservableProperty]
    private bool _hasUnreadNotifications;

    public void MarkNotificationsRead()
        => notificationCenter.MarkAllRead();

    public void ClearNotifications()
        => notificationCenter.ClearAll();

    public void ReloadNotifications()
    {
        var lastReadAtUtc = notificationCenter.LastReadAtUtc;
        Notifications.Clear();
        foreach (var notification in notificationCenter.ListNotifications())
        {
            Notifications.Add(new NotificationItemViewModel(notification, lastReadAtUtc));
        }

        HasUnreadNotifications = notificationCenter.HasUnreadTrayNotifications();
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasNoNotifications));
    }

    public void AddToast(AppToastNotification notification)
    {
        if (_disposed)
        {
            return;
        }

        while (Toasts.Count >= 3)
        {
            Toasts.RemoveAt(0);
        }

        var toast = new ToastNotificationViewModel(notification);
        Toasts.Add(toast);
        _ = DismissToastAsync(toast);
    }

    public void DismissToastNotification(ToastNotificationViewModel? toast)
    {
        if (toast is not null)
        {
            Toasts.Remove(toast);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private async Task DismissToastAsync(ToastNotificationViewModel toast)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3.5));
        }
        catch
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!_disposed)
            {
                Toasts.Remove(toast);
            }
        });
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }
}
