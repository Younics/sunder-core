using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class MainWindow : Window
{
    internal MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private readonly MacNativeMenuController _macNativeMenuController;
    private readonly WindowCloseToHideCoordinator _closeCoordinator;
    private RenderTargetBitmap? _packageViewTransitionBitmap;
    private int _packageViewTransitionLeases;

    internal event EventHandler? HidingForClose;

    public MainWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplyMainWindowSize(this);
        _closeCoordinator = new WindowCloseToHideCoordinator(
            this,
            hideOnClose: OperatingSystem.IsMacOS(),
            hiding: () => HidingForClose?.Invoke(this, EventArgs.Empty));
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

    internal void CloseForShutdown() => _closeCoordinator.CloseForShutdown();

    private void OnClosed(object? sender, EventArgs e)
    {
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;
        _macNativeMenuController.Dispose();
        ClearPackageViewTransitionSnapshot();
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

    internal IDisposable AcquirePackageViewTransitionSnapshot()
    {
        BeginPackageViewTransitionSnapshot();
        return new PackageViewTransitionLease(this);
    }

    private void BeginPackageViewTransitionSnapshot()
    {
        _packageViewTransitionLeases++;
        if (_packageViewTransitionLeases != 1
            || !ShellWorkspaceControl.IsAttachedToVisualTree()
            || ShellWorkspaceControl.Bounds.Width <= 0
            || ShellWorkspaceControl.Bounds.Height <= 0)
        {
            return;
        }

        var renderScaling = RenderScaling;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(ShellWorkspaceControl.Bounds.Width * renderScaling)),
            Math.Max(1, (int)Math.Ceiling(ShellWorkspaceControl.Bounds.Height * renderScaling)));
        var bitmap = new RenderTargetBitmap(
            pixelSize,
            new Vector(96 * renderScaling, 96 * renderScaling));
        bitmap.Render(ShellWorkspaceControl);
        _packageViewTransitionBitmap = bitmap;
        PackageViewTransitionSnapshot.Source = bitmap;
        PackageViewTransitionSnapshot.IsVisible = true;
    }

    private void EndPackageViewTransitionSnapshot()
    {
        if (_packageViewTransitionLeases == 0
            || --_packageViewTransitionLeases != 0)
        {
            return;
        }
        ClearPackageViewTransitionSnapshot();
    }

    internal void DetachStagedPackageViews()
    {
        PackageViewStagingSurface.Children.Clear();
        _packageViewTransitionLeases = 0;
        ClearPackageViewTransitionSnapshot();
    }

    private void ClearPackageViewTransitionSnapshot()
    {
        PackageViewTransitionSnapshot.IsVisible = false;
        PackageViewTransitionSnapshot.Source = null;
        _packageViewTransitionBitmap?.Dispose();
        _packageViewTransitionBitmap = null;
    }

    private sealed class PackageViewTransitionLease(MainWindow owner) : IDisposable
    {
        private MainWindow? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndPackageViewTransitionSnapshot();
    }
}
