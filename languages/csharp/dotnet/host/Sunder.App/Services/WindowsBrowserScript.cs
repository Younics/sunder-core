using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Web.WebView2.Core;

namespace Sunder.App.Services;

// ExecuteScript may relinquish its callback without invoking it when the document
// disappears. The managed SDK Task alone cannot observe that path. Use the public
// COM API and settle only when native ownership ends, not on a canceled waiter.
[SupportedOSPlatform("windows")]
internal static class WindowsBrowserScript
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Execute(nint self, [MarshalAs(UnmanagedType.LPWStr)] string script, nint callback);

    internal static Task<string?> ExecuteAsync(CoreWebView2 web, string script)
    {
        var callback = new Completion();
        try
        {
            var view = web.GetComICoreWebView2();
            try
            {
                // Original ICoreWebView2 ABI, including IUnknown: ExecuteScript.
                var method = Marshal.GetDelegateForFunctionPointer<Execute>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(view), 29 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(method(view, script, callback.Pointer));
            }
            finally { Marshal.Release(view); }
        }
        catch (Exception error) { callback.Error = error; }
        finally { callback.ReleaseOwner(); }
        return callback.Result;
    }

    private sealed class Completion
    {
        private static readonly Guid CallbackId = new("49511172-cc67-4bca-9923-137112f4c4cc");
        private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(nint self, ref Guid id, out nint result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Reference(nint self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(nint self, int result, nint json);
        private static readonly Query QueryHandler = QueryInterface;
        private static readonly Reference AddHandler = AddReference, ReleaseHandler = ReleaseReference;
        private static readonly Invoke InvokeHandler = Complete;
        private static readonly nint Vtable = CreateVtable();
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GCHandle _root;
        private int _references = 1;
        private bool _invoked;
        private string? _value;
        internal Exception? Error;
        internal nint Pointer { get; }
        internal Task<string?> Result => _result.Task;

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
        internal void ReleaseOwner() => ReleaseReference(Pointer);
        private static Completion Context(nint self) => (Completion)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target!;
        private static int QueryInterface(nint self, ref Guid id, out nint result)
        {
            result = 0;
            if (id != CallbackId && id != UnknownId) return unchecked((int)0x80004002);
            AddReference(self); result = self; return 0;
        }
        private static uint AddReference(nint self) => checked((uint)Interlocked.Increment(ref Context(self)._references));
        private static uint ReleaseReference(nint self)
        {
            var context = Context(self);
            var count = Interlocked.Decrement(ref context._references);
            if (count == 0)
            {
                var error = context.Error;
                if (error is not null) context._result.TrySetException(error);
                else if (context._invoked) context._result.TrySetResult(context._value);
                else context._result.TrySetException(new OperationCanceledException("The native browser relinquished the script operation without a result."));
                Marshal.FreeHGlobal(self); context._root.Free();
            }
            return checked((uint)count);
        }
        private static int Complete(nint self, int result, nint json)
        {
            var context = Context(self);
            try { Marshal.ThrowExceptionForHR(result); context._value = Marshal.PtrToStringUni(json); }
            catch (Exception error) { context.Error = error; }
            context._invoked = true;
            return 0;
        }
        private static nint CreateVtable()
        {
            var memory = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.WriteIntPtr(memory, Marshal.GetFunctionPointerForDelegate(QueryHandler));
            Marshal.WriteIntPtr(memory, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddHandler));
            Marshal.WriteIntPtr(memory, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReleaseHandler));
            Marshal.WriteIntPtr(memory, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(InvokeHandler));
            return memory;
        }
    }
}
