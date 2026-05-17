using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Sunder.App.Views.Controls;

public partial class WindowChromeControls : UserControl
{
    public WindowChromeControls()
    {
        InitializeComponent();
        StandardControls.IsVisible = !OperatingSystem.IsMacOS();
        MacControls.IsVisible = OperatingSystem.IsMacOS();
    }

    private Window? OwnerWindow => this.FindAncestorOfType<Window>();

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void ToggleFullScreenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } window)
        {
            window.WindowState = window.WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OwnerWindow?.Close();
    }
}
