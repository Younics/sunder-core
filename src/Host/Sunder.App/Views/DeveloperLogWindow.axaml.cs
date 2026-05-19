using Avalonia.Controls;
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

    private void OnLifecycleClosed()
    {
        if (DataContext is DeveloperLogWindowViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
