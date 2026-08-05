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
    private readonly WindowCloseToHideCoordinator _closeCoordinator;
    private readonly OwnedTaskObserver _tasks = new("Settings window");

    public SettingsWindow()
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
        Closed += (_, _) => _tasks.Dispose();
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
        => _closeCoordinator.CloseForShutdown();

    private void SectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsSectionItemViewModel item } && ViewModel is not null)
        {
            _tasks.Run(_ => ViewModel.SelectSectionAsync(item), "selecting a settings section");
        }
    }

    private void SaveButton_OnClick(object? sender, RoutedEventArgs e)
        => _tasks.Run(_ => SaveAndCloseAsync(), "saving settings");

    private async Task SaveAndCloseAsync()
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

    private void CopyCliPathInstructionsButton_OnClick(object? sender, RoutedEventArgs e)
        => _tasks.Run(_ => CopyCliPathInstructionsAsync(), "copying CLI path instructions");

    private async Task CopyCliPathInstructionsAsync()
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.Cli.PathInstructions))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(ViewModel.Cli.PathInstructions);
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
        _stateController?.RecordWindowedPlacement();
    }

    private void OnLifecycleClosed()
    {
        ViewModel?.Dispose();
        DataContext = null;
    }
}
