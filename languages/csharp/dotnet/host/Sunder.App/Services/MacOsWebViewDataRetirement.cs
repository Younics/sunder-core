using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sunder.App.Services;

internal static class MacOsWebViewDataRetirement
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib", SystemLibrary = "/usr/lib/libSystem.B.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Invoke(nint block);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Copy(nint target, nint source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Dispose(nint block);
    private static readonly Invoke Invoked = block => Get(block).Settled.TrySetResult();
    private static readonly Copy Copied = (_, block) => Interlocked.Increment(ref Get(block).References);
    private static readonly Dispose Disposed = block => Get(block).Release();
    private static readonly nint Descriptor = CreateDescriptor();
    private static readonly nint StackClass = NativeLibrary.GetExport(NativeLibrary.Load(SystemLibrary), "_NSConcreteStackBlock");
    [StructLayout(LayoutKind.Sequential)] private struct Block
    {
        internal nint Isa;
        internal int Flags, Reserved;
        internal nint Invoke, Descriptor, Context;
    }
    private sealed class Context
    {
        internal int References = 1;
        internal readonly GCHandle Root;
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Context() => Root = GCHandle.Alloc(this);
        internal void Release()
        {
            if (Interlocked.Decrement(ref References) != 0) return;
            Settled.TrySetException(new OperationCanceledException("WebKit relinquished its data-retirement callback without completion."));
            Root.Free();
        }
    }

    [SupportedOSPlatform("macos")]
    internal static Task ClearAsync(nint view)
    {
        var context = new Context();
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Block>());
        nint copied = 0;
        try
        {
            Marshal.StructureToPtr(new Block { Isa = StackClass, Flags = 1 << 25, Invoke = Marshal.GetFunctionPointerForDelegate(Invoked), Descriptor = Descriptor, Context = GCHandle.ToIntPtr(context.Root) }, memory, false);
            copied = BlockCopy(memory);
            if (copied == 0) throw new InvalidOperationException("WebKit could not retain its data-retirement callback.");
            var configuration = Send(view, Selector("configuration"));
            var store = Send(configuration, Selector("websiteDataStore"));
            if (store == 0) throw new InvalidOperationException("WebKit supplied no isolated website data store.");
            var types = Send(GetClass("WKWebsiteDataStore"), Selector("allWebsiteDataTypes"));
            var date = Send(GetClass("NSDate"), Selector("distantPast"));
            RemoveData(store, Selector("removeDataOfTypes:modifiedSince:completionHandler:"), types, date, copied);
        }
        catch (Exception error) { context.Settled.TrySetException(error); }
        finally
        {
            if (copied != 0) BlockRelease(copied);
            Marshal.FreeHGlobal(memory);
            context.Release();
        }
        return context.Settled.Task;
    }
    private static Context Get(nint block) => (Context)GCHandle.FromIntPtr(Marshal.PtrToStructure<Block>(block).Context).Target!;
    private static nint CreateDescriptor()
    {
        var result = Marshal.AllocHGlobal(4 * IntPtr.Size);
        Marshal.WriteIntPtr(result, 0);
        Marshal.WriteIntPtr(result, IntPtr.Size, Marshal.SizeOf<Block>());
        Marshal.WriteIntPtr(result, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(Copied));
        Marshal.WriteIntPtr(result, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(Disposed));
        return result;
    }
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void RemoveData(nint receiver, nint selector, nint types, nint date, nint completion);
    [DllImport(SystemLibrary, EntryPoint = "_Block_copy")] private static extern nint BlockCopy(nint block);
    [DllImport(SystemLibrary, EntryPoint = "_Block_release")] private static extern void BlockRelease(nint block);
}
