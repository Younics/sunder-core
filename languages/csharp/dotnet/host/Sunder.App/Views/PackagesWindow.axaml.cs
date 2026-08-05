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
    private readonly WindowCloseToHideCoordinator _closeCoordinator;
    private PackagesWindowViewModel? _subscribedViewModel;
    private readonly OwnedTaskObserver _tasks = new(nameof(PackagesWindow));
    private readonly CancellationTokenSource _lifetime = new();

    public PackagesWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplySecondaryWindowSize(this);
        _closeCoordinator = new WindowCloseToHideCoordinator(
            this,
            hideOnClose: true,
            closeOnEscape: true,
            persistWindowState: () => _stateController?.PersistWindowState(),
            closed: OnLifecycleClosed);
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
        => _closeCoordinator.CloseForShutdown();

    private void OnOpened(object? sender, EventArgs e)
    {
        _stateController?.ApplySidebarWidth();
        _stateController?.RecordWindowedPlacement();

        if (ViewModel is not null)
        {
            _tasks.Observe(ViewModel.InitializeAsync(_lifetime.Token), "initializing Packages");
        }
    }

    private void PackagesSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        _stateController?.PersistSidebarWidth();
    }

    private void OnLifecycleClosed()
    {
        SubscribeToViewModel(null);
        _lifetime.Cancel();
        _tasks.Dispose();
        ViewModel?.Dispose();
        DataContext = null;
        _lifetime.Dispose();
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
