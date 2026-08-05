using Avalonia.Controls;
using Sunder.App.Models;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellWindowPlacementServiceTests
{
    [Fact]
    public void Capture_FullScreenPreservesLastWindowedBounds()
    {
        var normal = ShellWindowPlacementService.Capture(
            Snapshot(120, 80, 1100, 760, WindowState.Normal));

        var captured = ShellWindowPlacementService.Capture(
            Snapshot(0, 0, 3024, 1964, WindowState.FullScreen),
            normal);

        Assert.Same(normal, captured);
        Assert.Equal(120, captured!.X);
        Assert.Equal(80, captured.Y);
        Assert.Equal(1100, captured.Width);
        Assert.Equal(760, captured.Height);
    }

    [Fact]
    public void Capture_FirstFullScreenPlacementDoesNotPersistFullScreenBounds()
    {
        var captured = ShellWindowPlacementService.Capture(
            Snapshot(0, 0, 3024, 1964, WindowState.FullScreen));

        Assert.Null(captured);
    }

    [Fact]
    public void Capture_ReturnedToNormalUsesRestoredBounds()
    {
        var previous = ShellWindowPlacementService.Capture(
            Snapshot(120, 80, 1100, 760, WindowState.Normal));
        var duringFullScreen = ShellWindowPlacementService.Capture(
            Snapshot(0, 0, 3024, 1964, WindowState.FullScreen),
            previous);

        var restored = ShellWindowPlacementService.Capture(
            Snapshot(220, 140, 1280, 820, WindowState.Normal),
            duringFullScreen);

        Assert.NotSame(previous, restored);
        Assert.Equal(220, restored!.X);
        Assert.Equal(140, restored.Y);
        Assert.Equal(1280, restored.Width);
        Assert.Equal(820, restored.Height);
    }

    private static ShellWindowPlacementSnapshot Snapshot(
        double x,
        double y,
        double width,
        double height,
        WindowState state) =>
        new(x, y, width, height, MinWidth: 640, MinHeight: 480, state);
}
