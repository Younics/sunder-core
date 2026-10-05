using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class ExecutionRuntimeRepairConfirmationWindow : Window
{
    public ExecutionRuntimeRepairConfirmationWindow()
    {
        InitializeComponent();
    }

    public ExecutionRuntimeRepairConfirmationWindow(
        ExecutionRuntimeRepairConfirmationViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e) => Close(false);

    private void ApproveButton_OnClick(object? sender, RoutedEventArgs e) => Close(true);
}
