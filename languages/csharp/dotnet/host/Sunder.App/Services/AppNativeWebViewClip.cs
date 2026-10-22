using Avalonia;
using Avalonia.Platform;

namespace Sunder.App.Services;

// Geometry belongs to the shell, never to package HTML/CSS. Each boundary is in
// browser-local logical coordinates; multiple boundaries are intersected.
internal readonly record struct AppNativeWebViewClipRegion(Rect Bounds, CornerRadius Radius)
{
    internal static AppNativeWebViewClipRegion Create(Rect bounds, CornerRadius radius)
    {
        var horizontal = Math.Max(radius.TopLeft + radius.TopRight, radius.BottomLeft + radius.BottomRight);
        var vertical = Math.Max(radius.TopLeft + radius.BottomLeft, radius.TopRight + radius.BottomRight);
        var factor = Math.Max(0, Math.Min(1, Math.Min(horizontal > 0 ? bounds.Width / horizontal : 1,
            vertical > 0 ? bounds.Height / vertical : 1)));
        return new(bounds, new(radius.TopLeft * factor, radius.TopRight * factor, radius.BottomRight * factor, radius.BottomLeft * factor));
    }

    internal (double Left, double Right) SpanAt(double y)
    {
        if (y < Bounds.Top || y > Bounds.Bottom) return default;
        var left = Bounds.Left;
        var right = Bounds.Right;
        if (Radius.TopLeft > 0 && y < Bounds.Top + Radius.TopLeft)
            left += Inset(Radius.TopLeft, y - Bounds.Top);
        if (Radius.BottomLeft > 0 && y > Bounds.Bottom - Radius.BottomLeft)
            left += Inset(Radius.BottomLeft, Bounds.Bottom - y);
        if (Radius.TopRight > 0 && y < Bounds.Top + Radius.TopRight)
            right -= Inset(Radius.TopRight, y - Bounds.Top);
        if (Radius.BottomRight > 0 && y > Bounds.Bottom - Radius.BottomRight)
            right -= Inset(Radius.BottomRight, Bounds.Bottom - y);
        return (left, right);

        static double Inset(double radius, double distance) => radius - Math.Sqrt(Math.Max(0, radius * radius - Math.Pow(radius - distance, 2)));
    }

    internal bool Contains(Point point)
    {
        var span = SpanAt(point.Y);
        return Bounds.Contains(point) && point.X >= span.Left && point.X <= span.Right;
    }
}

internal interface IAppNativeWebViewClip : IDisposable
{
    bool TryApply(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions, double scaling);
}

internal static class AppNativeWebViewClip
{
    internal static IAppNativeWebViewClip? Create(IPlatformHandle handle)
    {
        if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple && apple.WKWebView != 0)
            return new MacOsWebViewClip(apple.WKWebView);
        if (OperatingSystem.IsWindows() && handle.HandleDescriptor == "HWND" && handle.Handle != 0)
            return new WindowsWebViewClip(handle.Handle);
        if (OperatingSystem.IsLinux() && handle.HandleDescriptor == "XID" && handle.Handle != 0)
            return X11WebViewClip.TryCreate(handle.Handle);
        return null;
    }

    // A native region clips both painting and native input. Coalesce equal rows so
    // only the corner bands need separate rectangles. Round IN, never outside the
    // rounded boundary; inspect both ends of each physical-pixel row.
    internal static IReadOnlyList<PixelRect> PixelRectangles(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions, double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)
            || size.Width * scaling > short.MaxValue || size.Height * scaling > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(size), "Native browser clip dimensions exceed their supported bound.");
        var width = (int)Math.Max(0, Math.Floor(size.Width * scaling));
        var height = (int)Math.Max(0, Math.Floor(size.Height * scaling));
        var rectangles = new List<PixelRect>();
        for (var y = 0; y < height; y++)
        {
            var left = 0d;
            var right = (double)width;
            foreach (var region in regions)
            {
                var top = region.SpanAt(y / scaling);
                var bottom = region.SpanAt((y + 1) / scaling);
                left = Math.Max(left, Math.Max(top.Left, bottom.Left) * scaling);
                right = Math.Min(right, Math.Min(top.Right, bottom.Right) * scaling);
            }
            var x = (int)Math.Ceiling(left);
            var end = (int)Math.Floor(right);
            if (end <= x) continue;
            if (rectangles.Count > 0 && rectangles[^1] is var prior && prior.X == x && prior.Width == end - x && prior.Bottom == y)
                rectangles[^1] = new(prior.X, prior.Y, prior.Width, prior.Height + 1);
            else rectangles.Add(new(x, y, end - x, 1));
        }
        return rectangles;
    }
}
