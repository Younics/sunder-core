using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class PackagesWindow : Window
{
    private PackagesWindowViewModel? ViewModel => DataContext as PackagesWindowViewModel;
    private readonly SecondaryWindowStateController? _stateController;
    private readonly SecondaryWindowLifecycleController _lifecycleController;
    private PackagesWindowViewModel? _subscribedViewModel;

    public PackagesWindow()
    {
        InitializeComponent();
        _lifecycleController = new SecondaryWindowLifecycleController(
            this,
            () => _stateController?.PersistWindowState(),
            OnLifecycleClosed);
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
    }

    public PackagesWindow(ShellStateService shellStateService, ShellState shellState)
        : this()
    {
        _stateController = new SecondaryWindowStateController(
            this,
            shellStateService,
            shellState,
            PackagesContentGrid.ColumnDefinitions[0],
            PackagesListPane,
            state => state.PackagesSidebarWidth,
            (state, width) => state.PackagesSidebarWidth = width,
            state => state.PackagesWindowPlacement,
            (state, placement) => state.PackagesWindowPlacement = placement);
        _stateController.ApplyInitialWindowState();
    }

    public void CloseForShutdown()
        => _lifecycleController.CloseForShutdown();

    private async void OnOpened(object? sender, EventArgs e)
    {
        _stateController?.ApplySidebarWidth();

        if (ViewModel is not null)
        {
            await ViewModel.InitializeAsync();
        }
    }

    private void PackagesSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        _stateController?.PersistSidebarWidth();
    }

    private void ToolbarDragHost_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowDragHost.BeginWindowDragOrToggleMaximize(this, e);

    private void OnLifecycleClosed()
    {
        SubscribeToViewModel(null);
        ViewModel?.Dispose();
        DataContext = null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => SubscribeToViewModel(ViewModel);

    private void SubscribeToViewModel(PackagesWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.MarketplaceImageGalleryRequested -= ShowMarketplaceImageGalleryAsync;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.MarketplaceImageGalleryRequested += ShowMarketplaceImageGalleryAsync;
        }
    }

    private async Task ShowMarketplaceImageGalleryAsync(
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
