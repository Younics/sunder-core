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
    private readonly ListDetailLayout _layout;
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
        _layout = new(PackagesContentGrid, PackagesListPane, PackagesDetailPane, PackagesSplitter,
            CompactPackageBackButton, () => ViewModel is not { ShowNoSelection: true, CanGoBackInPackageDetails: false });
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

    internal void ShowDetails() => _layout.OpenDetails();

    private void OnOpened(object? sender, EventArgs e)
    {
        _stateController?.ApplySidebarWidth();
        _stateController?.RecordWindowedPlacement();
        _layout.Refresh();

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
        if (e.PropertyName == nameof(model.Mode) || (model.IsInstalledMode
                ? e.PropertyName == nameof(model.SelectedInstalledPackage)
                : e.PropertyName is nameof(model.SelectedMarketplacePackage) or nameof(model.SelectedMarketplaceVersion)))
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
            _layout.Refresh();
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

    private void PackageList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        => _layout.OpenSelectedRow(sender, e);

    private void PackageList_OnKeyUp(object? sender, KeyEventArgs e)
        => _layout.OpenSelectedRow(sender, e);

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
