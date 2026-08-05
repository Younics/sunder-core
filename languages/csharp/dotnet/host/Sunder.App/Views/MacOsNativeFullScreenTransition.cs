using System.Runtime.InteropServices;
using Avalonia.Controls;
using Sunder.App.Services;

namespace Sunder.App.Views;

internal sealed class MacOsNativeFullScreenTransition : INativeFullScreenTransition
{
    private const ulong FullScreenStyleMask = 1UL << 14;
    private static readonly object ObserversGate = new();
    private static readonly Dictionary<IntPtr, WeakReference<MacOsNativeFullScreenTransition>> Observers = [];
    private static readonly ObjectiveC.NotificationCallback WillEnterCallback =
        (observer, _, notification) => Dispatch(observer, notification, NativeFullScreenTransitionKind.WillEnter);
    private static readonly ObjectiveC.NotificationCallback DidEnterCallback =
        (observer, _, notification) => Dispatch(observer, notification, NativeFullScreenTransitionKind.DidEnter);
    private static readonly ObjectiveC.NotificationCallback WillExitCallback =
        (observer, _, notification) => Dispatch(observer, notification, NativeFullScreenTransitionKind.WillExit);
    private static readonly ObjectiveC.NotificationCallback DidExitCallback =
        (observer, _, notification) => Dispatch(observer, notification, NativeFullScreenTransitionKind.DidExit);
    private static readonly Lazy<IntPtr> ObserverClass = new(CreateObserverClass);
    private readonly Window _window;
    private MacOsWindowDelegateProxy? _delegateProxy;
    private IntPtr _notificationCenter;
    private IntPtr _observer;
    private NativeFullScreenState _state;
    private bool _disposed;

    private MacOsNativeFullScreenTransition(Window window)
    {
        _window = window;
    }

    public event EventHandler<NativeFullScreenTransitionEventArgs>? Changed;

    public IntPtr WindowHandle { get; private set; }

    public NativeFullScreenState State
    {
        get
        {
            if (_state == NativeFullScreenState.Windowed && _window.WindowState == WindowState.FullScreen)
            {
                return NativeFullScreenState.Entering;
            }

            if (_state == NativeFullScreenState.FullScreen && _window.WindowState != WindowState.FullScreen)
            {
                return NativeFullScreenState.Exiting;
            }

            return _state;
        }
    }

    public static INativeFullScreenTransition Create(Window window) =>
        OperatingSystem.IsMacOS()
            ? new MacOsNativeFullScreenTransition(window)
            : NoNativeFullScreenTransition.Instance;

    public void StartObserving()
    {
        if (_disposed || !OperatingSystem.IsMacOS())
        {
            return;
        }

        var handle = _window.TryGetPlatformHandle();
        if (
            handle is null
            || handle.Handle == IntPtr.Zero
            || !string.Equals(handle.HandleDescriptor, "NSWindow", StringComparison.Ordinal)
        )
        {
            return;
        }

        if (_observer != IntPtr.Zero && handle.Handle == WindowHandle)
        {
            return;
        }

        StopObserving();
        WindowHandle = handle.Handle;
        try
        {
            _notificationCenter = ObjectiveC.GetDefaultNotificationCenter();
            _observer = ObjectiveC.CreateObject(ObserverClass.Value);
            if (_notificationCenter == IntPtr.Zero || _observer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Could not create the native full-screen notification observer.");
            }
            lock (ObserversGate)
            {
                Observers.Add(_observer, new WeakReference<MacOsNativeFullScreenTransition>(this));
            }

            _delegateProxy = new MacOsWindowDelegateProxy(
                WindowHandle,
                ObjectiveCMacOsWindowDelegateInterop.Instance,
                OnDelegateFailure);
            if (!_delegateProxy.Install())
            {
                throw new InvalidOperationException(
                    "Could not interpose the existing NSWindow delegate.");
            }

            AddObserver(
                "sunderWindowWillEnterFullScreen:",
                "NSWindowWillEnterFullScreenNotification");
            AddObserver(
                "sunderWindowDidEnterFullScreen:",
                "NSWindowDidEnterFullScreenNotification");
            AddObserver(
                "sunderWindowWillExitFullScreen:",
                "NSWindowWillExitFullScreenNotification");
            AddObserver(
                "sunderWindowDidExitFullScreen:",
                "NSWindowDidExitFullScreenNotification");

            _state = ReadCurrentState();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to observe native macOS full-screen transitions.", ex);
            StopObserving();
        }
    }

