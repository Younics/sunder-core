using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sunder.App.Services;

internal static class GtkWebViewDataRetirement
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Source(nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Ready(nint source, nint result, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetManager(nint view);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Clear(nint manager, uint types, long timespan, nint cancellable, Ready callback, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Finish(nint manager, nint result, out nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WeakNotify(nint data, nint value);
    private static readonly Source StartCallback = Start;
    private static readonly Ready Completed = Complete;
    private static readonly WeakNotify Finalized = (data, _) =>
    {
        var root = GCHandle.FromIntPtr(data);
        var completion = (TaskCompletionSource)root.Target!;
        root.Free();
        completion.TrySetResult();
    };
    private static readonly Source RegisterRetirement = data =>
    {
        var root = GCHandle.FromIntPtr(data);
        var registration = (Registration)root.Target!;
        root.Free();
        try
        {
            var tasks = new List<Task>();
            foreach (var pointer in new[] { registration.View, registration.GetContext(registration.View), registration.GetManager(registration.View) }.Distinct())
            {
                if (pointer == 0) throw new InvalidOperationException("The WebKitGTK retirement context is missing.");
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var callback = GCHandle.Alloc(completion);
                ObjectWeakRef(pointer, Finalized, GCHandle.ToIntPtr(callback));
                tasks.Add(completion.Task);
            }
            registration.Ready.TrySetResult(Task.WhenAll(tasks));
        }
        catch (Exception error) { registration.Ready.TrySetException(error); }
        return 0;
    };

    private sealed record Registration(nint View, GetManager GetContext, GetManager GetManager)
    {
        internal TaskCompletionSource<Task> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [SupportedOSPlatform("linux")]
    internal static async Task<Task> ObserveNativeRetirementAsync(nint view)
    {
        nint library = 0;
        foreach (var name in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
            if ((library = OpenLibrary(name, 2 | 4)) != 0) break;
        if (library == 0) throw new InvalidOperationException("The active WebKitGTK context could not be observed.");
        try
        {
            var registration = new Registration(view,
                Marshal.GetDelegateForFunctionPointer<GetManager>(NativeLibrary.GetExport(library, "webkit_web_view_get_context")),
                Marshal.GetDelegateForFunctionPointer<GetManager>(NativeLibrary.GetExport(library, "webkit_web_view_get_website_data_manager")));
            var root = GCHandle.Alloc(registration);
            if (IdleAdd(0, RegisterRetirement, GCHandle.ToIntPtr(root), 0) == 0)
            {
                root.Free();
                throw new InvalidOperationException("WebKitGTK retirement observation could not be scheduled.");
            }
            return await registration.Ready.Task;
        }
        finally { NativeLibrary.Free(library); }
    }

    [SupportedOSPlatform("linux")]
    internal static async Task ClearAsync(nint view)
    {
        nint library = 0;
        foreach (var name in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
            if ((library = OpenLibrary(name, 2 | 4 /* RTLD_NOW | RTLD_NOLOAD */)) != 0) break;
        if (library == 0) throw new InvalidOperationException("The active WebKitGTK data manager could not be retained.");
        try
        {
            var context = new Context(view,
                Marshal.GetDelegateForFunctionPointer<GetManager>(NativeLibrary.GetExport(library, "webkit_web_view_get_website_data_manager")),
                Marshal.GetDelegateForFunctionPointer<Clear>(NativeLibrary.GetExport(library, "webkit_website_data_manager_clear")),
                Marshal.GetDelegateForFunctionPointer<Finish>(NativeLibrary.GetExport(library, "webkit_website_data_manager_clear_finish")));
            var root = GCHandle.Alloc(context);
            try
            {
                if (IdleAdd(0, StartCallback, GCHandle.ToIntPtr(root), 0) == 0)
                    throw new InvalidOperationException("WebKitGTK data retirement could not be scheduled.");
                await context.Settled.Task;
            }
            finally { root.Free(); }
        }
        finally { NativeLibrary.Free(library); }
    }

    private sealed class Context(nint view, GetManager get, Clear clear, Finish finish)
    {
        internal nint View => view;
        internal GetManager Get => get;
        internal Clear Clear => clear;
        internal Finish Finish => finish;
        internal nint Manager;
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static Context Get(nint data) => (Context)GCHandle.FromIntPtr(data).Target!;
    private static int Start(nint data)
    {
        var context = Get(data);
        try
        {
            context.Manager = context.Get(context.View);
            if (context.Manager == 0) throw new InvalidOperationException("WebKitGTK supplied no data manager.");
            ObjectRef(context.Manager);
            context.Clear(context.Manager, 16383 /* WEBKIT_WEBSITE_DATA_ALL */, 0, 0, Completed, data);
        }
        catch (Exception error)
        {
            if (context.Manager != 0) { ObjectUnref(context.Manager); context.Manager = 0; }
            context.Settled.TrySetException(error);
        }
        return 0;
    }
    private static void Complete(nint source, nint result, nint data)
    {
        var context = Get(data);
        nint error = 0;
        Exception? failure = null;
        try
        {
            if (context.Finish(context.Manager, result, out error) == 0 || error != 0)
                throw new InvalidOperationException("WebKitGTK did not clear the retired view's data context.");
        }
        catch (Exception exception) { failure = exception; }
        finally { if (error != 0) ErrorFree(error); ObjectUnref(context.Manager); context.Manager = 0; }
        if (failure is null) context.Settled.TrySetResult();
        else context.Settled.TrySetException(failure);
    }

    [DllImport("libdl.so.2", EntryPoint = "dlopen")] private static extern nint OpenLibrary([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_idle_add_full")] private static extern uint IdleAdd(int priority, Source callback, nint data, nint destroy);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static extern void ErrorFree(nint error);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")] private static extern nint ObjectRef(nint value);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static extern void ObjectUnref(nint value);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_weak_ref")] private static extern void ObjectWeakRef(nint value, WeakNotify notify, nint data);
}
