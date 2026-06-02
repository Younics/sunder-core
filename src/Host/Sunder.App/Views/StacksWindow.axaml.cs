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

    public StacksWindow()
    {
        InitializeComponent();
        SunderWindowSizing.ApplySecondaryWindowSize(this);
        _lifecycleController = new SecondaryWindowLifecycleController(
            this,
            () => _stateController?.PersistWindowState(),
            OnLifecycleClosed);
        Opened += OnOpened;
    }

    public StacksWindow(ShellStateService shellStateService, ShellState shellState)
        : this()
    {
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
        if (ViewModel is null || !await ViewModel.ImportStackWithPickerAsync())
        {
            return;
        }

        await ShowUseStackWizardAsync();
    }

    private async void UseStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ShowUseStackWizardAsync();
    }

    private async void UseRegistryStackButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is null || !await ViewModel.ImportSelectedRegistryStackAsLocalAsync(AppLaunchRequestKind.StackUse))
        {
            return;
        }

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
        var result = await wizardWindow.ShowDialog<bool?>(this);
        if (result == true)
        {
            await ViewModel.RefreshAfterUsedStackAsync();
        }
    }

    private void OnLifecycleClosed()
    {
        ViewModel?.Dispose();
        DataContext = null;
    }
}
