using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Views.Controls;

public partial class ShellToolbar : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private readonly ToolbarMainMenuController _toolbarMainMenuController;

    public ShellToolbar()
    {
        InitializeComponent();
        MoreActionsButton.IsVisible = !OperatingSystem.IsMacOS();
        _toolbarMainMenuController = new ToolbarMainMenuController(
            ToolbarMainMenu,
            ToolbarDefaultActions,
            MiddlePackageIconBar,
            ToolbarLeftMenuHost,
            () => ViewModel);
    }

    public void HideMenuIfPointerOutside(PointerPressedEventArgs e)
        => _toolbarMainMenuController.HideIfPointerOutside(e);

    public bool HideMenuOnEscape(Key key)
        => _toolbarMainMenuController.HideOnEscape(key);

    private void MoreActionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_toolbarMainMenuController.Show())
        {
            e.Handled = true;
        }
    }
}
