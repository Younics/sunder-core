using Avalonia.Controls;
using Avalonia.Input;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class DeveloperLogWindow : Window
{
    private readonly SecondaryWindowLifecycleController _lifecycleController;

    public DeveloperLogWindow()
    {
        InitializeComponent();
        _lifecycleController = new SecondaryWindowLifecycleController(this, () => { }, OnLifecycleClosed);
    }

    public void CloseForShutdown()
        => _lifecycleController.CloseForShutdown();

    private void ToolbarDragHost_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnLifecycleClosed()
    {
        if (DataContext is DeveloperLogWindowViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
