using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class LocalPackageInstallConsentWindow : Window
{
    public LocalPackageInstallConsentWindow()
    {
        InitializeComponent();
    }

    public LocalPackageInstallConsentWindow(LocalPackageInstallConsentViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
        => Close(false);

    private void InstallButton_OnClick(object? sender, RoutedEventArgs e)
        => Close(true);
}
