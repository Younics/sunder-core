using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class SettingsWindow : Window
{
    private SettingsWindowViewModel? ViewModel => DataContext as SettingsWindowViewModel;
    private readonly SecondaryWindowStateController? _stateController;
    private readonly SecondaryWindowLifecycleController _lifecycleController;

    public SettingsWindow()
    {
        InitializeComponent();
        _lifecycleController = new SecondaryWindowLifecycleController(
            this,
            () => _stateController?.PersistWindowState(),
            OnLifecycleClosed);
        Opened += OnOpened;
    }

    public SettingsWindow(ShellStateService shellStateService, ShellState shellState)
        : this()
    {
        _stateController = new SecondaryWindowStateController(
            this,
            shellStateService,
            shellState,
            SettingsContentGrid.ColumnDefinitions[0],
            SettingsSidebarPane,
            state => state.SettingsSidebarWidth,
            (state, width) => state.SettingsSidebarWidth = width,
            state => state.SettingsWindowPlacement,
            (state, placement) => state.SettingsWindowPlacement = placement);
        _stateController.ApplyInitialWindowState();
    }

    public void CloseForShutdown()
        => _lifecycleController.CloseForShutdown();

    private async void SectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsSectionItemViewModel item } && ViewModel is not null)
        {
            await ViewModel.SelectSectionAsync(item);
        }
    }

    private async void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            if (!await ViewModel.SaveAsync())
            {
                return;
            }
        }

        Close();
    }

    private async void CopyCliPathInstructionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.CliPathInstructions))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(ViewModel.CliPathInstructions);
            ViewModel.MarkCliPathInstructionsCopied();
        }
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        _stateController?.PersistSidebarWidth();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _stateController?.ApplySidebarWidth();
    }

    private void OnLifecycleClosed()
    {
        ViewModel?.Dispose();
        DataContext = null;
    }
}
