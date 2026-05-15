using Avalonia.Controls;
using Avalonia.Input;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

public partial class BackgroundProcessMonitor : UserControl
{
    public BackgroundProcessMonitor()
    {
        InitializeComponent();
        ProcessesPopup.PlacementTarget = MonitorButton;
    }

    private void MonitorButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ProcessesPopup.IsOpen = !ProcessesPopup.IsOpen;

    private void ClosePopupButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ProcessesPopup.IsOpen = false;

    private void ResizeLeftThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(-e.Vector.X, 0);

    private void ResizeRightThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(e.Vector.X, 0);

    private void ResizeTopThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(0, -e.Vector.Y);

    private void ResizeBottomThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(0, e.Vector.Y);

    private void ResizeTopLeftThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(-e.Vector.X, -e.Vector.Y);

    private void ResizeTopRightThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(e.Vector.X, -e.Vector.Y);

    private void ResizeBottomLeftThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(-e.Vector.X, e.Vector.Y);

    private void ResizeBottomRightThumb_OnDragDelta(object? sender, VectorEventArgs e)
        => ResizePopover(e.Vector.X, e.Vector.Y);

    private void ResizePopover(double deltaWidth, double deltaHeight)
    {
        if (DataContext is BackgroundProcessMonitorViewModel viewModel)
        {
            viewModel.ResizePopover(deltaWidth, deltaHeight);
        }
    }

    private void ResizeThumb_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is BackgroundProcessMonitorViewModel viewModel)
        {
            viewModel.PersistPopoverSize();
        }
    }
}
