using Avalonia.Controls;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class UseStackWizardWindow : Window
{
    private UseStackWizardViewModel? ViewModel => DataContext as UseStackWizardViewModel;

    public UseStackWizardWindow()
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
