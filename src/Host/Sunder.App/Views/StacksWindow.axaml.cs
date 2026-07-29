using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Sunder.App.Models;
using Sunder.App.Composition;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class StacksWindow : Window
{
    private StacksWindowViewModel? ViewModel => DataContext as StacksWindowViewModel;
    private readonly SecondaryWindowStateController? _stateController;
    private readonly WindowCloseToHideCoordinator _closeCoordinator;
    private readonly StackWizardWindowFactory? _stackWizardWindowFactory;
    private readonly OwnedTaskObserver _tasks = new(nameof(StacksWindow));
    private readonly CancellationTokenSource _lifetime = new();
    private StacksWindowViewModel? _subscribedViewModel;

    public StacksWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplySecondaryWindowSize(this);
        _closeCoordinator = new WindowCloseToHideCoordinator(
            this,
            hideOnClose: true,
            closeOnEscape: true,
            persistWindowState: () => _stateController?.PersistWindowState(),
            closed: OnLifecycleClosed);
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
    }

    public StacksWindow(
        ShellStateService shellStateService,
        ShellState shellState,
        StackWizardWindowFactory stackWizardWindowFactory)
        : this()
    {
        _stackWizardWindowFactory = stackWizardWindowFactory;
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
        => _closeCoordinator.CloseForShutdown();

    private void OnOpened(object? sender, EventArgs e)
    {
        _stateController?.ApplySidebarWidth();
        _stateController?.RecordWindowedPlacement();

        if (ViewModel is not null)
        {
            _tasks.Observe(ViewModel.InitializeAsync(_lifetime.Token), "initializing Stacks");
        }
    }

    private void StacksSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        _stateController?.PersistSidebarWidth();
    }

    private void CreateStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var wizardViewModel = ViewModel.CreateCreateStackWizardViewModel();
        var wizardWindow = RequireWizardFactory().Create(wizardViewModel);
        _tasks.Observe(ShowCreateWizardAsync(wizardWindow, wizardViewModel, isEdit: false), "showing Create Stack wizard");
    }

    private void EditStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var wizardViewModel = ViewModel.CreateEditStackWizardViewModel();
        if (wizardViewModel is null)
        {
            return;
        }

        var wizardWindow = RequireWizardFactory().Create(wizardViewModel);
        _tasks.Observe(ShowCreateWizardAsync(wizardWindow, wizardViewModel, isEdit: true), "showing Edit Stack wizard");
    }

    private void ImportStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            _tasks.Observe(ViewModel.ImportStackWithPickerAsync(_lifetime.Token), "importing a Stack");
        }
    }

    private void UseRegistryStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _tasks.Observe(ImportAndShowUseStackWizardAsync(), "opening a Registry Stack");
    }

    private void UseLocalStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _tasks.Observe(ShowUseStackWizardAsync(), "showing Use Stack wizard");
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

        var wizardWindow = RequireWizardFactory().Create(wizardViewModel);
        var result = await wizardWindow.ShowDialog<bool?>(this);
        if (result == true)
        {
            await ViewModel.RefreshAfterUsedStackAsync();
        }
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

    private async Task ShowCreateWizardAsync(
        CreateStackWizardWindow window,
        CreateStackWizardViewModel viewModel,
        bool isEdit)
    {
        var result = await window.ShowDialog<bool?>(this);
        if (result == true && ViewModel is not null)
        {
            if (isEdit)
            {
                await ViewModel.RefreshAfterEditedStackAsync(viewModel.CreatedStackId);
            }
            else
            {
                await ViewModel.RefreshAfterCreatedStackAsync(viewModel.CreatedStackId);
            }
        }
    }

    private async Task ImportAndShowUseStackWizardAsync()
    {
        if (ViewModel is not null
            && await ViewModel.ImportSelectedRegistryStackAsLocalAsync(AppLaunchRequestKind.StackUse, _lifetime.Token))
        {
            await ShowUseStackWizardAsync();
        }
    }

    private StackWizardWindowFactory RequireWizardFactory()
        => _stackWizardWindowFactory
           ?? throw new InvalidOperationException("Stack wizard windows must be created by the composition root.");

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
