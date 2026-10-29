using Avalonia.Controls;
using Avalonia.Threading;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class DeveloperLogWindow : Window
{
    private readonly WindowCloseToHideCoordinator _closeCoordinator;
    private long _packageNavigation;

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

    internal void SelectPackage(string packageId)
    {
        if (DataContext is not DeveloperLogWindowViewModel model) return;
        var navigation = ++_packageNavigation;
        model.SelectPackage(packageId);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || navigation != _packageNavigation || !ReferenceEquals(DataContext, model)
                || !string.Equals(model.SelectedFilter?.PackageId, packageId, StringComparison.OrdinalIgnoreCase)) return;
            UpdateLayout();
            if (model.FilteredEntries.LastOrDefault() is { } latest) LogEntries.ScrollIntoView(latest);
        }, DispatcherPriority.Loaded);
    }

    private void OnLifecycleClosed()
    {
        if (DataContext is DeveloperLogWindowViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
