using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class StacksWindow : Window
{
    private StacksWindowViewModel? ViewModel => DataContext as StacksWindowViewModel;
    private readonly SecondaryWindowStateController? _stateController;
    private readonly SecondaryWindowLifecycleController _lifecycleController;
    private readonly ShellStateService? _shellStateService;
    private readonly ShellState? _shellState;
    private StacksWindowViewModel? _subscribedViewModel;

    public StacksWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplySecondaryWindowSize(this);
        _lifecycleController = new SecondaryWindowLifecycleController(
            this,
            () => _stateController?.PersistWindowState(),
            OnLifecycleClosed);
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
    }

    public StacksWindow(ShellStateService shellStateService, ShellState shellState)
        : this()
    {
        _shellStateService = shellStateService;
        _shellState = shellState;
        _stateController = new SecondaryWindowStateController(
            this,
            shellStateService,
            shellState,
            StacksContentGrid.ColumnDefinitions[0],
            StacksListPane,
            state => state.StacksSidebarWidth,
            (state, width) => state.StacksSidebarWidth = width,
            state => state.StacksWindowPlacement,
            (state, placement) => state.StacksWindowPlacement = placement);
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

    private void StacksSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        _stateController?.PersistSidebarWidth();
    }

    private async void CreateStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var wizardWindow = new CreateStackWizardWindow();
        ApplyCreateStackWizardPlacement(wizardWindow);
        var wizardViewModel = ViewModel.CreateCreateStackWizardViewModel();
        wizardWindow.DataContext = wizardViewModel;
        var result = await wizardWindow.ShowDialog<bool?>(this);
        if (result == true)
        {
            await ViewModel.RefreshAfterCreatedStackAsync(wizardViewModel.CreatedStackId);
        }
    }

    private async void ImportStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ImportStackWithPickerAsync();
        }
    }

    private async void UseRegistryStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null || !await ViewModel.ImportSelectedRegistryStackAsLocalAsync(AppLaunchRequestKind.StackUse))
        {
            return;
        }

        await ShowUseStackWizardAsync();
    }

    private async void UseLocalStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ShowUseStackWizardAsync();
    }

    private async Task ShowUseStackWizardAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var wizardViewModel = ViewModel.CreateUseStackWizardViewModel();
        if (wizardViewModel is null)
        {
            return;
        }

        var wizardWindow = new UseStackWizardWindow
        {
            DataContext = wizardViewModel,
        };
        ApplyUseStackWizardPlacement(wizardWindow);
        var result = await wizardWindow.ShowDialog<bool?>(this);
        if (result == true)
        {
            await ViewModel.RefreshAfterUsedStackAsync();
        }
    }

    private void OnLifecycleClosed()
    {
        SubscribeToViewModel(null);
        ViewModel?.Dispose();
        DataContext = null;
    }

    private void ApplyCreateStackWizardPlacement(Window wizardWindow)
        => ApplyStackWizardPlacement(
            wizardWindow,
            state => state.CreateStackWizardWindowPlacement,
            (state, placement) => state.CreateStackWizardWindowPlacement = placement);

    private void ApplyUseStackWizardPlacement(Window wizardWindow)
        => ApplyStackWizardPlacement(
            wizardWindow,
            state => state.UseStackWizardWindowPlacement,
            (state, placement) => state.UseStackWizardWindowPlacement = placement);

    private void ApplyStackWizardPlacement(
        Window wizardWindow,
        Func<ShellState, ShellWindowPlacement?> getPlacement,
        Action<ShellState, ShellWindowPlacement?> setPlacement)
    {
        if (_shellState is null || _shellStateService is null)
        {
            return;
        }

        ShellWindowPlacementService.Apply(wizardWindow, getPlacement(_shellState));
        wizardWindow.Closing += (_, _) =>
        {
            setPlacement(_shellState, ShellWindowPlacementService.Capture(wizardWindow, getPlacement(_shellState)));
            _shellStateService.Save(_shellState);
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => SubscribeToViewModel(ViewModel);

    private void SubscribeToViewModel(StacksWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.LocalImageGalleryRequested -= ShowImageGalleryAsync;
            _subscribedViewModel.RegistryImageGalleryRequested -= ShowRegistryImageGalleryAsync;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.LocalImageGalleryRequested += ShowImageGalleryAsync;
            _subscribedViewModel.RegistryImageGalleryRequested += ShowRegistryImageGalleryAsync;
        }
    }

    private async Task ShowRegistryImageGalleryAsync(
        IReadOnlyList<RegistryPackageMediaItemViewModel> media,
        int selectedIndex)
        => await ShowImageGalleryAsync(media, selectedIndex);

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
