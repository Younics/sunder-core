using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class PackagesWindow : Window
{
    private PackagesWindowViewModel? ViewModel => DataContext as PackagesWindowViewModel;
    private readonly ShellStateService? _shellStateService;
    private readonly ShellState? _shellState;
    private PackagesWindowViewModel? _subscribedViewModel;
    private bool _allowClose;

    public PackagesWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public PackagesWindow(ShellStateService shellStateService, ShellState shellState)
        : this()
    {
        _shellStateService = shellStateService;
        _shellState = shellState;
        PackagesContentGrid.ColumnDefinitions[0].Width = new GridLength(shellState.PackagesSidebarWidth);
        ShellWindowPlacementService.Apply(this, shellState.PackagesWindowPlacement);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_shellState is not null)
        {
            PackagesContentGrid.ColumnDefinitions[0].Width = new GridLength(_shellState.PackagesSidebarWidth);
        }

        if (ViewModel is not null)
        {
            await ViewModel.InitializeAsync();
        }
    }

    private void PackagesSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        PersistListWidth();
    }

    private void ToolbarDragHost_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual visual)
        {
            var ancestors = visual.GetSelfAndVisualAncestors().OfType<StyledElement>().ToArray();
            if (ancestors.Any(x => x is Button or TextBox))
            {
                return;
            }
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizedState();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximizedState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            PersistWindowState();
            return;
        }

        e.Cancel = true;
        PersistWindowState();
        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PersistWindowState();
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

    private void PersistWindowState()
    {
        if (_shellState is null || _shellStateService is null)
        {
            return;
        }

        PersistListWidth();
        _shellState.PackagesWindowPlacement = ShellWindowPlacementService.Capture(this, _shellState.PackagesWindowPlacement);
        _shellStateService.Save(_shellState);
    }

    private void PersistListWidth()
    {
        if (_shellState is null || PackagesListPane.Bounds.Width <= 0)
        {
            return;
        }

        _shellState.PackagesSidebarWidth = Math.Clamp(PackagesListPane.Bounds.Width, 180, 900);
    }
}
