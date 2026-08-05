using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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
    private readonly WizardDialogLaunchCoordinator _wizardDialogs = new();
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
            closed: OnLifecycleClosed,
            isCloseBlocked: _wizardDialogs.HandleCloseRequest);
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
        PrepareWizardDialogLaunch(sender, e);
        _tasks.Observe(
            _wizardDialogs.RunAsync(
                SuppressWizardOwnerInput,
                DeferWizardDialogLaunchAsync,
                CanShowWizardDialog,
                () => ShowCreateWizardAsync(isEdit: false),
                cancellationToken: _lifetime.Token),
            "showing Create Stack wizard");
    }

    private void EditStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PrepareWizardDialogLaunch(sender, e);
        _tasks.Observe(
            _wizardDialogs.RunAsync(
                SuppressWizardOwnerInput,
                DeferWizardDialogLaunchAsync,
                CanShowWizardDialog,
                () => ShowCreateWizardAsync(isEdit: true),
                cancellationToken: _lifetime.Token),
            "showing Edit Stack wizard");
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
        PrepareWizardDialogLaunch(sender, e);
        _tasks.Observe(
            _wizardDialogs.RunAsync(
                SuppressWizardOwnerInput,
                DeferWizardDialogLaunchAsync,
                CanShowWizardDialog,
                ShowUseStackWizardAsync,
                PrepareRegistryStackUseAsync,
                _lifetime.Token),
            "opening a Registry Stack");
    }

    private void UseLocalStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PrepareWizardDialogLaunch(sender, e);
        _tasks.Observe(
            _wizardDialogs.RunAsync(
                SuppressWizardOwnerInput,
                DeferWizardDialogLaunchAsync,
                CanShowWizardDialog,
                ShowUseStackWizardAsync,
                cancellationToken: _lifetime.Token),
            "showing Use Stack wizard");
    }

    private async Task<Func<Task>?> ShowUseStackWizardAsync()
    {
        var ownerViewModel = ViewModel;
        if (ownerViewModel is null)
        {
            return null;
        }

        var wizardViewModel = ownerViewModel.CreateUseStackWizardViewModel();
        if (wizardViewModel is null)
        {
            return null;
        }

        var wizardWindow = RequireWizardFactory().Create(wizardViewModel);
        var result = await wizardWindow.ShowDialog<bool?>(this);
        return result == true ? ownerViewModel.RefreshAfterUsedStackAsync : null;
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

    private async Task<Func<Task>?> ShowCreateWizardAsync(bool isEdit)
    {
        var ownerViewModel = ViewModel;
        if (ownerViewModel is null)
        {
            return null;
        }
        var viewModel = isEdit
            ? ownerViewModel.CreateEditStackWizardViewModel()
            : ownerViewModel.CreateCreateStackWizardViewModel();
        if (viewModel is null)
        {
            return null;
        }

        var window = RequireWizardFactory().Create(viewModel);
        var result = await window.ShowDialog<bool?>(this);
        if (result != true)
        {
            return null;
        }

        var stackId = viewModel.CreatedStackId;
        return isEdit
            ? () => ownerViewModel.RefreshAfterEditedStackAsync(stackId)
            : () => ownerViewModel.RefreshAfterCreatedStackAsync(stackId);
    }

    private async Task<bool> PrepareRegistryStackUseAsync(CancellationToken cancellationToken)
    {
        var viewModel = ViewModel;
        return viewModel is not null
               && await viewModel.ImportSelectedRegistryStackAsLocalAsync(
                   AppLaunchRequestKind.StackUse,
                   cancellationToken);
    }

    private Action SuppressWizardOwnerInput()
    {
        var wasEnabled = StacksRoot.IsEnabled;
        var wasHitTestVisible = StacksRoot.IsHitTestVisible;
        var focusedControl = FocusManager?.GetFocusedElement() as Control;
        void RestoreOwnerInput()
        {
            try
            {
                StacksRoot.IsEnabled = wasEnabled;
            }
            finally
            {
                try
                {
                    StacksRoot.IsHitTestVisible = wasHitTestVisible;
                }
                finally
                {
                    if (IsVisible
                        && !_lifetime.IsCancellationRequested
                        && FocusManager?.GetFocusedElement() is null
                        && focusedControl is
                        {
                            Focusable: true,
                            IsEffectivelyEnabled: true,
                            IsEffectivelyVisible: true,
                        })
                    {
                        focusedControl.Focus();
                    }
                }
            }
        }

        try
        {
            StacksRoot.IsEnabled = false;
            StacksRoot.IsHitTestVisible = false;
            return RestoreOwnerInput;
        }
        catch
        {
            RestoreOwnerInput();
            throw;
        }
    }

    private static Task DeferWizardDialogLaunchAsync()
        => Dispatcher.UIThread.InvokeAsync(
                static () => { },
                DispatcherPriority.Background)
            .GetTask();

    private bool CanShowWizardDialog()
        => IsVisible
           && !_lifetime.IsCancellationRequested
           && !_closeCoordinator.IsHidePending;

    private static void PrepareWizardDialogLaunch(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control source)
        {
            ToolTip.SetIsOpen(source, false);
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
