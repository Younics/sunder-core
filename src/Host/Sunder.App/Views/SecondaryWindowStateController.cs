using Avalonia;
using Avalonia.Controls;
using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Views;

internal sealed class SecondaryWindowStateController(
    Window window,
    ShellStateService? shellStateService,
    ShellState? shellState,
    ColumnDefinition sidebarColumn,
    Control sidebarPane,
    Func<ShellState, double> getSidebarWidth,
    Action<ShellState, double> setSidebarWidth,
    Func<ShellState, ShellWindowPlacement?> getWindowPlacement,
    Action<ShellState, ShellWindowPlacement?> setWindowPlacement)
{
    private const double MinimumSidebarWidth = 180;
    private const double MaximumSidebarWidth = 900;

    public void ApplyInitialWindowState()
    {
        if (shellState is null)
        {
            return;
        }

        ApplySidebarWidth();
        ShellWindowPlacementService.Apply(window, getWindowPlacement(shellState));
    }

    public void ApplySidebarWidth()
    {
        if (shellState is not null)
        {
            sidebarColumn.Width = new GridLength(getSidebarWidth(shellState));
        }
    }

    public void PersistSidebarWidth()
    {
        if (shellState is null || sidebarPane.Bounds.Width <= 0)
        {
            return;
        }

        setSidebarWidth(shellState, Math.Clamp(sidebarPane.Bounds.Width, MinimumSidebarWidth, MaximumSidebarWidth));
    }

    public void PersistWindowState()
    {
        if (shellState is null || shellStateService is null)
        {
            return;
        }

        PersistSidebarWidth();
        setWindowPlacement(shellState, ShellWindowPlacementService.Capture(window, getWindowPlacement(shellState)));
        shellStateService.Save(shellState);
    }
}
