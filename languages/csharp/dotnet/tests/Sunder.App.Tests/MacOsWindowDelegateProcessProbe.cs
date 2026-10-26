using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Sunder.App.Views;

internal static class MacOsWindowDelegateProcessProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", name.Name + ".dll"));
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return Start(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Start(string[] args) => AppBuilder.Configure<Application>().UsePlatformDetect().AfterSetup(builder =>
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Startup += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var window = new Window { Title = "Window delegate regression", Width = 320, Height = 160 };
                desktop.MainWindow = window;
                window.Show();
                var handle = window.TryGetPlatformHandle()!;
                Require(handle.HandleDescriptor == "NSWindow", "This test requires a real AppKit window.");
                var original = Send(handle.Handle, Selector("delegate"));
                Require(original == handle.Handle, "The fixture must exercise Avalonia's self-delegating window.");
                using var transition = MacOsNativeFullScreenTransition.Create(window);
                transition.StartObserving();
                var proxy = Send(handle.Handle, Selector("delegate"));
                Require(proxy != original && proxy != IntPtr.Zero, "The production delegate proxy was not installed.");
                var request = Selector("validRequestorForSendType:returnType:");
                Require(!Responds(proxy, Selector("respondsToSelector:"), request), "The proxy advertises an inherited responder method.");
                Require(SendOne(proxy, Selector("forwardingTargetForSelector:"), request) == IntPtr.Zero,
                    "The proxy forwards a responder request back to the window.");
                // This actual AppKit traversal overflowed the stack before the fix.
                _ = SendTwo(handle.Handle, request, IntPtr.Zero, IntPtr.Zero);
                Require(Responds(proxy, Selector("respondsToSelector:"), Selector("windowShouldClose:")),
                    "The proxy lost a legitimate optional delegate callback.");
                SendVoid(proxy, Selector("windowDidFailToExitFullScreen:"), handle.Handle);

                // A failed restoration leaves a native proxy installed after its
                // managed owner is unregistered. Its fallback must be equally safe.
                var registry = (IDictionary)typeof(MacOsWindowDelegateProxy).GetField("Proxies", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                var registration = registry[proxy];
                registry.Remove(proxy);
                try
                {
                    Require(!Responds(proxy, Selector("respondsToSelector:"), request), "The unregistered proxy advertises a responder method.");
                    Require(SendOne(proxy, Selector("forwardingTargetForSelector:"), request) == IntPtr.Zero,
                        "The unregistered proxy forwards a responder method.");
                    _ = SendTwo(handle.Handle, request, IntPtr.Zero, IntPtr.Zero);
                    Require(Responds(proxy, Selector("respondsToSelector:"), Selector("windowShouldClose:")),
                        "The fallback lost a legitimate delegate callback.");
                }
                finally { registry[proxy] = registration; }
                var savedForwardDelegate = MacOsWindowDelegateProxy.AcquireForwardingTarget(proxy, Selector("windowShouldClose:"));
                Require(savedForwardDelegate == original, "The proxy no longer forwards to the original delegate.");
                Release(savedForwardDelegate);
                transition.Dispose();
                Require(Send(handle.Handle, Selector("delegate")) == original, "Disposal did not restore the original delegate.");
                window.Close();
                Console.WriteLine("native-window-delegate: services-request-returned; callbacks-preserved; delegate-restored");
                Console.WriteLine("runtime=" + RuntimeInformation.FrameworkDescription);
                desktop.Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
        });
    }).StartWithClassicDesktopLifetime(args);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendOne(IntPtr target, IntPtr selector, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendTwo(IntPtr target, IntPtr selector, IntPtr first, IntPtr second);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr target, IntPtr selector, IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")][return: MarshalAs(UnmanagedType.I1)] private static extern bool Responds(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_release")] private static extern void Release(IntPtr target);
}
