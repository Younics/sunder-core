using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sunder.App.Services;

internal static class AppPolicySchedulingActivity
{
    // A loaded package requires timely policy supervision even when its window is
    // inactive. Do not let App Nap defer that work. Neither display sleep nor
    // system sleep is disabled, and this is not an audio/real-time IO assertion.
    internal static IDisposable? Begin() => OperatingSystem.IsMacOS() ? new MacActivity() : null;

    [SupportedOSPlatform("macos")]
    private sealed class MacActivity : IDisposable
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal const ulong Options = 0x00FFFFFFUL & ~(1UL << 20); // NSActivityUserInitiatedAllowingIdleSystemSleep
        private static readonly nint Foundation = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        private readonly nint _process;
        private nint _activity;

        internal MacActivity()
        {
            GC.KeepAlive(Foundation);
            var pool = Send(Send(GetClass("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
            var reason = nint.Zero;
            try
            {
                _process = Send(GetClass("NSProcessInfo"), Selector("processInfo"));
                var text = Marshal.StringToCoTaskMemUTF8("Sunder loaded-package policy supervision");
                try { reason = SendPointer(Send(GetClass("NSString"), Selector("alloc")), Selector("initWithUTF8String:"), text); }
                finally { Marshal.FreeCoTaskMem(text); }
                var activity = BeginActivity(_process, Selector("beginActivityWithOptions:reason:"), Options, reason);
                if (activity == 0) throw new InvalidOperationException("macOS did not grant the policy scheduling activity.");
                _activity = Send(activity, Selector("retain"));
            }
            finally
            {
                if (reason != 0) Release(reason, Selector("release"));
                Release(pool, Selector("drain"));
            }
        }

        public void Dispose()
        {
            var activity = Interlocked.Exchange(ref _activity, 0);
            if (activity == 0) return;
            EndActivity(_process, Selector("endActivity:"), activity);
            Release(activity, Selector("release"));
        }

        [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPointer(nint receiver, nint selector, nint argument);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint BeginActivity(nint receiver, nint selector, ulong options, nint reason);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void EndActivity(nint receiver, nint selector, nint activity);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Release(nint receiver, nint selector);
    }
}
