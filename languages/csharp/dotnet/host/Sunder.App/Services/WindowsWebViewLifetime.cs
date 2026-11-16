using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;

namespace Sunder.App.Services;

// A controller's Close/AdapterDestroyed is not browser-group retirement. Keep
// the public WebView2 environment alive until its matching exit event, then
// unregister on the original STA. No wrapper-private types or process discovery.
[SupportedOSPlatform("windows")]
internal sealed class WindowsWebViewLifetime
{
    private static readonly Guid View2Id = new("9e8f0cf8-e670-4b5e-b2bc-73e061e3184c");
    private static readonly Guid Environment5Id = new("319e423d-e0d7-4b8d-9254-ae9475de9b17");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt(nint self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddExited(nint self, nint callback, out long token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RemoveExited(nint self, long token);
    private readonly nint _environment;
    private readonly long _token;
    private readonly ExitCallback _callback;

    private WindowsWebViewLifetime(nint environment, uint processId)
    {
        _environment = environment;
        _callback = new(processId);
        try { Marshal.ThrowExceptionForHR(Method<AddExited>(environment, 12)(environment, _callback.Pointer, out _token)); }
        catch { _callback.Dispose(); throw; }
    }

    internal static WindowsWebViewLifetime Observe(IWindowsWebView2PlatformHandle handle)
    {
        var borrowedView = handle.CoreWebView2;
        nint view2 = 0, environment = 0, environment5 = 0;
        try
        {
            if (borrowedView == 0) throw new InvalidOperationException("The native browser is not available for lifetime observation.");
            Marshal.ThrowExceptionForHR(Method<GetUInt>(borrowedView, 37)(borrowedView, out var processId));
            if (processId == 0) throw new InvalidDataException("The native browser supplied no process identity.");
            var viewId = View2Id;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(borrowedView, in viewId, out view2));
            // ICoreWebView2_2.get_Environment, including IUnknown/base slots.
            Marshal.ThrowExceptionForHR(Method<GetInterface>(view2, 67)(view2, out environment));
            if (environment == 0) throw new InvalidDataException("The native browser supplied no environment.");
            var environmentId = Environment5Id;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(environment, in environmentId, out environment5));
            var lifetime = new WindowsWebViewLifetime(environment5, processId);
            environment5 = 0; // Transfer the owned reference, not the getter's wrapper.
            return lifetime;
        }
        finally
        {
            if (environment5 != 0) Marshal.Release(environment5);
            if (environment != 0) Marshal.Release(environment);
            if (view2 != 0) Marshal.Release(view2);
        }
    }

    internal async Task DrainAsync(IUiDispatcher dispatcher)
    {
        var kind = await _callback.Exited.ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            // Release only after native acknowledgement, including on an
            // abnormal exit. A caller's timeout never abandons this real drain.
            try { Marshal.ThrowExceptionForHR(Method<RemoveExited>(_environment, 13)(_environment, _token)); }
            finally { Marshal.Release(_environment); _callback.Dispose(); }
        }).ConfigureAwait(false);
        if (kind != 0) throw new InvalidOperationException("The package browser process exited abnormally during retirement.");
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    // Native AddRef roots the callback through Invoke and any later Release.
    private sealed class ExitCallback : IDisposable
    {
        private static readonly Guid CallbackId = new("fa504257-a216-4911-a860-fe8825712861");
        private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(nint self, ref Guid id, out nint result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Reference(nint self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(nint self, nint sender, nint args);
        private static readonly Query QueryHandler = QueryInterface;
        private static readonly Reference AddHandler = AddReference, ReleaseHandler = ReleaseReference;
        private static readonly Invoke InvokeHandler = Complete;
        private static readonly nint Vtable = CreateVtable();
        private readonly TaskCompletionSource<uint> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GCHandle _root;
        private readonly uint _processId;
        private int _references = 1, _owned = 1;
        internal nint Pointer { get; }
        internal Task<uint> Exited => _exited.Task;

        internal ExitCallback(uint processId)
        {
            _processId = processId;
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
        private static ExitCallback Context(nint self) => (ExitCallback)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target!;
        private static int QueryInterface(nint self, ref Guid id, out nint result)
        {
            result = 0;
            if (id != CallbackId && id != UnknownId) return unchecked((int)0x80004002);
            AddReference(self);
            result = self;
            return 0;
        }
        private static uint AddReference(nint self) => checked((uint)Interlocked.Increment(ref Context(self)._references));
        private static uint ReleaseReference(nint self)
        {
            var context = Context(self);
            var references = Interlocked.Decrement(ref context._references);
            if (references == 0) { Marshal.FreeHGlobal(self); context._root.Free(); }
            return checked((uint)references);
        }
        private static int Complete(nint self, nint sender, nint args)
        {
            var context = Context(self);
            try
            {
                Marshal.ThrowExceptionForHR(Method<GetUInt>(args, 4)(args, out var processId));
                if (processId != context._processId) return 0;
                Marshal.ThrowExceptionForHR(Method<GetUInt>(args, 3)(args, out var kind));
                context._exited.TrySetResult(kind);
            }
            catch (Exception error) { context._exited.TrySetException(error); }
            return 0; // Never throw across a native callback boundary.
        }
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
