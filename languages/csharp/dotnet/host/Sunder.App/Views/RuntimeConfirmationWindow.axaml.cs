using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class RuntimeConfirmationWindow : Window
{
    public RuntimeConfirmationWindow() => InitializeComponent();
    public RuntimeConfirmationWindow(RuntimeConfirmationViewModel viewModel) : this() => DataContext = viewModel;
    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm_OnClick(object? sender, RoutedEventArgs e) => Close(true);
}
