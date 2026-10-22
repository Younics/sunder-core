using System.Runtime.InteropServices;
using Avalonia;

namespace Sunder.App.Services;

internal sealed class MacOsWebViewClip(nint view) : IAppNativeWebViewClip
{
    private readonly List<nint> _masks = [];

    public bool TryApply(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions, double scaling)
    {
        Native.SetBoolean(view, Native.Selector("setWantsLayer:"), true);
        var layer = Native.Send(view, Native.Selector("layer"));
        if (layer == 0) return false;
        var masks = new List<nint>();
        var transaction = Native.GetClass("CATransaction");
        Native.Send(transaction, Native.Selector("begin"));
        Native.SetBoolean(transaction, Native.Selector("setDisableActions:"), true);
        try
        {
            // AppKit owns the view/layer. Retain only our mask objects, not WKWebView
            // or its WebContent process. AdapterDestroyed may follow view destruction.
            var flipped = Native.GetBoolean(layer, Native.Selector("isGeometryFlipped"));
            foreach (var region in regions)
            {
                var shape = Native.Send(Native.Send(Native.GetClass("CAShapeLayer"), Native.Selector("alloc")), Native.Selector("init"));
                if (shape == 0) return false;
                masks.Add(shape);
                Native.SetRect(shape, Native.Selector("setFrame:"), new(0, 0, size.Width, size.Height));
                Native.SetDouble(shape, Native.Selector("setContentsScale:"), scaling);
                var path = CreatePath(region, size.Height, flipped);
                if (path == 0) return false;
                try { Native.SetPointer(shape, Native.Selector("setPath:"), path); }
                finally { Native.ReleasePath(path); }
                if (masks.Count > 1) Native.SetPointer(masks[^2], Native.Selector("setMask:"), shape);
            }
            Native.SetBoolean(layer, Native.Selector("setMasksToBounds:"), true);
            Native.SetPointer(layer, Native.Selector("setMask:"), masks.Count > 0 ? masks[0] : 0);
            ReleaseMasks(_masks);
            _masks.AddRange(masks);
            masks.Clear();
            return true;
        }
        finally
        {
            Native.Send(transaction, Native.Selector("commit"));
            ReleaseMasks(masks);
        }
    }

    private static nint CreatePath(AppNativeWebViewClipRegion region, double height, bool flipped)
    {
        const double k = 0.5522847498307936;
        var path = Native.CreatePath();
        if (path == 0) return 0;
        var b = region.Bounds;
        var r = region.Radius;
        double Y(double y) => flipped ? y : height - y;
        void Line(double x, double y) => Native.Line(path, 0, x, Y(y));
        void Curve(double x1, double y1, double x2, double y2, double x, double y)
            => Native.Curve(path, 0, x1, Y(y1), x2, Y(y2), x, Y(y));
        Native.Move(path, 0, b.Left + r.TopLeft, Y(b.Top));
        Line(b.Right - r.TopRight, b.Top);
        Curve(b.Right - r.TopRight * (1 - k), b.Top, b.Right, b.Top + r.TopRight * (1 - k), b.Right, b.Top + r.TopRight);
        Line(b.Right, b.Bottom - r.BottomRight);
        Curve(b.Right, b.Bottom - r.BottomRight * (1 - k), b.Right - r.BottomRight * (1 - k), b.Bottom, b.Right - r.BottomRight, b.Bottom);
        Line(b.Left + r.BottomLeft, b.Bottom);
        Curve(b.Left + r.BottomLeft * (1 - k), b.Bottom, b.Left, b.Bottom - r.BottomLeft * (1 - k), b.Left, b.Bottom - r.BottomLeft);
        Line(b.Left, b.Top + r.TopLeft);
        Curve(b.Left, b.Top + r.TopLeft * (1 - k), b.Left + r.TopLeft * (1 - k), b.Top, b.Left + r.TopLeft, b.Top);
        Native.Close(path);
        return path;
    }

    public void Dispose() => ReleaseMasks(_masks);

    private static void ReleaseMasks(List<nint> masks)
    {
        foreach (var mask in masks) Native.Send(mask, Native.Selector("release"));
        masks.Clear();
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct NativeRect(double X, double Y, double Width, double Height);
        [DllImport(ObjC, EntryPoint = "objc_getClass")] internal static extern nint GetClass(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] internal static extern nint Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool GetBoolean(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SetBoolean(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SetPointer(nint receiver, nint selector, nint value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SetDouble(nint receiver, nint selector, double value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern void SetRect(nint receiver, nint selector, NativeRect value);
        [DllImport(Graphics, EntryPoint = "CGPathCreateMutable")] internal static extern nint CreatePath();
        [DllImport(Graphics, EntryPoint = "CGPathMoveToPoint")] internal static extern void Move(nint path, nint transform, double x, double y);
        [DllImport(Graphics, EntryPoint = "CGPathAddLineToPoint")] internal static extern void Line(nint path, nint transform, double x, double y);
        [DllImport(Graphics, EntryPoint = "CGPathAddCurveToPoint")] internal static extern void Curve(nint path, nint transform, double x1, double y1, double x2, double y2, double x, double y);
        [DllImport(Graphics, EntryPoint = "CGPathCloseSubpath")] internal static extern void Close(nint path);
        [DllImport(Graphics, EntryPoint = "CGPathRelease")] internal static extern void ReleasePath(nint path);
    }
}
