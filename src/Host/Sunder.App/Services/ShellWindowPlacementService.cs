using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Sunder.App.Models;
using Sunder.App.Views;

namespace Sunder.App.Services;

public static class ShellWindowPlacementService
{
    private const double MinimumWindowWidth = 640;
    private const double MinimumWindowHeight = 480;

    public static void Apply(Window window, ShellWindowPlacement? placement)
    {
        if (placement is null || !IsValidSize(placement.Width, placement.Height))
        {
            return;
        }

        var screen = ResolveScreen(window, placement);
        var size = screen is null
            ? new Size(Math.Max(window.MinWidth, placement.Width), Math.Max(window.MinHeight, placement.Height))
            : SunderWindowSizing.ClampSizeToWorkingArea(
                Math.Max(window.MinWidth, placement.Width),
                Math.Max(window.MinHeight, placement.Height),
                window.MinWidth,
                window.MinHeight,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                screen.Scaling);
        window.Width = size.Width;
        window.Height = size.Height;

        if (screen is not null && IsValidPosition(placement.X, placement.Y))
        {
            window.Position = ClampPositionToVisibleScreen(
                screen,
                (int)Math.Round(placement.X),
                (int)Math.Round(placement.Y),
                size.Width,
                size.Height);
        }

        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public static ShellWindowPlacement Capture(Window window, ShellWindowPlacement? previousPlacement = null)
    {
        if (window.WindowState == WindowState.Minimized && previousPlacement is not null)
        {
            return previousPlacement;
        }

        var width = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        var height = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        return new ShellWindowPlacement
        {
            X = window.Position.X,
            Y = window.Position.Y,
            Width = Math.Max(window.MinWidth, width),
            Height = Math.Max(window.MinHeight, height),
            IsMaximized = window.WindowState == WindowState.Maximized,
        };
    }

    private static Screen? ResolveScreen(Window window, ShellWindowPlacement placement)
    {
        if (IsValidPosition(placement.X, placement.Y))
        {
            var bounds = new PixelRect((int)Math.Round(placement.X), (int)Math.Round(placement.Y), 1, 1);
            return window.Screens.ScreenFromBounds(bounds) ?? window.Screens.All.FirstOrDefault();
        }

        return window.Screens.ScreenFromWindow(window) ?? window.Screens.All.FirstOrDefault();
    }

    private static PixelPoint ClampPositionToVisibleScreen(Screen screen, int x, int y, double width, double height)
    {
        var area = screen.WorkingArea;
        var pixelWidth = Math.Min((int)Math.Ceiling(width * screen.Scaling), area.Width);
        var pixelHeight = Math.Min((int)Math.Ceiling(height * screen.Scaling), area.Height);
        return new PixelPoint(
            Math.Clamp(x, area.X, Math.Max(area.X, area.Right - pixelWidth)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - pixelHeight)));
    }

    private static bool IsValidSize(double width, double height)
        => !double.IsNaN(width)
           && !double.IsInfinity(width)
           && !double.IsNaN(height)
           && !double.IsInfinity(height)
           && width >= MinimumWindowWidth
           && height >= MinimumWindowHeight;

    private static bool IsValidPosition(double x, double y)
        => !double.IsNaN(x)
           && !double.IsInfinity(x)
           && !double.IsNaN(y)
           && !double.IsInfinity(y);
}
