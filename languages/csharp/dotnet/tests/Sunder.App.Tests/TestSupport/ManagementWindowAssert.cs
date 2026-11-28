using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App.Services;
using Sunder.App.Themes;
using Xunit;

namespace Sunder.App.Tests.TestSupport;

internal static class ManagementWindowAssert
{
    public static async Task CheckLayoutsAsync(Window window, Action? checkContent = null, string? state = null,
        Action<SunderThemeDefinition>? selectTheme = null)
    {
        var themes = new ThemeManager(Application.Current!);
        foreach (var theme in themes.AvailableThemes)
        {
            if (selectTheme is null) themes.ApplyTheme(theme.Id);
            else selectTheme(theme);
            foreach (var width in new[] { 1040, 360 })
            {
                window.Width = width;
                window.Height = width == 360 ? 580 : 760;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Assert.Equal(width, window.Bounds.Width);
                Assert.Equal(theme.Variant, window.ActualThemeVariant);
                var footer = window.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("window-footer"));
                Assert.Same(Application.Current!.Resources["Sunder.Brush.Surface.Base"], footer.Background);
                foreach (var button in footer.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
                {
                    var origin = button.TranslatePoint(default, window)!.Value;
                    Assert.True(origin.X >= 0 && origin.Y >= 0 && origin.X + button.Bounds.Width <= width + 1
                        && origin.Y + button.Bounds.Height <= window.Bounds.Height + 1,
                        $"{window.Title}: footer action '{button.Content}' is outside the window at {origin}, {button.Bounds.Size}.");
                }
                foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(scroll =>
                             scroll.IsEffectivelyVisible && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                             && !scroll.GetVisualAncestors().OfType<ScrollViewer>().Any()))
                {
                    Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                        $"{window.Title} at {width}: {scroll.Name} extent {scroll.Extent}, viewport {scroll.Viewport}.");
                }
                checkContent?.Invoke();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("SUNDER_LAYOUT_CAPTURE_DIRECTORY") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var name = string.Join("-", new[] { window.GetType().Name, state, theme.Id, width.ToString() }.OfType<string>());
                    frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
                }
            }
        }
    }

    public static void Click(Window window, Control control)
    {
        control.BringIntoView();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Assert.True(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && new Rect(window.Bounds.Size).Contains(center));
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        window.UpdateLayout();
    }
}
