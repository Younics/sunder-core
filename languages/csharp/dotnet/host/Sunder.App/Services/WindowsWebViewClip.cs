using System.Runtime.InteropServices;
using Avalonia;

namespace Sunder.App.Services;

internal sealed class WindowsWebViewClip(nint window) : IAppNativeWebViewClip
{
    public bool TryApply(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions, double scaling)
    {
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return false;
        try
        {
            foreach (var rect in AppNativeWebViewClip.PixelRectangles(size, regions, scaling))
            {
                var part = Native.CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
                if (part == 0) return false;
                try { if (Native.CombineRgn(region, region, part, 2 /* RGN_OR */) == 0) return false; }
                finally { Native.DeleteObject(part); }
            }
            if (Native.SetWindowRgn(window, region, true) == 0) return false;
            region = 0; // The HWND now owns the region, including on destruction.
            return true;
        }
        finally { if (region != 0) Native.DeleteObject(region); }
    }

    public void Dispose() { } // Never destroy the WebView's HWND or its transferred region.

    private static class Native
    {
        [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern int CombineRgn(nint destination, nint first, nint second, int mode);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    }
}
