using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppNativeWebViewClipTests
{
    [Fact]
    public void NativeMaskKeepsBrowserEdgeToEdgeAndUsesTheShellsActualRoundedBoundary()
    {
        var browser = new Border();
        var mask = new RecordingClip();
        using var viewport = new AppNativeWebViewViewport(browser, mask);
        var shell = new Border { Child = viewport, CornerRadius = new(18, 8, 24, 4), ClipToBounds = true };
        shell.Classes.Add("shell-panel-content-clip");
        shell.Measure(new Size(300, 200));
        shell.Arrange(new Rect(0, 0, 300, 200));
        // Border commits its Bounds after arranging children. An attached viewport
        // requests this second pass from LayoutUpdated; exercise the settled pass.
        viewport.InvalidateArrange();
        viewport.Arrange(new Rect(0, 0, 300, 200));
        Assert.Equal(new Rect(0, 0, 300, 200), browser.Bounds); // No padding, margin, reflow or smaller native surface.
        Assert.Equal(new Size(300, 200), mask.Size);
        Assert.Equal(AppNativeWebViewClipRegion.Create(new Rect(0, 0, 300, 200), shell.CornerRadius), mask.Regions[^1]);
        Assert.False(mask.Regions[^1].Contains(default));
        Assert.True(mask.Regions[^1].Contains(new Point(150, 0))); // Straight edges still reach the shell edge.
        Assert.True(browser.IsVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNativeMaskUsesContainedFallbackAndNeverLeavesATinyNativeSurface(bool tiny)
    {
        var browser = new Border();
        var mask = new RecordingClip { Succeeds = false };
        using var viewport = new AppNativeWebViewViewport(browser, mask);
        var size = tiny ? new Size(1, 1) : new Size(300, 200);
        viewport.Measure(size);
        viewport.Arrange(new Rect(size));
        var expected = AppNativeWebViewViewport.ContainedRectangle(new Rect(size), 18, 1);
        if (!tiny) Assert.Equal(expected, browser.Bounds);
        Assert.Equal(!tiny, browser.IsVisible);
        Assert.Equal(1, mask.Disposals);
    }

    [Fact]
    public void ReplacingOrDestroyingAdapterReleasesOnlyItsOwnMaskAndKeepsCompositedBrowserFullSize()
    {
        var first = new RecordingClip();
        var second = new RecordingClip();
        var browser = new Border();
        var viewport = new AppNativeWebViewViewport(browser, first);
        viewport.SetNativeClip(second);
        Assert.Equal(1, first.Disposals);
        viewport.SetNativeClip(null);
        Assert.Equal(1, second.Disposals);
        viewport.SetPlatformHandle(new PlatformHandle(1, "Windows.UI.Composition.ContainerVisual"));
        viewport.Measure(new Size(80, 40));
        viewport.Arrange(new Rect(0, 0, 80, 40));
        Assert.Equal(new Rect(0, 0, 80, 40), browser.Bounds);
        // A recreated adapter must not inherit a previous offscreen backend's
        // exemption when the new native handle has no supported mask.
        viewport.SetPlatformHandle(new PlatformHandle(1, "unsupported-native-handle"));
        viewport.Measure(new Size(80, 40));
        viewport.Arrange(new Rect(0, 0, 80, 40));
        Assert.Equal(AppNativeWebViewViewport.ContainedRectangle(new Rect(0, 0, 80, 40), 18, 1), browser.Bounds);
        viewport.Dispose();
        viewport.Dispose();
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.False(browser.IsVisible);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(2)]
    public void PixelRegionsIntersectAllTranslatedAsymmetricRoundedClipsWithoutPaintingOutside(double scaling)
    {
        var size = new Size(100, 60);
        AppNativeWebViewClipRegion[] regions =
        [
            AppNativeWebViewClipRegion.Create(new Rect(size), new(18, 0, 12, 6)),
            AppNativeWebViewClipRegion.Create(new Rect(-4, 3.5, 96, 70), new(5, 20, 3, 8)),
        ];
        var rectangles = AppNativeWebViewClip.PixelRectangles(size, regions, scaling);
        Assert.NotEmpty(rectangles);
        foreach (var rect in rectangles)
        {
            foreach (var point in new[] { new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y),
                new Point(rect.X, rect.Bottom), new Point(rect.Right, rect.Bottom) })
                Assert.All(regions, region => Assert.True(region.Contains(new Point(point.X / scaling, point.Y / scaling))));
        }
        Assert.Contains(rectangles, rect => rect.Contains(new PixelPoint((int)(50 * scaling), (int)(30 * scaling))));
        Assert.DoesNotContain(rectangles, rect => rect.Contains(new PixelPoint(0, 0)));
    }

    [Fact]
    public void CornerNormalizationUsesAdjacentSumsRatherThanShrinkingEveryCornerToHalfTheShortestSide()
    {
        var region = AppNativeWebViewClipRegion.Create(new Rect(0, 0, 100, 40), new(80, 0, 0, 0));
        Assert.Equal(new CornerRadius(40, 0, 0, 0), region.Radius);
        Assert.False(region.Contains(new Point(0, 0)));
        Assert.True(region.Contains(new Point(40, 0)));
        var rectangles = AppNativeWebViewClip.PixelRectangles(new Size(100, 40), [region], 2);
        Assert.Contains(rectangles, rect => rect.Right == 200); // No uniform inset on square edges.
    }

    private sealed class RecordingClip : IAppNativeWebViewClip
    {
        internal Size Size;
        internal AppNativeWebViewClipRegion[] Regions = [];
        internal int Disposals;
        internal bool Succeeds = true;
        public bool TryApply(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions, double scaling)
        { Size = size; Regions = regions.ToArray(); return Succeeds; }
        public void Dispose() => Disposals++;
    }
}
