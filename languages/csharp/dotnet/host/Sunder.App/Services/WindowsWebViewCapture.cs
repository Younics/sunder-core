using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Platform;

namespace Sunder.App.Services;

// Captures the WebView's own surface, never an HWND/window/desktop screenshot.
// The owner preserves native render state until this real callback drains.
internal static class WindowsWebViewCapture
{
    private static readonly Guid WebViewId = new("76eceacb-0462-4d94-ac83-423a6793775e");
    private static readonly Guid ControllerId = new("4d00c0d1-9434-4eb6-8078-8697a560334f");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CapturePreview(nint self, int format, nint stream, nint callback);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBounds(nint self, out NativeBounds bounds);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeBounds(int Left, int Top, int Right, int Bottom);

    [SupportedOSPlatform("windows")]
    internal static Task<byte[]> CaptureAsync(IWindowsWebView2PlatformHandle handle, Rect bounds, Size viewport, int edge)
        // WebView 12.1.2 returns borrowed handles. CaptureCoreAsync obtains its
        // own QueryInterface references for submission, releasing the view before
        // awaiting the real callback rather than self-retaining native closure.
        => CaptureCoreAsync(handle.CoreWebView2, handle.CoreWebView2Controller, bounds, viewport, edge);

    [SupportedOSPlatform("windows")]
    internal static async Task<byte[]> CaptureCoreAsync(nint webView, nint controller, Rect bounds, Size viewport, int edge, PixelSize? ownedControllerPixels = null)
    {
        AppNativeWebViewCapture.ValidateRegion(bounds, viewport, edge);
        if (webView == 0 || controller == 0 && ownedControllerPixels is null) throw new InvalidOperationException("The native browser is not ready for capture.");
        var viewId = WebViewId;
        var controllerId = ControllerId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(webView, in viewId, out var view));
        nint retainedController = 0, streamPointer = 0;
        try
        {
            using var stream = new CaptureStream();
            using var callback = new Completion();
            if (controller != 0) Marshal.ThrowExceptionForHR(Marshal.QueryInterface(controller, in controllerId, out retainedController));
            // The direct backend owns its controller through the drain and checks
            // its native bounds/presentation epoch after completion. The wrapper
            // path instead retains and reads the public controller COM interface.
            var before = retainedController != 0 ? ReadBounds(retainedController)
                : new NativeBounds(0, 0, ownedControllerPixels!.Value.Width, ownedControllerPixels.Value.Height);
            var width = checked(before.Right - before.Left);
            var height = checked(before.Bottom - before.Top);
            AppNativeWebViewCapture.RequirePixels(width, height);
            streamPointer = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            // ICoreWebView2's original, stable ABI: CapturePreview is slot 30
            // including IUnknown. PNG is COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG (0).
            var capture = Method<CapturePreview>(view, 30);
            Marshal.ThrowExceptionForHR(capture(view, 0, streamPointer, callback.Pointer));
            callback.MarkSubmitted();
            // The native operation owns its submitted callback/stream. Retaining
            // our extra view reference until that callback settles can keep the
            // view alive precisely when closure must relinquish the callback.
            Marshal.Release(view);
            view = 0;
            // No cancellation/timeout releases a stream which WebView2 can still
            // write. Owner cancellation is checked by the caller after settlement.
            var result = await callback.Result;
            if (stream.LimitExceeded) throw new InvalidDataException("Native image exceeds its 2 MiB capture budget.");
            Marshal.ThrowExceptionForHR(result);
            if (retainedController != 0 && before != ReadBounds(retainedController)) throw new InvalidOperationException("The native viewport changed during capture.");
            var png = stream.ToArray();
            var dimensions = AppDevelopmentViewOperations.ValidatePng(png);
            if (dimensions != (width, height)) throw new InvalidDataException("Native image dimensions do not match the captured viewport.");
            return AppNativeWebViewCapture.Crop(png, bounds, viewport, edge);
        }
        finally
        {
            if (streamPointer != 0) Marshal.Release(streamPointer);
            if (retainedController != 0) Marshal.Release(retainedController);
            if (view != 0) Marshal.Release(view);
        }
    }

    private static NativeBounds ReadBounds(nint controller)
    {
        Marshal.ThrowExceptionForHR(Method<GetBounds>(controller, 5)(controller, out var bounds));
        return bounds;
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    // IStream's storage is bounded while WebView2 writes, not after allocating an
    // unbounded HGLOBAL. The same limit also applies while encoding cropped output.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class CaptureStream : AppNativeWebViewCapture.PngStream, IStream
    {
        void IStream.Read(byte[] buffer, int count, nint read) { var size = Read(buffer, 0, count); if (read != 0) Marshal.WriteInt32(read, size); }
        void IStream.Write(byte[] buffer, int count, nint written) { Write(buffer, 0, count); if (written != 0) Marshal.WriteInt32(written, count); }
        void IStream.Seek(long move, int origin, nint position) { var value = Seek(move, (SeekOrigin)origin); if (position != 0) Marshal.WriteInt64(position, value); }
        void IStream.SetSize(long size) => SetLength(size);
        void IStream.Commit(int flags) => Flush();
        void IStream.Stat(out STATSTG stat, int flags) => stat = new() { type = 2 /* STGTY_STREAM */, cbSize = Length, grfMode = 2 /* STGM_READWRITE */ };
        void IStream.CopyTo(IStream target, long count, nint read, nint written) => throw new NotSupportedException();
        void IStream.Revert() => throw new NotSupportedException();
        void IStream.LockRegion(long offset, long count, int type) => throw new NotSupportedException();
        void IStream.UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
        void IStream.Clone(out IStream stream) => throw new NotSupportedException();
    }

    // A four-slot IUnknown callback avoids exporting a public managed COM API or
    // depending on the wrapper's private interop types. Native references root the
    // managed context until Release, including a release after Invoke returns.
    internal sealed class Completion : IDisposable
    {
        private static readonly Guid CallbackId = new("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5");
        private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(nint self, ref Guid id, out nint result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Reference(nint self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Invoke(nint self, int result);
        private static readonly Query QueryHandler = QueryInterface;
        private static readonly Reference AddHandler = AddReference, ReleaseHandler = ReleaseReference;
        private static readonly Invoke InvokeHandler = Complete;
        private static readonly nint Vtable = CreateVtable();
        private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GCHandle _root;
        private int _references = 1, _owned = 1;
        private int _submitted;
        internal nint Pointer { get; }
        internal Task<int> Result => _result.Task;
        internal void MarkSubmitted()
        {
            Volatile.Write(ref _submitted, 1);
            if (Volatile.Read(ref _references) == 1) Relinquished();
        }

        private void Relinquished()
            => _result.TrySetException(new OperationCanceledException("The native browser relinquished capture without a result."));

        internal Completion()
        {
            Pointer = Marshal.AllocHGlobal(2 * IntPtr.Size);
            try
            {
                _root = GCHandle.Alloc(this);
                Marshal.WriteIntPtr(Pointer, Vtable);
                Marshal.WriteIntPtr(Pointer, IntPtr.Size, GCHandle.ToIntPtr(_root));
            }
            catch { Marshal.FreeHGlobal(Pointer); throw; }
        }
        public void Dispose() { if (Interlocked.Exchange(ref _owned, 0) != 0) ReleaseReference(Pointer); }
        private static Completion Context(nint self) => (Completion)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target!;
        private static int QueryInterface(nint self, ref Guid id, out nint result)
        {
            result = 0;
            if (id != CallbackId && id != UnknownId) return unchecked((int)0x80004002); // E_NOINTERFACE
            AddReference(self);
            result = self;
            return 0;
        }
        private static uint AddReference(nint self) => checked((uint)Interlocked.Increment(ref Context(self)._references));
        private static uint ReleaseReference(nint self)
        {
            var context = Context(self);
            var references = Interlocked.Decrement(ref context._references);
            if (references == 1 && Volatile.Read(ref context._owned) != 0 && Volatile.Read(ref context._submitted) != 0)
                context.Relinquished();
            if (references == 0) { Marshal.FreeHGlobal(self); context._root.Free(); }
            return checked((uint)references);
        }
        private static int Complete(nint self, int result) { Context(self)._result.TrySetResult(result); return 0; }
        private static nint CreateVtable()
        {
            var memory = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.WriteIntPtr(memory, 0, Marshal.GetFunctionPointerForDelegate(QueryHandler));
            Marshal.WriteIntPtr(memory, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddHandler));
            Marshal.WriteIntPtr(memory, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReleaseHandler));
            Marshal.WriteIntPtr(memory, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(InvokeHandler));
            return memory;
        }
    }
}
