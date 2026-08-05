using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

public sealed class MacOsWindowDelegateProxyTests
{
    private static readonly IntPtr Window = new(10);
    private static readonly IntPtr OriginalDelegate = new(20);
    private static readonly IntPtr OtherWindow = new(11);
    private static readonly IntPtr ReplacementDelegate = new(30);
    private static readonly IntPtr FailureSelector = new(40);
    private static readonly IntPtr OtherSelector = new(41);

    [Theory]
    [InlineData((int)NativeFullScreenTransitionKind.DidFailToEnter)]
    [InlineData((int)NativeFullScreenTransitionKind.DidFailToExit)]
    public void FailureSelector_ForwardsToExistingDelegateBeforeCoordinator(
        int kindValue)
    {
        var kind = (NativeFullScreenTransitionKind)kindValue;
        var calls = new List<string>();
        var interop = new FakeDelegateInterop(Window, OriginalDelegate, calls);
        interop.AddSelector(OriginalDelegate, FailureSelector);
        using var proxy = new MacOsWindowDelegateProxy(
            Window,
            interop,
            (window, failureKind) => calls.Add($"coordinator:{window}:{failureKind}"));
        Assert.True(proxy.Install());

        MacOsWindowDelegateProxy.DispatchFailure(
            proxy.ProxyHandle,
            FailureSelector,
            Window,
            kind);

        Assert.Equal(
            [
                $"forward:{OriginalDelegate}:{FailureSelector}:{Window}",
                $"coordinator:{Window}:{kind}",
            ],
            calls);
        Assert.Equal(interop.AcquireCount, interop.ReleaseCount);
    }

    [Fact]
    public void FailureSelector_ForOtherWindowOnlyForwardsExistingDelegate()
    {
        var calls = new List<string>();
        var interop = new FakeDelegateInterop(Window, OriginalDelegate, calls);
        interop.AddSelector(OriginalDelegate, FailureSelector);
        using var proxy = new MacOsWindowDelegateProxy(
            Window,
            interop,
            (_, _) => calls.Add("coordinator"));
        proxy.Install();

        MacOsWindowDelegateProxy.DispatchFailure(
            proxy.ProxyHandle,
            FailureSelector,
            OtherWindow,
            NativeFullScreenTransitionKind.DidFailToExit);

        Assert.Equal(
            [$"forward:{OriginalDelegate}:{FailureSelector}:{OtherWindow}"],
            calls);
    }

    [Fact]
    public void OptionalDelegateSelectors_ResolveToExistingDelegate()
    {
        var interop = new FakeDelegateInterop(Window, OriginalDelegate);
        interop.AddSelector(OriginalDelegate, OtherSelector);
        using var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => { });
        proxy.Install();

        Assert.True(
            MacOsWindowDelegateProxy.ForwardDelegateRespondsToSelector(
                proxy.ProxyHandle,
                OtherSelector));
        var forwardingTarget = MacOsWindowDelegateProxy.AcquireForwardingTarget(
            proxy.ProxyHandle,
            OtherSelector);

