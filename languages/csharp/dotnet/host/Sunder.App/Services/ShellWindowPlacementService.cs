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

        window.WindowStartupLocation = WindowStartupLocation.Manual;
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
        // A saved window may move from a larger screen to a working area below
        // its old minimum. The minimum must not undo the visible-screen clamp.
        window.MinWidth = Math.Min(window.MinWidth, size.Width);
        window.MinHeight = Math.Min(window.MinHeight, size.Height);
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

        window.WindowState = placement.IsFullScreen ? WindowState.FullScreen
            : placement.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    public static ShellWindowPlacement? Capture(Window window, ShellWindowPlacement? previousPlacement = null)
    {
        var width = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        var height = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        return Capture(
            new ShellWindowPlacementSnapshot(
                window.Position.X,
                window.Position.Y,
                width,
                height,
                window.MinWidth,
                window.MinHeight,
                window.WindowState),
            previousPlacement);
    }

    internal static ShellWindowPlacement? Capture(
        ShellWindowPlacementSnapshot window,
        ShellWindowPlacement? previousPlacement = null)
    {
        if (window.State is WindowState.FullScreen or WindowState.Maximized)
        {
            // Non-normal dimensions are never normal restore bounds.
            return previousPlacement is null ? null : new ShellWindowPlacement
            {
                X = previousPlacement.X, Y = previousPlacement.Y,
                Width = previousPlacement.Width, Height = previousPlacement.Height,
                IsMaximized = window.State == WindowState.Maximized,
                IsFullScreen = window.State == WindowState.FullScreen,
            };
        }

        if (window.State == WindowState.Minimized && previousPlacement is not null)
        {
            return previousPlacement;
        }

        return new ShellWindowPlacement
        {
            X = window.X,
            Y = window.Y,
            Width = Math.Max(window.MinWidth, window.Width),
            Height = Math.Max(window.MinHeight, window.Height),
            IsMaximized = window.State == WindowState.Maximized,
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

internal readonly record struct ShellWindowPlacementSnapshot(
    double X,
    double Y,
    double Width,
    double Height,
    double MinWidth,
    double MinHeight,
    WindowState State);

internal sealed class ShellWindowPlacementTracker : IDisposable
{
    private readonly Window _window;
    private ShellWindowPlacement? _lastWindowedPlacement;
    private bool _awaitingNormalResize;

    public ShellWindowPlacementTracker(Window window, ShellWindowPlacement? initialPlacement)
    {
        _window = window;
        _lastWindowedPlacement = initialPlacement ?? ShellWindowPlacementService.Capture(window);
        _window.PositionChanged += OnPositionChanged;
        _window.Resized += OnResized;
        _window.PropertyChanged += OnPropertyChanged;
    }

    public ShellWindowPlacement? Capture()
    {
        RecordWindowedPlacement();
        return _lastWindowedPlacement;
    }

    public void RecordWindowedPlacement()
    {
        if (_awaitingNormalResize) return;
        _lastWindowedPlacement = ShellWindowPlacementService.Capture(
            _window,
            _lastWindowedPlacement);
    }

    internal void Reset(ShellWindowPlacement? placement)
    {
        _lastWindowedPlacement = placement;
        _awaitingNormalResize = false;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => RecordWindowedPlacement();
    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        _awaitingNormalResize = false;
        RecordWindowedPlacement();
    }
    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.BoundsProperty && _window.WindowState == WindowState.Normal)
        {
            _awaitingNormalResize = false;
            RecordWindowedPlacement();
            return;
        }
        if (e.Property != Window.WindowStateProperty) return;
        _awaitingNormalResize = _window.WindowState == WindowState.Normal;
        if (_awaitingNormalResize && _lastWindowedPlacement is { } previous)
        {
            // WindowState can change before the native normal-bounds resize arrives.
            _lastWindowedPlacement = new ShellWindowPlacement
            {
                X = previous.X, Y = previous.Y, Width = previous.Width, Height = previous.Height,
            };
        }
        RecordWindowedPlacement();
    }

    public void Dispose()
    {
        _window.PositionChanged -= OnPositionChanged;
        _window.Resized -= OnResized;
        _window.PropertyChanged -= OnPropertyChanged;
    }
}
