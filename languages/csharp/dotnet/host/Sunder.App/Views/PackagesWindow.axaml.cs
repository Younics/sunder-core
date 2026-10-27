using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
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
    private bool _compactLayout;
    private bool _showCompactDetails = true;
    private GridLength _wideSidebarWidth = new(430);
    private readonly Dictionary<string, Vector> _detailOffsets = new(StringComparer.Ordinal);
    private string? _detailKey;
    private Vector? _pendingDetailOffset;

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
        PackagesContentGrid.SizeChanged += (_, _) => ApplyResponsiveLayout();
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
        ApplyResponsiveLayout();

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
            _subscribedViewModel.PropertyChanged -= PackageDetails_OnPropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.MarketplaceImageGalleryRequested += ShowMarketplaceImageGalleryAsync;
            _subscribedViewModel.PropertyChanged += PackageDetails_OnPropertyChanged;
        }
    }

    private void PackageDetails_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } model) return;
        if (e.PropertyName is nameof(model.SelectedInstalledPackage) or nameof(model.SelectedMarketplacePackage) or nameof(model.SelectedMarketplaceVersion) or nameof(model.Mode))
        {
            var key = model.IsInstalledMode ? $"local:{model.SelectedInstalledPackage?.PackageId}:{model.SelectedInstalledPackage?.Version}"
                : $"market:{model.RegistryUrlText}:{model.SelectedMarketplacePackage?.PackageId}:{model.SelectedMarketplaceVersion?.Version}";
            if (_detailKey != key)
            {
                if (_detailKey is not null)
                {
                    if (_detailOffsets.Count >= 128) _detailOffsets.Clear();
                    _detailOffsets[_detailKey] = PackageDetailsScroll.Offset;
                }
                _detailKey = key;
                _pendingDetailOffset = _detailOffsets.GetValueOrDefault(key);
                PackageDetailsScroll.Offset = default;
            }
            _showCompactDetails = true;
            ApplyResponsiveLayout();
        }
        if (e.PropertyName == nameof(model.Information) && model.Information.IsLoaded && _pendingDetailOffset is { } offset)
        {
            var key = _detailKey;
            _pendingDetailOffset = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (_detailKey == key) PackageDetailsScroll.Offset = offset;
            }, DispatcherPriority.Loaded);
        }
    }

    private void ApplyResponsiveLayout()
    {
        if (PackagesContentGrid.Bounds.Width <= 0) return;
        var compact = PackagesContentGrid.Bounds.Width < 850;
        if (compact != _compactLayout)
        {
            if (compact) _wideSidebarWidth = PackagesContentGrid.ColumnDefinitions[0].Width;
            else PackagesContentGrid.ColumnDefinitions[0].Width = _wideSidebarWidth;
            _compactLayout = compact;
        }
        PackagesListPane.IsVisible = !compact || !_showCompactDetails;
        PackagesDetailPane.IsVisible = !compact || _showCompactDetails;
        PackagesSplitter.IsVisible = !compact;
        CompactPackageBackButton.IsVisible = compact;
        PackagesContentGrid.ColumnDefinitions[0].MaxWidth = compact ? 900 : Math.Min(900, Math.Max(240, PackagesContentGrid.Bounds.Width - 364));
        PackagesContentGrid.ColumnDefinitions[2].MinWidth = compact ? 0 : 360;
        if (compact)
        {
            PackagesContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            PackagesContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
            PackagesContentGrid.ColumnDefinitions[2].Width = new GridLength(0);
            Grid.SetColumn(PackagesDetailPane, 0);
            PackagesDetailPane.Margin = new Thickness(0);
        }
        else
        {
            PackagesContentGrid.ColumnDefinitions[1].Width = new GridLength(4);
            PackagesContentGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(PackagesDetailPane, 2);
            PackagesDetailPane.Margin = new Thickness(16);
        }
    }

    private void CompactPackageBackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _showCompactDetails = false;
        ApplyResponsiveLayout();
    }

    private void PackageList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not ListBox { SelectedItem: not null }) return;
        _showCompactDetails = true;
        ApplyResponsiveLayout();
    }

    private void PackageList_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBox { SelectedItem: not null }) return;
        _showCompactDetails = true;
        ApplyResponsiveLayout();
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
