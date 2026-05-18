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
        _macNativeMenuController = new MacNativeMenuController(this, () => ViewModel);
        Closing += OnClosing;
        Closed += OnClosed;
        KeyDown += MainWindow_OnKeyDown;
        AddHandler(InputElement.PointerPressedEvent, MainWindow_OnPointerPressed, RoutingStrategies.Tunnel, true);
    }

    internal static (double LeftWidth, double RightWidth) CalculateTopColumnWidths(
        double totalWidth,
        double requestedLeftWidth,
        double requestedRightWidth,
        bool hasLeftPanel,
        bool hasRightPanel,
        double leftSplitterWidth = ShellLayoutCalculator.SplitterThickness,
        double rightSplitterWidth = ShellLayoutCalculator.SplitterThickness)
        => MainWindowLayoutController.CalculateTopColumnWidths(
            totalWidth,
            requestedLeftWidth,
            requestedRightWidth,
            hasLeftPanel,
            hasRightPanel,
            leftSplitterWidth,
            rightSplitterWidth);

    internal static double CalculateResizableExtent(double totalExtent, params double[] fixedExtents)
        => MainWindowLayoutController.CalculateResizableExtent(totalExtent, fixedExtents);

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ViewModel?.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _macNativeMenuController.Dispose();
    }

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
