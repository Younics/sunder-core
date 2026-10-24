using System.Runtime.InteropServices;
using Avalonia;

namespace Sunder.App.Services;

// WKWebView snapshots its own surface, never the desktop or a shell window.
internal static class MacOsWebViewCapture
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib", SystemLibrary = "/usr/lib/libSystem.B.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InvokeBlock(nint block, nint image, nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CopyBlock(nint target, nint source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DisposeBlock(nint block);
    private static readonly InvokeBlock Invoke = Complete;
    private static readonly CopyBlock Copy = (target, source) => Context(source).Retain();
    private static readonly DisposeBlock Dispose = block => Context(block).Release();
    private static readonly nint Descriptor = CreateDescriptor();
    private static readonly nint StackBlock = NativeLibrary.GetExport(NativeLibrary.Load(SystemLibrary), "_NSConcreteStackBlock");

    [StructLayout(LayoutKind.Sequential)] private struct Block
    {
        internal nint Isa;
        internal int Flags;
        internal int Reserved;
        internal nint Invoke;
        internal nint Descriptor;
        internal nint Context;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeRect(double X, double Y, double Width, double Height);

    internal static async Task<byte[]> CaptureAsync(nint view, Rect bounds, int edge)
    {
        if (view == 0 || bounds.Width <= 0 || bounds.Height <= 0 || edge is < 1 or > 2048) throw new ArgumentException("Invalid native capture region.");
        var context = new CaptureContext();
        var source = Marshal.AllocHGlobal(Marshal.SizeOf<Block>());
        var configuration = Send(Send(GetClass("WKSnapshotConfiguration"), Selector("alloc")), Selector("init"));
        nint copied = 0;
        try
        {
            SetRect(configuration, Selector("setRect:"), new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            var window = Send(view, Selector("window"));
            var scaling = window == 0 ? 1 : GetDouble(window, Selector("backingScaleFactor"));
            if (!double.IsFinite(scaling) || scaling < 1) scaling = 1;
            var width = SendDouble(GetClass("NSNumber"), Selector("numberWithDouble:"), Math.Min(bounds.Width, edge * bounds.Width / Math.Max(bounds.Width, bounds.Height) / scaling));
            SendPointer(configuration, Selector("setSnapshotWidth:"), width);
            Marshal.StructureToPtr(new Block { Isa = StackBlock, Flags = 1 << 25, Invoke = Marshal.GetFunctionPointerForDelegate(Invoke), Descriptor = Descriptor, Context = context.Handle }, source, false);
            copied = BlockCopy(source);
            if (copied == 0) throw new InvalidOperationException("The native snapshot callback could not be retained.");
            SendTwoPointers(view, Selector("takeSnapshotWithConfiguration:completionHandler:"), configuration, copied);
            return await context.Completion.Task;
        }
        finally
        {
            if (copied != 0) BlockRelease(copied);
            Release(configuration);
            Marshal.FreeHGlobal(source);
            context.Release();
        }
    }

    private static CaptureContext Context(nint block) => (CaptureContext)GCHandle.FromIntPtr(Marshal.PtrToStructure<Block>(block).Context).Target!;
    private static nint CreateDescriptor()
    {
        var memory = Marshal.AllocHGlobal(4 * IntPtr.Size);
        Marshal.WriteIntPtr(memory, 0, 0);
        Marshal.WriteIntPtr(memory, IntPtr.Size, Marshal.SizeOf<Block>());
        Marshal.WriteIntPtr(memory, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(Copy));
        Marshal.WriteIntPtr(memory, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(Dispose));
        return memory;
    }

    private static void Complete(nint block, nint image, nint error)
    {
        var context = Context(block);
        try
        {
            if (error != 0 || image == 0) throw new InvalidOperationException("WebKit could not capture this package view.");
            var tiff = Send(image, Selector("TIFFRepresentation"));
            var bitmap = SendPointer(GetClass("NSBitmapImageRep"), Selector("imageRepWithData:"), tiff);
            var data = SendIntegerPointer(bitmap, Selector("representationUsingType:properties:"), 4, Send(GetClass("NSDictionary"), Selector("dictionary")));
            var length = Send(data, Selector("length"));
            if (data == 0 || length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Native image exceeds its 2 MiB capture budget.");
            var bytes = new byte[(int)length];
            Marshal.Copy(Send(data, Selector("bytes")), bytes, 0, bytes.Length);
            context.Completion.TrySetResult(bytes);
        }
        catch (Exception failure) { context.Completion.TrySetException(failure); }
    }

    private sealed class CaptureContext
    {
        private int _references = 1;
        private readonly GCHandle _handle;
        internal nint Handle => GCHandle.ToIntPtr(_handle);
        internal TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CaptureContext() => _handle = GCHandle.Alloc(this);
        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release() { if (Interlocked.Decrement(ref _references) == 0) _handle.Free(); }
    }

    internal static void Release(nint value) { if (value != 0) Send(value, Selector("release")); }
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double GetDouble(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPointer(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendDouble(nint receiver, nint selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendIntegerPointer(nint receiver, nint selector, nint type, nint properties);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendTwoPointers(nint receiver, nint selector, nint first, nint second);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SetRect(nint receiver, nint selector, NativeRect rect);
    [DllImport(SystemLibrary, EntryPoint = "_Block_copy")] private static extern nint BlockCopy(nint block);
    [DllImport(SystemLibrary, EntryPoint = "_Block_release")] private static extern void BlockRelease(nint block);
}
