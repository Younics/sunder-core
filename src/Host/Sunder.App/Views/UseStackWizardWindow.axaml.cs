using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class UseStackWizardWindow : Window
{
    private UseStackWizardViewModel? _subscribedViewModel;
    private readonly OwnedTaskObserver _tasks = new(nameof(UseStackWizardWindow));
    private readonly CancellationTokenSource _lifetime = new();

    private UseStackWizardViewModel? ViewModel => DataContext as UseStackWizardViewModel;

    public UseStackWizardWindow()
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
            _tasks.Observe(ViewModel.InitializeAsync(_lifetime.Token), "initializing Use Stack wizard");
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToViewModel(ViewModel);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        _tasks.Dispose();
        SubscribeToViewModel(null);
        ViewModel?.Dispose();
        DataContext = null;
        _lifetime.Dispose();
    }

    private void SubscribeToViewModel(UseStackWizardViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested -= ViewModel_OnCloseRequested;
            _subscribedViewModel.ImageGalleryRequested -= ShowImageGalleryAsync;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.CloseRequested += ViewModel_OnCloseRequested;
            _subscribedViewModel.ImageGalleryRequested += ShowImageGalleryAsync;
        }
    }

    private void ViewModel_OnCloseRequested(bool? result)
    {
        if (ViewModel is not null)
        {
            ViewModel.CloseRequested -= ViewModel_OnCloseRequested;
        }

        Close(result);
    }

    private async Task ShowImageGalleryAsync(
        IReadOnlyList<RegistryPackageMediaItemViewModel> media,
        int selectedIndex)
    {
        if (media.Count == 0)
        {
            return;
        }

        var galleryWindow = new PackageImageGalleryWindow(media, selectedIndex);
        await galleryWindow.ShowDialog(this);
    }
}
