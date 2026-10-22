using System.Runtime.InteropServices;
using Avalonia;

namespace Sunder.App.Services;

internal sealed class X11WebViewClip(nint display, nint window, bool inputShape) : IAppNativeWebViewClip
{
    private nint _display = display;
    internal static X11WebViewClip? TryCreate(nint window)
    {
        var display = Native.XOpenDisplay(0);
        if (display == 0) return null;
        try
        {
            if (Native.XShapeQueryExtension(display, out _, out _) == 0) return null;
            Native.XShapeQueryVersion(display, out var major, out var minor);
            var clip = new X11WebViewClip(display, window, major > 1 || major == 1 && minor >= 1);
            display = 0;
            return clip;
        }
        finally { if (display != 0) Native.XCloseDisplay(display); }
    }

    public bool TryApply(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions, double scaling)
    {
        var connection = Volatile.Read(ref _display);
        if (connection == 0) return false;
        var rectangles = AppNativeWebViewClip.PixelRectangles(size, regions, scaling)
            .Select(rect => new Native.XRectangle((short)rect.X, (short)rect.Y, (ushort)rect.Width, (ushort)rect.Height)).ToArray();
        Native.XShapeCombineRectangles(connection, window, 0 /* ShapeBounding */, 0, 0, rectangles, rectangles.Length, 0 /* ShapeSet */, 0);
        if (inputShape) Native.XShapeCombineRectangles(connection, window, 2 /* ShapeInput */, 0, 0, rectangles, rectangles.Length, 0, 0);
        Native.XFlush(connection);
        return true;
    }

    public void Dispose()
    {
        var connection = Interlocked.Exchange(ref _display, 0);
        if (connection != 0) Native.XCloseDisplay(connection); // Own connection only; GTK owns its XID.
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct XRectangle(short X, short Y, ushort Width, ushort Height);
        [DllImport("libX11.so.6")] internal static extern nint XOpenDisplay(nint name);
        [DllImport("libX11.so.6")] internal static extern int XCloseDisplay(nint display);
        [DllImport("libX11.so.6")] internal static extern int XFlush(nint display);
        [DllImport("libXext.so.6")] internal static extern int XShapeQueryExtension(nint display, out int eventBase, out int errorBase);
        [DllImport("libXext.so.6")] internal static extern int XShapeQueryVersion(nint display, out int major, out int minor);
        [DllImport("libXext.so.6")] internal static extern void XShapeCombineRectangles(nint display, nint window, int kind, int x, int y,
            [In] XRectangle[] rectangles, int count, int operation, int ordering);
    }
}
