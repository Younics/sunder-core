using Avalonia;
using Avalonia.Controls;
using Sunder.App.Models;

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

        var width = Math.Max(window.MinWidth, placement.Width);
        var height = Math.Max(window.MinHeight, placement.Height);
        window.Width = width;
        window.Height = height;

        if (IsValidPosition(placement.X, placement.Y))
        {
            var bounds = ClampToVisibleScreen(window, new PixelRect(
                (int)Math.Round(placement.X),
                (int)Math.Round(placement.Y),
                (int)Math.Round(width),
                (int)Math.Round(height)));
            window.Position = new PixelPoint(bounds.X, bounds.Y);
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

    private static PixelRect ClampToVisibleScreen(Window window, PixelRect bounds)
    {
        var screen = window.Screens.ScreenFromBounds(bounds) ?? window.Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return bounds;
        }

        var area = screen.WorkingArea;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Clamp(bounds.X, area.X, Math.Max(area.X, area.Right - width));
        var y = Math.Clamp(bounds.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
        return new PixelRect(x, y, width, height);
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
