using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class CreateStackWizardWindow : Window
{
    private CreateStackWizardViewModel? _subscribedViewModel;

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
        SubscribeViewModel(ViewModel);
    }

    private void SubscribeViewModel(CreateStackWizardViewModel? viewModel)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested -= ViewModel_OnCloseRequested;
            _subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested += ViewModel_OnCloseRequested;
            _subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested -= ViewModel_OnCloseRequested;
            _subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            _subscribedViewModel.Dispose();
            _subscribedViewModel = null;
        }

        DataContext = null;
    }

    private void ViewModel_OnCloseRequested(bool? result)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested -= ViewModel_OnCloseRequested;
        }

        Close(result);
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CreateStackWizardViewModel.CurrentStep))
        {
            StepScrollViewer.Offset = new Vector(0, 0);
        }
    }
}
