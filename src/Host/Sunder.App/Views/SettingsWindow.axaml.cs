using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class SettingsWindow : Window
{
    private SettingsWindowViewModel? ViewModel => DataContext as SettingsWindowViewModel;
    private readonly ShellStateService? _shellStateService;
    private readonly ShellState? _shellState;
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public SettingsWindow(ShellStateService shellStateService, ShellState shellState)
        : this()
    {
        _shellStateService = shellStateService;
        _shellState = shellState;
        SettingsContentGrid.ColumnDefinitions[0].Width = new GridLength(shellState.SettingsSidebarWidth);
        ShellWindowPlacementService.Apply(this, shellState.SettingsWindowPlacement);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private async void SectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsSectionItemViewModel item } && ViewModel is not null)
        {
            await ViewModel.SelectSectionAsync(item);
        }
    }

    private async void ApplyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ApplyAsync();
        }
    }

    private async void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.SaveAsync();
        }

        Close();
    }

    private async void InstallOrRepairCliButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.InstallOrRepairCliAsync();
        }
    }

    private async void RefreshCliStatusButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshCliStatusAsync();
        }
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

    private async void UninstallCliButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.UninstallCliAsync();
        }
    }

    private async void CheckForUpdatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.CheckForAppUpdatesAsync();
        }
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        PersistSidebarWidth();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_shellState is not null)
        {
            SettingsContentGrid.ColumnDefinitions[0].Width = new GridLength(_shellState.SettingsSidebarWidth);
        }
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
            if (ancestors.Any(x => x is Button or TextBox or ComboBox or CheckBox))
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
        ViewModel?.Dispose();
        DataContext = null;
    }

    private void PersistWindowState()
    {
        if (_shellState is null || _shellStateService is null)
        {
            return;
        }

        PersistSidebarWidth();
        _shellState.SettingsWindowPlacement = ShellWindowPlacementService.Capture(this, _shellState.SettingsWindowPlacement);
        _shellStateService.Save(_shellState);
    }

    private void PersistSidebarWidth()
    {
        if (_shellState is null || SettingsSidebarPane.Bounds.Width <= 0)
        {
            return;
        }

        _shellState.SettingsSidebarWidth = Math.Clamp(SettingsSidebarPane.Bounds.Width, 180, 900);
    }
}
