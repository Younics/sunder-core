using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class MainWindow : Window
{
    internal MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private readonly MacNativeMenuController _macNativeMenuController;

    public MainWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplyMainWindowSize(this);
        _macNativeMenuController = new MacNativeMenuController(this, () => ViewModel);
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
        KeyDown += MainWindow_OnKeyDown;
        AddHandler(
            InputElement.PointerPressedEvent,
            MainWindow_OnPointerPressed,
            RoutingStrategies.Tunnel,
            true
        );
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;
        _macNativeMenuController.Dispose();
    }

    private void OnActivated(object? sender, EventArgs e) => Classes.Set("inactive", false);

    private void OnDeactivated(object? sender, EventArgs e) => Classes.Set("inactive", true);

    private void MainWindow_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        ShellToolbar.HideMenuIfPointerOutside(e);

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShellToolbar.HideMenuOnEscape(e.Key))
        {
            e.Handled = true;
        }
    }

    public void ShowPackageDragGhost(ShellItemViewModel item, bool compact, Point centerPosition) =>
        PackageDragOverlay.ShowPackageDragGhost(item, compact, centerPosition);

    public void MovePackageDragGhost(Point centerPosition) =>
        PackageDragOverlay.MovePackageDragGhost(centerPosition);

    public void HidePackageDragGhost() => PackageDragOverlay.HidePackageDragGhost();

    internal void StagePackageView(Control view)
    {
        if (PackageViewStagingSurface.Children.Contains(view))
        {
            return;
        }
        if (view.Parent is not null)
        {
            throw new InvalidOperationException(
                "A candidate package view cannot be staged while it has a parent."
            );
        }

        PackageViewStagingSurface.Children.Add(view);
        var availableSize = PackageViewStagingSurface.Bounds.Size;
        if (availableSize.Width <= 0 || availableSize.Height <= 0)
        {
            availableSize = ClientSize;
        }
        if (availableSize.Width <= 0 || availableSize.Height <= 0)
        {
            availableSize = new Size(1, 1);
        }

        PackageViewStagingSurface.Measure(availableSize);
        PackageViewStagingSurface.Arrange(new Rect(availableSize));
    }

    internal void DetachStagedPackageViews() => PackageViewStagingSurface.Children.Clear();
}