        Assert.Equal(OriginalDelegate, forwardingTarget);
        interop.Release(forwardingTarget);
        Assert.Equal(interop.AcquireCount, interop.ReleaseCount);
    }

    [Fact]
    public void OptionalDelegateSelectors_DoNotForwardToTheProxyItself()
    {
        var interop = new FakeDelegateInterop(Window, OriginalDelegate)
        {
            CreateSelfForwardingProxy = true,
        };
        var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => { });
        Assert.True(proxy.Install());
        var proxyHandle = proxy.ProxyHandle;
        interop.AddSelector(proxy.ProxyHandle, OtherSelector);

        Assert.False(
            MacOsWindowDelegateProxy.ForwardDelegateRespondsToSelector(
                proxy.ProxyHandle,
                OtherSelector));
        Assert.Equal(
            IntPtr.Zero,
            MacOsWindowDelegateProxy.AcquireForwardingTarget(proxy.ProxyHandle, OtherSelector));
        Assert.Equal(interop.AcquireCount, interop.ReleaseCount);

        proxy.Dispose();

        Assert.Equal(IntPtr.Zero, interop.GetWindowDelegate(Window));
        Assert.Contains(proxyHandle, interop.DestroyedProxies);
    }

    [Fact]
    public void OptionalDelegateSelectors_DoNotFollowAForwardingCycleBackThroughTheWindow()
    {
        var interop = new FakeDelegateInterop(Window, OriginalDelegate);
        interop.AddForwardedSelector(OriginalDelegate, OtherSelector);
        using var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => { });
        Assert.True(proxy.Install());
        Assert.True(interop.WouldRespondToSelector(OriginalDelegate, OtherSelector));

        Assert.False(
            MacOsWindowDelegateProxy.ForwardDelegateRespondsToSelector(
                proxy.ProxyHandle,
                OtherSelector));
        Assert.Equal(
            IntPtr.Zero,
            MacOsWindowDelegateProxy.AcquireForwardingTarget(proxy.ProxyHandle, OtherSelector));
        Assert.Equal(interop.AcquireCount, interop.ReleaseCount);
    }

    [Fact]
    public void Dispose_RestoresExistingDelegateAndDestroysProxy()
    {
        var interop = new FakeDelegateInterop(Window, OriginalDelegate);
        var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => { });
        proxy.Install();
        var proxyHandle = proxy.ProxyHandle;
        Assert.Equal(proxyHandle, interop.GetWindowDelegate(Window));

        proxy.Dispose();

        Assert.Equal(OriginalDelegate, interop.GetWindowDelegate(Window));
        Assert.Contains(proxyHandle, interop.DestroyedProxies);
        Assert.Equal(interop.AcquireCount, interop.ReleaseCount);
    }

    [Fact]
    public void Dispose_DoesNotOverwriteDelegateInstalledAfterProxy()
    {
        var interop = new FakeDelegateInterop(Window, OriginalDelegate);
        var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => { });
        proxy.Install();
        var proxyHandle = proxy.ProxyHandle;
        interop.TrySetWindowDelegate(Window, ReplacementDelegate);

        proxy.Dispose();

        Assert.Equal(ReplacementDelegate, interop.GetWindowDelegate(Window));
        Assert.Contains(proxyHandle, interop.DestroyedProxies);
    }

    [Fact]
    public void Dispose_WhenDelegateRestorationFails_DoesNotReleaseInstalledProxy()
    {
        var interop = new FakeDelegateInterop(Window, OriginalDelegate);
        var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => { });
        proxy.Install();
        var proxyHandle = proxy.ProxyHandle;
        interop.AllowDelegateChanges = false;

        proxy.Dispose();

        Assert.Equal(proxyHandle, interop.GetWindowDelegate(Window));
        Assert.DoesNotContain(proxyHandle, interop.DestroyedProxies);
    }

    [Fact]
    public void DeallocatedForwardDelegate_IsNeverMessagedAndRestoresNil()
    {
        var calls = new List<string>();
        var interop = new FakeDelegateInterop(Window, OriginalDelegate, calls);
        interop.AddSelector(OriginalDelegate, FailureSelector);
        var proxy = new MacOsWindowDelegateProxy(Window, interop, (_, _) => calls.Add("coordinator"));
        proxy.Install();
        interop.Deallocate(OriginalDelegate);

        MacOsWindowDelegateProxy.DispatchFailure(
            proxy.ProxyHandle,
            FailureSelector,
            Window,
            NativeFullScreenTransitionKind.DidFailToEnter);
        proxy.Dispose();

        Assert.Equal(["coordinator"], calls);
        Assert.Equal(IntPtr.Zero, interop.GetWindowDelegate(Window));
        Assert.Equal(interop.AcquireCount, interop.ReleaseCount);
    }

    [Fact]
    public void FailureSelectorNames_MatchNSWindowDelegateContract()
    {
        Assert.Equal(
            "windowDidFailToEnterFullScreen:",
            MacOsWindowDelegateProxy.DidFailToEnterSelector);
        Assert.Equal(
            "windowDidFailToExitFullScreen:",
            MacOsWindowDelegateProxy.DidFailToExitSelector);
    }

    private sealed class FakeDelegateInterop : IMacOsWindowDelegateInterop
    {
        private readonly Dictionary<IntPtr, IntPtr> _windowDelegates = [];
        private readonly Dictionary<IntPtr, IntPtr> _forwardDelegates = [];
        private readonly HashSet<(IntPtr Target, IntPtr Selector)> _selectors = [];
        private readonly HashSet<(IntPtr Target, IntPtr Selector)> _forwardedSelectors = [];
        private readonly HashSet<IntPtr> _alive = [];
        private readonly List<string>? _calls;
        private long _nextProxy = 100;

        public FakeDelegateInterop(
            IntPtr window,
            IntPtr originalDelegate,
            List<string>? calls = null)
        {
            _windowDelegates[window] = originalDelegate;
            _alive.Add(originalDelegate);
            _calls = calls;
        }

        public int AcquireCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public HashSet<IntPtr> DestroyedProxies { get; } = [];

        public bool AllowDelegateChanges { get; set; } = true;

        public bool CreateSelfForwardingProxy { get; set; }

        public void AddSelector(IntPtr target, IntPtr selector) =>
            _selectors.Add((target, selector));

        public void AddForwardedSelector(IntPtr target, IntPtr selector) =>
            _forwardedSelectors.Add((target, selector));

        public bool WouldRespondToSelector(IntPtr target, IntPtr selector) =>
            _alive.Contains(target)
            && (_selectors.Contains((target, selector))
                || _forwardedSelectors.Contains((target, selector)));

        public void Deallocate(IntPtr target) => _alive.Remove(target);

        public IntPtr GetWindowDelegate(IntPtr windowHandle) =>
            _windowDelegates.GetValueOrDefault(windowHandle);

        public IntPtr AcquireWindowDelegate(IntPtr windowHandle)
        {
            var target = GetWindowDelegate(windowHandle);
            if (target != IntPtr.Zero && _alive.Contains(target))
            {
                AcquireCount++;
                return target;
            }
            return IntPtr.Zero;
        }

        public IntPtr CreateProxy(IntPtr forwardDelegate)
        {
            var proxy = new IntPtr(_nextProxy++);
            _forwardDelegates[proxy] = CreateSelfForwardingProxy ? proxy : forwardDelegate;
            _alive.Add(proxy);
            return proxy;
        }

        public bool TrySetWindowDelegate(IntPtr windowHandle, IntPtr windowDelegate)
        {
            if (!AllowDelegateChanges)
            {
                return false;
            }
            _windowDelegates[windowHandle] = windowDelegate;
            return true;
        }

        public IntPtr AcquireForwardDelegate(IntPtr proxy)
        {
            var target = _forwardDelegates.GetValueOrDefault(proxy);
            if (target == IntPtr.Zero || !_alive.Contains(target))
            {
                return IntPtr.Zero;
            }

            AcquireCount++;
            return target;
        }

        public bool ImplementsSelector(IntPtr target, IntPtr selector) =>
            _alive.Contains(target) && _selectors.Contains((target, selector));

        public void SendWindowDelegateCallback(
            IntPtr target,
            IntPtr selector,
            IntPtr windowHandle) =>
            _calls?.Add($"forward:{target}:{selector}:{windowHandle}");

        public void Release(IntPtr target)
        {
            if (target != IntPtr.Zero)
            {
                ReleaseCount++;
            }
        }

        public void DestroyProxy(IntPtr proxy)
        {
            DestroyedProxies.Add(proxy);
            _alive.Remove(proxy);
            _forwardDelegates.Remove(proxy);
        }
    }
}
