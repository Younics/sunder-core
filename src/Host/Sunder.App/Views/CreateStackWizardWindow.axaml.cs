using Avalonia.Controls;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class CreateStackWizardWindow : Window
{
    private CreateStackWizardViewModel? ViewModel => DataContext as CreateStackWizardViewModel;

    public CreateStackWizardWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.InitializeAsync();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.CloseRequested += ViewModel_OnCloseRequested;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.CloseRequested -= ViewModel_OnCloseRequested;
            ViewModel.Dispose();
        }

        DataContext = null;
    }

    private void ViewModel_OnCloseRequested(bool? result)
    {
        if (ViewModel is not null)
        {
            ViewModel.CloseRequested -= ViewModel_OnCloseRequested;
        }

        Close(result);
    }
}
