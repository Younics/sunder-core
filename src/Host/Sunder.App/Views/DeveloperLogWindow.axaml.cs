using Avalonia.Controls;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class DeveloperLogWindow : Window
{
    private readonly WindowCloseToHideCoordinator _closeCoordinator;

    public DeveloperLogWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplySecondaryWindowSize(this);
        _closeCoordinator = new WindowCloseToHideCoordinator(
            this,
            hideOnClose: true,
            closeOnEscape: true,
            closed: OnLifecycleClosed);
    }

    public void CloseForShutdown()
        => _closeCoordinator.CloseForShutdown();

    private void OnLifecycleClosed()
    {
        if (DataContext is DeveloperLogWindowViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
