using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

public partial class PackageIconBar : UserControl
{
    private PackageIconBarViewModel? ViewModel => DataContext as PackageIconBarViewModel;
    internal PackageIconBarDragController DragController { get; }
    internal bool IsBarRootVisible => BarRoot.IsVisible;

    public PackageIconBar()
    {
        InitializeComponent();
        DragController = new PackageIconBarDragController(this, BarRoot, () => ViewModel);
        AttachedToVisualTree += (_, _) => UpdateVisibleCapacity();
        DataContextChanged += (_, _) => UpdateVisibleCapacity();
        SizeChanged += (_, _) => UpdateVisibleCapacity();

        BarRoot.AddHandler(InputElement.PointerPressedEvent, DragController.OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        BarRoot.AddHandler(InputElement.PointerMovedEvent, DragController.OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        BarRoot.AddHandler(InputElement.PointerReleasedEvent, DragController.OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        BarRoot.AddHandler(InputElement.PointerCaptureLostEvent, DragController.OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
    }

    private void UpdateVisibleCapacity()
    {
        if (ViewModel is null)
        {
            return;
        }

        var availableLength = ViewModel.IsHorizontal
            ? Bounds.Width
            : Math.Max(0, Bounds.Height - GetVerticalContentInset());
        if (availableLength <= 0)
        {
            return;
        }

        var visibleCapacity = PackageIconBarLayoutMetrics.CalculateVisibleCapacity(availableLength);
        ViewModel.UpdateVisibleCapacity(visibleCapacity);
    }

    private double GetVerticalContentInset()
    {
        if (ViewModel?.IsVertical != true)
        {
            return 0;
        }

        var inset = 0d;
        if (Classes.Contains("side-top-lane"))
        {
            inset += PackageIconBarLayoutMetrics.SideLaneInset;
        }

        if (Classes.Contains("side-bottom-lane"))
        {
            inset += PackageIconBarLayoutMetrics.SideLaneInset;
        }

        return inset;
    }

    private void OverflowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || ViewModel is null || ViewModel.OverflowItems.Count == 0)
        {
            return;
        }

        PackageIconBarContextMenu.OpenOverflowMenu(control, ViewModel);
    }

}
