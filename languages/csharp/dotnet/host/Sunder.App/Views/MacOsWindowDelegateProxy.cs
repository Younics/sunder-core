namespace Sunder.App.Views;

internal sealed class MacOsWindowDelegateProxy : IDisposable
{
    internal const string DidFailToEnterSelector = "windowDidFailToEnterFullScreen:";
    internal const string DidFailToExitSelector = "windowDidFailToExitFullScreen:";

    private static readonly object ProxiesGate = new();
    private static readonly Dictionary<IntPtr, WeakReference<MacOsWindowDelegateProxy>> Proxies = [];
    private readonly IntPtr _windowHandle;
    private readonly IMacOsWindowDelegateInterop _interop;
    private Action<IntPtr, NativeFullScreenTransitionKind>? _failure;
    private IntPtr _proxy;
    private bool _disposed;

    public MacOsWindowDelegateProxy(
        IntPtr windowHandle,
        IMacOsWindowDelegateInterop interop,
        Action<IntPtr, NativeFullScreenTransitionKind> failure)
    {
        _windowHandle = windowHandle;
        _interop = interop;
        _failure = failure;
    }

    internal IntPtr ProxyHandle => _proxy;

    public bool Install()
    {
        if (_disposed || _proxy != IntPtr.Zero || _windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var originalDelegate = _interop.AcquireWindowDelegate(_windowHandle);
        IntPtr proxy;
        try
        {
            proxy = _interop.CreateProxy(originalDelegate);
        }
        finally
        {
            _interop.Release(originalDelegate);
        }
        if (proxy == IntPtr.Zero)
        {
            return false;
        }

        _proxy = proxy;
        lock (ProxiesGate)
        {
            Proxies.Add(proxy, new WeakReference<MacOsWindowDelegateProxy>(this));
        }

        if (_interop.TrySetWindowDelegate(_windowHandle, proxy))
        {
            return true;
        }

        lock (ProxiesGate)
        {
            Proxies.Remove(proxy);
        }
        _proxy = IntPtr.Zero;
        var canDestroy = true;
        if (_interop.GetWindowDelegate(_windowHandle) == proxy)
        {
            var forwardDelegate = _interop.AcquireForwardDelegate(proxy);
            try
            {
                canDestroy = _interop.TrySetWindowDelegate(_windowHandle, forwardDelegate);
            }
            finally
            {
                _interop.Release(forwardDelegate);
            }
        }
        if (canDestroy)
        {
            _interop.DestroyProxy(proxy);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _failure = null;
        var proxy = _proxy;
        _proxy = IntPtr.Zero;
        if (proxy == IntPtr.Zero)
        {
            return;
        }

        lock (ProxiesGate)
        {
            Proxies.Remove(proxy);
        }

        var canDestroy = true;
        if (_interop.GetWindowDelegate(_windowHandle) == proxy)
        {
            var originalDelegate = _interop.AcquireForwardDelegate(proxy);
            try
            {
                canDestroy = _interop.TrySetWindowDelegate(_windowHandle, originalDelegate);
            }
            finally
            {
                _interop.Release(originalDelegate);
            }
        }

        if (canDestroy)
        {
            _interop.DestroyProxy(proxy);
        }
    }

    internal static void DispatchFailure(
        IntPtr proxy,
        IntPtr selector,
        IntPtr windowHandle,
        NativeFullScreenTransitionKind kind)
    {
        TryGetProxy(proxy)?.HandleFailure(proxy, selector, windowHandle, kind);
    }

    internal static bool ForwardDelegateRespondsToSelector(IntPtr proxy, IntPtr selector) =>
        TryGetProxy(proxy)?.ForwardDelegateRespondsToSelector(selector) == true;

    internal static IntPtr AcquireForwardingTarget(IntPtr proxy, IntPtr selector) =>
        TryGetProxy(proxy)?.AcquireForwardingTarget(selector) ?? IntPtr.Zero;

    internal static bool IsRegistered(IntPtr proxy) => TryGetProxy(proxy) is not null;

    private void HandleFailure(
        IntPtr proxy,
        IntPtr selector,
        IntPtr windowHandle,
        NativeFullScreenTransitionKind kind)
    {
        if (_disposed || proxy != _proxy)
        {
            return;
        }

        var originalDelegate = _interop.AcquireForwardDelegate(proxy);
        try
        {
            if (
                originalDelegate != IntPtr.Zero
                && originalDelegate != proxy
                && _interop.RespondsToSelector(originalDelegate, selector)
            )
            {
                _interop.SendWindowDelegateCallback(originalDelegate, selector, windowHandle);
            }
        }
        finally
        {
            _interop.Release(originalDelegate);
        }

        if (windowHandle == _windowHandle)
        {
            _failure?.Invoke(windowHandle, kind);
        }
    }

    private bool ForwardDelegateRespondsToSelector(IntPtr selector)
    {
        if (_disposed || _proxy == IntPtr.Zero)
        {
            return false;
        }

        var forwardDelegate = _interop.AcquireForwardDelegate(_proxy);
        try
        {
            return forwardDelegate != IntPtr.Zero
                && _interop.RespondsToSelector(forwardDelegate, selector);
        }
        finally
        {
            _interop.Release(forwardDelegate);
        }
    }

    private IntPtr AcquireForwardingTarget(IntPtr selector)
    {
        if (_disposed || _proxy == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var forwardDelegate = _interop.AcquireForwardDelegate(_proxy);
        try
        {
            if (
                forwardDelegate != IntPtr.Zero
                && _interop.RespondsToSelector(forwardDelegate, selector)
            )
            {
                return forwardDelegate;
            }
        }
        catch
        {
            _interop.Release(forwardDelegate);
            throw;
        }

        _interop.Release(forwardDelegate);
        return IntPtr.Zero;
    }

    private static MacOsWindowDelegateProxy? TryGetProxy(IntPtr proxy)
    {
        lock (ProxiesGate)
        {
            return Proxies.TryGetValue(proxy, out var reference)
                && reference.TryGetTarget(out var registered)
                    ? registered
                    : null;
        }
    }
}

internal interface IMacOsWindowDelegateInterop
{
    IntPtr GetWindowDelegate(IntPtr windowHandle);

    IntPtr AcquireWindowDelegate(IntPtr windowHandle);

    IntPtr CreateProxy(IntPtr forwardDelegate);

    bool TrySetWindowDelegate(IntPtr windowHandle, IntPtr windowDelegate);

    IntPtr AcquireForwardDelegate(IntPtr proxy);

    bool RespondsToSelector(IntPtr target, IntPtr selector);

    void SendWindowDelegateCallback(IntPtr target, IntPtr selector, IntPtr windowHandle);

    void Release(IntPtr target);

    void DestroyProxy(IntPtr proxy);
}
