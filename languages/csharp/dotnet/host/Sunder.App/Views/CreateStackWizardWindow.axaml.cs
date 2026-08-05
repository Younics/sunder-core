using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class CreateStackWizardWindow : Window
{
    private CreateStackWizardViewModel? _subscribedViewModel;
    private readonly OwnedTaskObserver _tasks = new(nameof(CreateStackWizardWindow));
    private readonly CancellationTokenSource _lifetime = new();

    private CreateStackWizardViewModel? ViewModel => DataContext as CreateStackWizardViewModel;

    public CreateStackWizardWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            _tasks.Observe(ViewModel.InitializeAsync(_lifetime.Token), "initializing Create Stack wizard");
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
        _lifetime.Cancel();
        _tasks.Dispose();
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested -= ViewModel_OnCloseRequested;
            _subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            _subscribedViewModel.Dispose();
            _subscribedViewModel = null;
        }

        DataContext = null;
        _lifetime.Dispose();
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
