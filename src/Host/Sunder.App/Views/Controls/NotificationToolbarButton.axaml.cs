using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

public partial class NotificationToolbarButton : UserControl
{
    public NotificationToolbarButton()
    {
        InitializeComponent();
    }

    private void NotificationsButton_OnClick(object? sender, RoutedEventArgs e)
        => (DataContext as MainWindowViewModel)?.MarkNotificationsRead();
}
