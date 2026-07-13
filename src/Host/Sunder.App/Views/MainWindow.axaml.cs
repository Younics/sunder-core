using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private readonly MacNativeMenuController _macNativeMenuController;

    public MainWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplyMainWindowSize(this);
        _macNativeMenuController = new MacNativeMenuController(this, () => ViewModel);
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        Closed += OnClosed;
        KeyDown += MainWindow_OnKeyDown;
        AddHandler(InputElement.PointerPressedEvent, MainWindow_OnPointerPressed, RoutingStrategies.Tunnel, true);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ViewModel?.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;
        _macNativeMenuController.Dispose();
    }

    private void OnActivated(object? sender, EventArgs e)
        => Classes.Set("inactive", false);

    private void OnDeactivated(object? sender, EventArgs e)
        => Classes.Set("inactive", true);

    private void MainWindow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => ShellToolbar.HideMenuIfPointerOutside(e);

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShellToolbar.HideMenuOnEscape(e.Key))
        {
            e.Handled = true;
        }
    }

    public void ShowPackageDragGhost(ShellItemViewModel item, bool compact, Point centerPosition)
        => PackageDragOverlay.ShowPackageDragGhost(item, compact, centerPosition);

    public void MovePackageDragGhost(Point centerPosition)
        => PackageDragOverlay.MovePackageDragGhost(centerPosition);

    public void HidePackageDragGhost()
        => PackageDragOverlay.HidePackageDragGhost();
}
