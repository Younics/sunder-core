using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace Sunder.App.Services;

// GTK owns its GLib thread. Submit there, finish there, and retain the native
// view until the real callback settles. No window/desktop readback is involved.
internal static class GtkWebViewCapture
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SourceCallback(nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadyCallback(nint source, nint result, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteCallback(nint data, nint bytes, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Snapshot(nint view, int region, int options, nint cancel, ReadyCallback callback, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Finish(nint view, nint result, out nint error);
    private static readonly SourceCallback StartCallback = Start;
    private static readonly ReadyCallback CompletedCallback = Complete;
    private static readonly WriteCallback PngCallback = WritePng;

    [SupportedOSPlatform("linux")]
    internal static async Task<byte[]> CaptureAsync(nint view, Rect bounds, Size viewport, int edge)
    {
        AppNativeWebViewCapture.ValidateRegion(bounds, viewport, edge);
        if (view == 0) throw new InvalidOperationException("The native browser is not ready for capture.");
        using var api = Api.Load();
        using var context = new CaptureContext(view, api);
        if (IdleAdd(0, StartCallback, context.Handle, 0) == 0)
            throw new InvalidOperationException("The GTK snapshot could not be scheduled.");
        var png = await context.Completion.Task;
        return AppNativeWebViewCapture.Crop(png, bounds, viewport, edge);
    }

    private static int Start(nint data)
    {
        var context = Context(data);
        try
        {
            ObjectRef(context.View);
            context.Retained = true;
            context.Before = Geometry(context.View);
            AppNativeWebViewCapture.RequirePixels(checked(context.Before.Width * context.Before.Scale), checked(context.Before.Height * context.Before.Scale));
            context.Api.Take(context.View, 0 /* visible viewport */, 0, 0, CompletedCallback, data);
        }
        catch (Exception error) { context.Settle(null, error); }
        return 0;
    }

    private static void Complete(nint source, nint result, nint data)
    {
        var context = Context(data);
        nint surface = 0, nativeError = 0;
        byte[]? png = null;
        Exception? failure = null;
        try
        {
            surface = context.Api.End(context.View, result, out nativeError);
            if (nativeError != 0 || surface == 0 || SurfaceStatus(surface) != 0 || SurfaceType(surface) != 0)
                throw new InvalidOperationException("WebKitGTK could not capture this package viewport.");
            AppNativeWebViewCapture.RequirePixels(ImageWidth(surface), ImageHeight(surface));
            if (context.Before != Geometry(context.View)) throw new InvalidOperationException("The native viewport changed during capture.");
            var status = WriteSurface(surface, PngCallback, data);
            if (context.Stream.LimitExceeded) throw new InvalidDataException("Native image exceeds its 2 MiB capture budget.");
            if (status != 0 || context.WriteFailure is not null)
                throw new InvalidDataException("WebKitGTK could not encode the package image.", context.WriteFailure);
            png = context.Stream.ToArray();
            AppDevelopmentViewOperations.ValidatePng(png);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (surface != 0) SurfaceDestroy(surface);
            if (nativeError != 0) ErrorFree(nativeError);
        }
        context.Settle(png, failure);
    }

    private static int WritePng(nint data, nint bytes, uint length)
    {
        var context = Context(data);
        try
        {
            // Reject before copying a native chunk or growing managed storage.
            if (length > AppNativeWebViewCapture.PngStream.MaximumBytes - context.Stream.Length)
                context.Stream.SetLength(AppNativeWebViewCapture.PngStream.MaximumBytes + 1L);
            var buffer = new byte[checked((int)length)];
            Marshal.Copy(bytes, buffer, 0, buffer.Length);
            context.Stream.Write(buffer);
            return 0;
        }
        catch (Exception error) { context.WriteFailure = error; return 11 /* CAIRO_STATUS_WRITE_ERROR */; }
    }

    private static CaptureContext Context(nint data) => (CaptureContext)GCHandle.FromIntPtr(data).Target!;

    private static (int Width, int Height, int Scale) Geometry(nint view) => (WidgetWidth(view), WidgetHeight(view), WidgetScale(view));

    private sealed class CaptureContext : IDisposable
    {
        private readonly GCHandle _root;
        internal CaptureContext(nint view, Api api) { View = view; Api = api; _root = GCHandle.Alloc(this); }
        internal nint Handle => GCHandle.ToIntPtr(_root);
        internal nint View { get; }
        internal Api Api { get; }
        internal (int Width, int Height, int Scale) Before { get; set; }
        internal bool Retained { get; set; }
        internal AppNativeWebViewCapture.PngStream Stream { get; } = new();
        internal Exception? WriteFailure { get; set; }
        internal TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Settle(byte[]? png, Exception? error)
        {
            if (Retained) { Retained = false; ObjectUnref(View); }
            if (error is not null) Completion.TrySetException(error);
            else Completion.TrySetResult(png!);
        }
        public void Dispose() { _root.Free(); Stream.Dispose(); }
    }

    private sealed class Api(nint library, Snapshot take, Finish end) : IDisposable
    {
        internal Snapshot Take => take;
        internal Finish End => end;
        internal static Api Load()
        {
            // NOLOAD avoids loading a second WebKit/libsoup ABI into the process.
            foreach (var name in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
            {
                var library = OpenLibrary(name, 2 | 4 /* RTLD_NOW | RTLD_NOLOAD */);
                if (library == 0) continue;
                try
                {
                    return new(library,
                        Marshal.GetDelegateForFunctionPointer<Snapshot>(NativeLibrary.GetExport(library, "webkit_web_view_get_snapshot")),
                        Marshal.GetDelegateForFunctionPointer<Finish>(NativeLibrary.GetExport(library, "webkit_web_view_get_snapshot_finish")));
                }
                catch { NativeLibrary.Free(library); throw; }
            }
            throw new InvalidOperationException("The active WebKitGTK library could not be retained for capture.");
        }
        public void Dispose() => NativeLibrary.Free(library);
    }

    [DllImport("libdl.so.2", EntryPoint = "dlopen")] private static extern nint OpenLibrary([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_idle_add_full")] private static extern uint IdleAdd(int priority, SourceCallback callback, nint data, nint destroy);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static extern void ErrorFree(nint error);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")] private static extern nint ObjectRef(nint value);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static extern void ObjectUnref(nint value);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_allocated_width")] private static extern int WidgetWidth(nint view);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_allocated_height")] private static extern int WidgetHeight(nint view);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_scale_factor")] private static extern int WidgetScale(nint view);
    [DllImport("libcairo.so.2", EntryPoint = "cairo_surface_status")] private static extern int SurfaceStatus(nint surface);
    [DllImport("libcairo.so.2", EntryPoint = "cairo_surface_get_type")] private static extern int SurfaceType(nint surface);
    [DllImport("libcairo.so.2", EntryPoint = "cairo_image_surface_get_width")] private static extern int ImageWidth(nint surface);
    [DllImport("libcairo.so.2", EntryPoint = "cairo_image_surface_get_height")] private static extern int ImageHeight(nint surface);
    [DllImport("libcairo.so.2", EntryPoint = "cairo_surface_write_to_png_stream")] private static extern int WriteSurface(nint surface, WriteCallback write, nint data);
    [DllImport("libcairo.so.2", EntryPoint = "cairo_surface_destroy")] private static extern void SurfaceDestroy(nint surface);
}