    public bool RequestExitFullScreen()
    {
        if (_disposed || WindowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            ObjectiveC.SendObject(WindowHandle, "toggleFullScreen:", IntPtr.Zero);
            if (_state == NativeFullScreenState.FullScreen)
            {
                _state = NativeFullScreenState.Exiting;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to request native macOS full-screen exit.", ex);
            return false;
        }
    }

    public NativeFullScreenState ReconcileState()
    {
        if (_disposed || WindowHandle == IntPtr.Zero)
        {
            return State;
        }

        try
        {
            return _state = ReadCurrentState();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to reconcile native macOS full-screen state.", ex);
            return State;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopObserving();
    }

    private void AddObserver(string callbackSelector, string notificationSymbol)
    {
        ObjectiveC.AddObserver(
            _notificationCenter,
            _observer,
            callbackSelector,
            notificationSymbol,
            WindowHandle);
    }

    private void StopObserving()
    {
        try
        {
            _delegateProxy?.Dispose();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to restore the native NSWindow delegate.", ex);
        }
        _delegateProxy = null;
        if (_observer != IntPtr.Zero)
        {
            lock (ObserversGate)
            {
                Observers.Remove(_observer);
            }

            try
            {
                if (_notificationCenter != IntPtr.Zero)
                {
                    ObjectiveC.RemoveObserver(_notificationCenter, _observer);
                }
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to remove native macOS full-screen observers.", ex);
            }

            ObjectiveC.Release(_observer);
        }

        _notificationCenter = IntPtr.Zero;
        _observer = IntPtr.Zero;
        WindowHandle = IntPtr.Zero;
        _state = NativeFullScreenState.Windowed;
    }

    private NativeFullScreenState ReadCurrentState()
    {
        var isNativeFullScreen =
            (ObjectiveC.GetUnsignedInteger(WindowHandle, "styleMask") & FullScreenStyleMask) != 0;
        if (!isNativeFullScreen && _state == NativeFullScreenState.Exiting)
        {
            return NativeFullScreenState.Windowed;
        }
        return isNativeFullScreen
            ? _window.WindowState == WindowState.FullScreen
                ? NativeFullScreenState.FullScreen
                : NativeFullScreenState.Exiting
            : _window.WindowState == WindowState.FullScreen
                ? NativeFullScreenState.Entering
                : NativeFullScreenState.Windowed;
    }

    private void OnNotification(IntPtr notifiedWindow, NativeFullScreenTransitionKind kind)
    {
        if (_disposed || notifiedWindow != WindowHandle)
        {
            return;
        }

        _state = kind switch
        {
            NativeFullScreenTransitionKind.WillEnter => NativeFullScreenState.Entering,
            NativeFullScreenTransitionKind.DidEnter => NativeFullScreenState.FullScreen,
            NativeFullScreenTransitionKind.DidFailToEnter => NativeFullScreenState.Windowed,
            NativeFullScreenTransitionKind.WillExit => NativeFullScreenState.Exiting,
            NativeFullScreenTransitionKind.DidExit => NativeFullScreenState.Windowed,
            NativeFullScreenTransitionKind.DidFailToExit => NativeFullScreenState.FullScreen,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Changed?.Invoke(this, new NativeFullScreenTransitionEventArgs(notifiedWindow, kind));
    }

    private void OnDelegateFailure(IntPtr windowHandle, NativeFullScreenTransitionKind kind) =>
        OnNotification(windowHandle, kind);

    private static void Dispatch(
        IntPtr observer,
        IntPtr notification,
        NativeFullScreenTransitionKind kind)
    {
        MacOsNativeFullScreenTransition? transition = null;
        lock (ObserversGate)
        {
            if (
                Observers.TryGetValue(observer, out var reference)
                && reference.TryGetTarget(out var target)
            )
            {
                transition = target;
            }
        }

        if (transition is null)
        {
            return;
        }

        try
        {
            transition.OnNotification(ObjectiveC.GetNotificationObject(notification), kind);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to process a native macOS full-screen notification.", ex);
        }
    }

    private static IntPtr CreateObserverClass()
    {
        var existingClass = ObjectiveC.GetClass("SunderWindowFullScreenObserver");
        if (existingClass != IntPtr.Zero)
        {
            return existingClass;
        }

        var observerClass = ObjectiveC.AllocateClass(
            ObjectiveC.GetClass("NSObject"),
            "SunderWindowFullScreenObserver");
        if (observerClass == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not allocate the macOS full-screen observer class.");
        }

        ObjectiveC.AddMethod(observerClass, "sunderWindowWillEnterFullScreen:", WillEnterCallback);
        ObjectiveC.AddMethod(observerClass, "sunderWindowDidEnterFullScreen:", DidEnterCallback);
        ObjectiveC.AddMethod(observerClass, "sunderWindowWillExitFullScreen:", WillExitCallback);
        ObjectiveC.AddMethod(observerClass, "sunderWindowDidExitFullScreen:", DidExitCallback);
        ObjectiveC.RegisterClass(observerClass);
        return observerClass;
    }

    private sealed class ObjectiveCMacOsWindowDelegateInterop : IMacOsWindowDelegateInterop
    {
        private const string ProxyClassName = "SunderWindowDelegateProxy";
        private const string ForwardDelegateIvarName = "_sunderForwardDelegate";
        private static readonly FailureCallback DidFailToEnterCallback =
            (proxy, selector, window) => DispatchFailure(
                proxy,
                selector,
                window,
                NativeFullScreenTransitionKind.DidFailToEnter);
        private static readonly FailureCallback DidFailToExitCallback =
            (proxy, selector, window) => DispatchFailure(
                proxy,
                selector,
                window,
                NativeFullScreenTransitionKind.DidFailToExit);
        private static readonly RespondsToSelectorCallback RespondsToSelectorCallbackInstance =
            ProxyRespondsToSelector;
        private static readonly ForwardingTargetCallback ForwardingTargetCallbackInstance =
            ProxyForwardingTarget;
        private static readonly Lazy<ProxyMetadata> Metadata = new(CreateProxyMetadata);

        public static ObjectiveCMacOsWindowDelegateInterop Instance { get; } = new();

        private ObjectiveCMacOsWindowDelegateInterop() { }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FailureCallback(IntPtr proxy, IntPtr selector, IntPtr window);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool RespondsToSelectorCallback(
            IntPtr proxy,
            IntPtr command,
            IntPtr selector);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ForwardingTargetCallback(
            IntPtr proxy,
            IntPtr command,
            IntPtr selector);

        public IntPtr GetWindowDelegate(IntPtr windowHandle) =>
            ObjectiveC.GetObject(windowHandle, "delegate");

        public IntPtr AcquireWindowDelegate(IntPtr windowHandle) =>
            ObjectiveC.Retain(GetWindowDelegate(windowHandle));

        public IntPtr CreateProxy(IntPtr forwardDelegate)
        {
            var proxy = ObjectiveC.CreateObject(Metadata.Value.ProxyClass);
            if (proxy == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                ObjectiveC.InitializeWeak(
                    proxy,
                    Metadata.Value.ForwardDelegateOffset,
                    forwardDelegate);
                return proxy;
            }
            catch
            {
                ObjectiveC.Release(proxy);
                throw;
            }
        }

        public bool TrySetWindowDelegate(IntPtr windowHandle, IntPtr windowDelegate)
        {
            try
            {
                ObjectiveC.SendObject(windowHandle, "setDelegate:", windowDelegate);
                return GetWindowDelegate(windowHandle) == windowDelegate;
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to update the native NSWindow delegate.", ex);
                return false;
            }
        }

        public IntPtr AcquireForwardDelegate(IntPtr proxy) =>
            ObjectiveC.LoadWeakRetained(proxy, Metadata.Value.ForwardDelegateOffset);

        public bool RespondsToSelector(IntPtr target, IntPtr selector) =>
            ObjectiveC.RespondsToSelector(target, selector);

        public void SendWindowDelegateCallback(
            IntPtr target,
            IntPtr selector,
            IntPtr windowHandle) =>
            ObjectiveC.SendObject(target, selector, windowHandle);

        public void Release(IntPtr target) => ObjectiveC.Release(target);

        public void DestroyProxy(IntPtr proxy)
        {
            ObjectiveC.DestroyWeak(proxy, Metadata.Value.ForwardDelegateOffset);
            ObjectiveC.Release(proxy);
        }

        private static void DispatchFailure(
            IntPtr proxy,
            IntPtr selector,
            IntPtr window,
            NativeFullScreenTransitionKind kind)
        {
            try
            {
                if (!MacOsWindowDelegateProxy.IsRegistered(proxy))
                {
                    var forwardDelegate = Instance.AcquireForwardDelegate(proxy);
                    try
                    {
                        if (
                            forwardDelegate != IntPtr.Zero
                            && Instance.RespondsToSelector(forwardDelegate, selector)
                        )
                        {
                            Instance.SendWindowDelegateCallback(
                                forwardDelegate,
                                selector,
                                window);
                        }
                    }
                    finally
                    {
                        Instance.Release(forwardDelegate);
                    }
                    return;
                }

                MacOsWindowDelegateProxy.DispatchFailure(proxy, selector, window, kind);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to process an NSWindowDelegate callback.", ex);
            }
        }

        private static bool ProxyRespondsToSelector(
            IntPtr proxy,
            IntPtr command,
            IntPtr selector)
        {
            try
            {
                if (ObjectiveC.ClassHasInstanceMethod(Metadata.Value.ProxyClass, selector))
                {
                    return true;
                }
                if (MacOsWindowDelegateProxy.IsRegistered(proxy))
                {
                    return MacOsWindowDelegateProxy.ForwardDelegateRespondsToSelector(
                        proxy,
                        selector);
                }

                var forwardDelegate = Instance.AcquireForwardDelegate(proxy);
                try
                {
                    return forwardDelegate != IntPtr.Zero
                        && Instance.RespondsToSelector(forwardDelegate, selector);
                }
                finally
                {
                    Instance.Release(forwardDelegate);
                }
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to query the NSWindow delegate proxy.", ex);
                return false;
            }
        }

        private static IntPtr ProxyForwardingTarget(
            IntPtr proxy,
            IntPtr command,
            IntPtr selector)
        {
            try
            {
                if (MacOsWindowDelegateProxy.IsRegistered(proxy))
                {
                    return ObjectiveC.Autorelease(
                        MacOsWindowDelegateProxy.AcquireForwardingTarget(proxy, selector));
                }

                var forwardDelegate = Instance.AcquireForwardDelegate(proxy);
                try
                {
                    if (
                        forwardDelegate != IntPtr.Zero
                        && Instance.RespondsToSelector(forwardDelegate, selector)
                    )
                    {
                        return ObjectiveC.Autorelease(forwardDelegate);
                    }
                }
                catch
                {
                    Instance.Release(forwardDelegate);
                    throw;
                }

                Instance.Release(forwardDelegate);
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to forward an NSWindow delegate selector.", ex);
                return IntPtr.Zero;
            }
        }

        private static ProxyMetadata CreateProxyMetadata()
        {
            var proxyClass = ObjectiveC.GetClass(ProxyClassName);
            if (proxyClass == IntPtr.Zero)
            {
                proxyClass = ObjectiveC.AllocateClass(
                    ObjectiveC.GetClass("NSObject"),
                    ProxyClassName);
                if (proxyClass == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "Could not allocate the NSWindow delegate proxy class.");
                }

                ObjectiveC.AddObjectIvar(proxyClass, ForwardDelegateIvarName);
                ObjectiveC.AddProtocol(proxyClass, "NSWindowDelegate");
                ObjectiveC.AddMethod(
                    proxyClass,
                    MacOsWindowDelegateProxy.DidFailToEnterSelector,
                    DidFailToEnterCallback,
                    "v@:@");
                ObjectiveC.AddMethod(
                    proxyClass,
                    MacOsWindowDelegateProxy.DidFailToExitSelector,
                    DidFailToExitCallback,
                    "v@:@");
                ObjectiveC.AddMethod(
                    proxyClass,
                    "respondsToSelector:",
                    RespondsToSelectorCallbackInstance,
                    "c@::");
                ObjectiveC.AddMethod(
                    proxyClass,
                    "forwardingTargetForSelector:",
                    ForwardingTargetCallbackInstance,
                    "@@::");
                ObjectiveC.RegisterClass(proxyClass);
            }

            return new ProxyMetadata(
                proxyClass,
                ObjectiveC.GetIvarOffset(proxyClass, ForwardDelegateIvarName));
        }

        private sealed record ProxyMetadata(IntPtr ProxyClass, nint ForwardDelegateOffset);
    }

    private sealed class NoNativeFullScreenTransition : INativeFullScreenTransition
    {
        public static NoNativeFullScreenTransition Instance { get; } = new();

        private NoNativeFullScreenTransition() { }

        public event EventHandler<NativeFullScreenTransitionEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public IntPtr WindowHandle => IntPtr.Zero;

        public NativeFullScreenState State => NativeFullScreenState.Windowed;

        public NativeFullScreenState ReconcileState() => NativeFullScreenState.Windowed;

        public void StartObserving() { }

        public bool RequestExitFullScreen() => false;

        public void Dispose() { }
    }

    private static class ObjectiveC
    {
        private const string Library = "/usr/lib/libobjc.A.dylib";
        private const string AppKitLibrary = "/System/Library/Frameworks/AppKit.framework/AppKit";
        private const string MethodEncoding = "v@:@";
        private static readonly Lazy<IntPtr> AppKit = new(() => NativeLibrary.Load(AppKitLibrary));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void NotificationCallback(
            IntPtr observer,
            IntPtr selector,
            IntPtr notification);

        public static IntPtr GetClass(string name) => objc_getClass(name);

        public static IntPtr AllocateClass(IntPtr baseClass, string name) =>
            objc_allocateClassPair(baseClass, name, 0);

        public static void AddMethod(
            IntPtr targetClass,
            string selector,
            NotificationCallback callback)
            => AddMethod(targetClass, selector, (Delegate)callback, MethodEncoding);

        public static void AddMethod(
            IntPtr targetClass,
            string selector,
            Delegate callback,
            string methodEncoding)
        {
            if (
                !class_addMethod(
                    targetClass,
                    sel_registerName(selector),
                    Marshal.GetFunctionPointerForDelegate(callback),
                    methodEncoding)
            )
            {
                throw new InvalidOperationException($"Could not add Objective-C method '{selector}'.");
            }
        }

        public static void RegisterClass(IntPtr targetClass) => objc_registerClassPair(targetClass);

        public static IntPtr CreateObject(IntPtr targetClass)
        {
            var instance = SendIntPtr(targetClass, sel_registerName("alloc"));
            return SendIntPtr(instance, sel_registerName("init"));
        }

        public static void AddObjectIvar(IntPtr targetClass, string name)
        {
            var alignment = IntPtr.Size == 8 ? (byte)3 : (byte)2;
            if (!class_addIvar(targetClass, name, (nuint)IntPtr.Size, alignment, "@"))
            {
                throw new InvalidOperationException($"Could not add Objective-C ivar '{name}'.");
            }
        }

        public static void AddProtocol(IntPtr targetClass, string name)
        {
            var protocol = objc_getProtocol(name);
            if (protocol == IntPtr.Zero || !class_addProtocol(targetClass, protocol))
            {
                throw new InvalidOperationException(
                    $"Could not add Objective-C protocol '{name}'.");
            }
        }

        public static nint GetIvarOffset(IntPtr targetClass, string name)
        {
            var ivar = class_getInstanceVariable(targetClass, name);
            if (ivar == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Could not resolve Objective-C ivar '{name}'.");
            }
            return ivar_getOffset(ivar);
        }

        public static void InitializeWeak(IntPtr instance, nint offset, IntPtr value) =>
            objc_initWeak(GetIvarAddress(instance, offset), value);

        public static IntPtr LoadWeakRetained(IntPtr instance, nint offset) =>
            instance == IntPtr.Zero
                ? IntPtr.Zero
                : objc_loadWeakRetained(GetIvarAddress(instance, offset));

        public static void DestroyWeak(IntPtr instance, nint offset)
        {
            if (instance != IntPtr.Zero)
            {
                objc_destroyWeak(GetIvarAddress(instance, offset));
            }
        }

        public static IntPtr GetDefaultNotificationCenter()
        {
            var notificationCenterClass = GetClass("NSNotificationCenter");
            return SendIntPtr(
                notificationCenterClass,
                sel_registerName("defaultCenter"));
        }

        public static void AddObserver(
            IntPtr notificationCenter,
            IntPtr observer,
            string callbackSelector,
            string notificationSymbol,
            IntPtr window)
        {
            var symbol = NativeLibrary.GetExport(AppKit.Value, notificationSymbol);
            var notificationName = Marshal.ReadIntPtr(symbol);
            SendFourObjects(
                notificationCenter,
                sel_registerName("addObserver:selector:name:object:"),
                observer,
                sel_registerName(callbackSelector),
                notificationName,
                window);
        }

        public static void RemoveObserver(IntPtr notificationCenter, IntPtr observer) =>
            SendObject(
                notificationCenter,
                sel_registerName("removeObserver:"),
                observer);

        public static IntPtr GetNotificationObject(IntPtr notification) =>
            SendIntPtr(notification, sel_registerName("object"));

        public static IntPtr GetObject(IntPtr receiver, string selector) =>
            SendIntPtr(receiver, sel_registerName(selector));

        public static ulong GetUnsignedInteger(IntPtr receiver, string selector) =>
            SendUnsignedInteger(receiver, sel_registerName(selector));

        public static void SendObject(IntPtr receiver, string selector, IntPtr value) =>
            SendObject(receiver, sel_registerName(selector), value);

        public static void SendObject(IntPtr receiver, IntPtr selector, IntPtr value) =>
            SendObjectMessage(receiver, selector, value);

        public static bool RespondsToSelector(IntPtr target, IntPtr selector) =>
            target != IntPtr.Zero
            && SendBooleanWithSelector(
                target,
                sel_registerName("respondsToSelector:"),
                selector);

        public static bool ClassHasInstanceMethod(IntPtr targetClass, IntPtr selector) =>
            class_getInstanceMethod(targetClass, selector) != IntPtr.Zero;

        public static IntPtr Autorelease(IntPtr instance) =>
            instance == IntPtr.Zero
                ? IntPtr.Zero
                : SendIntPtr(instance, sel_registerName("autorelease"));

        public static IntPtr Retain(IntPtr instance) =>
            instance == IntPtr.Zero ? IntPtr.Zero : objc_retain(instance);

        public static void Release(IntPtr instance)
        {
            if (instance != IntPtr.Zero)
            {
                SendVoid(instance, sel_registerName("release"));
            }
        }

        private static IntPtr GetIvarAddress(IntPtr instance, nint offset) =>
            instance + checked((int)offset);

        [DllImport(Library)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(Library)]
        private static extern IntPtr objc_allocateClassPair(
            IntPtr superclass,
            string name,
            nuint extraBytes);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addMethod(
            IntPtr targetClass,
            IntPtr selector,
            IntPtr implementation,
            string types);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addIvar(
            IntPtr targetClass,
            string name,
            nuint size,
            byte alignment,
            string types);

        [DllImport(Library)]
        private static extern IntPtr objc_getProtocol(string name);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addProtocol(IntPtr targetClass, IntPtr protocol);

        [DllImport(Library)]
        private static extern IntPtr class_getInstanceVariable(IntPtr targetClass, string name);

        [DllImport(Library)]
        private static extern nint ivar_getOffset(IntPtr ivar);

        [DllImport(Library)]
        private static extern IntPtr class_getInstanceMethod(IntPtr targetClass, IntPtr selector);

        [DllImport(Library)]
        private static extern IntPtr objc_initWeak(IntPtr location, IntPtr value);

        [DllImport(Library)]
        private static extern IntPtr objc_loadWeakRetained(IntPtr location);

        [DllImport(Library)]
        private static extern void objc_destroyWeak(IntPtr location);

        [DllImport(Library)]
        private static extern IntPtr objc_retain(IntPtr instance);

        [DllImport(Library)]
        private static extern void objc_registerClassPair(IntPtr targetClass);

        [DllImport(Library, EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string selectorName);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern ulong SendUnsignedInteger(IntPtr receiver, IntPtr selector);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr receiver, IntPtr selector);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern void SendObjectMessage(
            IntPtr receiver,
            IntPtr selector,
            IntPtr value);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBooleanWithSelector(
            IntPtr receiver,
            IntPtr selector,
            IntPtr value);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern void SendFourObjects(
            IntPtr receiver,
            IntPtr selector,
            IntPtr value1,
            IntPtr value2,
            IntPtr value3,
            IntPtr value4);
    }
}
