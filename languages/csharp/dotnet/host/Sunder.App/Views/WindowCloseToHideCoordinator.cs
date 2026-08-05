using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sunder.App.Views;

internal sealed class WindowCloseToHideCoordinator : IDisposable
{
    private const int MaximumReconciliationAttempts = 3;
    private static readonly TimeSpan ReconciliationDelay = TimeSpan.FromSeconds(2);
    private static readonly ConditionalWeakTable<Window, WindowCloseToHideCoordinator> Coordinators = new();
    private readonly ICloseToHideWindow _window;
    private readonly INativeFullScreenTransition _fullScreenTransition;
    private readonly bool _hideOnClose;
    private readonly bool _closeOnEscape;
    private readonly Action _persistWindowState;
    private readonly Action _hiding;
    private readonly Action _closed;
    private readonly Func<bool> _isCloseBlocked;
    private readonly IFullScreenReconciliationScheduler _reconciliationScheduler;
    private readonly Window? _avaloniaWindow;
    private IDisposable? _scheduledReconciliation;
    private int _reconciliationAttempts;
    private int _reconciliationGeneration;
    private bool _deferredHide;
    private bool _exitRequested;
    private bool _shutdown;
    private bool _disposed;

    public WindowCloseToHideCoordinator(
        Window window,
        bool hideOnClose,
        bool closeOnEscape = false,
        Action? persistWindowState = null,
        Action? hiding = null,
        Action? closed = null,
        INativeFullScreenTransition? fullScreenTransition = null,
        IFullScreenReconciliationScheduler? reconciliationScheduler = null,
        Func<bool>? isCloseBlocked = null)
        : this(
            new AvaloniaCloseToHideWindow(window),
            fullScreenTransition ?? MacOsNativeFullScreenTransition.Create(window),
            hideOnClose,
            persistWindowState,
            hiding,
            closed,
            reconciliationScheduler ?? DispatcherFullScreenReconciliationScheduler.Instance,
            isCloseBlocked)
    {
        _avaloniaWindow = window;
        _closeOnEscape = closeOnEscape;
        Coordinators.Add(window, this);
        window.Opened += Window_OnShownOrActivated;
        window.Activated += Window_OnShownOrActivated;
        window.KeyDown += Window_OnKeyDown;
        window.Closing += Window_OnClosing;
        window.Closed += Window_OnClosed;
    }

    internal WindowCloseToHideCoordinator(
        ICloseToHideWindow window,
        INativeFullScreenTransition fullScreenTransition,
        bool hideOnClose = true,
        Action? persistWindowState = null,
        Action? hiding = null,
        Action? closed = null,
        IFullScreenReconciliationScheduler? reconciliationScheduler = null,
        Func<bool>? isCloseBlocked = null)
    {
        _window = window;
        _fullScreenTransition = fullScreenTransition;
        _hideOnClose = hideOnClose;
        _persistWindowState = persistWindowState ?? (static () => { });
        _hiding = hiding ?? (static () => { });
        _closed = closed ?? (static () => { });
        _isCloseBlocked = isCloseBlocked ?? (static () => false);
        _reconciliationScheduler =
            reconciliationScheduler ?? DispatcherFullScreenReconciliationScheduler.Instance;
        _fullScreenTransition.Changed += FullScreenTransition_OnChanged;
    }

    internal bool HandleCloseRequest()
    {
        if (_shutdown || _disposed || !_hideOnClose)
        {
            return false;
        }

        if (_isCloseBlocked())
        {
            return true;
        }

        _fullScreenTransition.StartObserving();
        if (_deferredHide)
        {
            return true;
        }

        switch (_fullScreenTransition.State)
        {
            case NativeFullScreenState.Windowed:
                HideNow();
                break;
            case NativeFullScreenState.Entering:
                BeginDeferredHide();
                break;
            case NativeFullScreenState.FullScreen:
                _deferredHide = true;
                RequestNativeExit();
                break;
            case NativeFullScreenState.Exiting:
                BeginDeferredHide();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return true;
    }

    internal bool IsHidePending => _deferredHide;

    internal void NotifyShownOrActivated()
    {
        if (_shutdown || _disposed)
        {
            return;
        }

        _fullScreenTransition.StartObserving();
        CancelDeferredHide();
    }

    public void CloseForShutdown()
    {
        if (_shutdown || _disposed)
        {
            return;
        }

        _shutdown = true;
        CancelDeferredHide();
        _fullScreenTransition.Changed -= FullScreenTransition_OnChanged;
        _fullScreenTransition.Dispose();
        try
        {
            _window.CloseOwnedWindows();
            _persistWindowState();
        }
        finally
        {
            _window.Close();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDeferredHide();
        _fullScreenTransition.Changed -= FullScreenTransition_OnChanged;
        _fullScreenTransition.Dispose();
        if (_avaloniaWindow is not null)
        {
            Coordinators.Remove(_avaloniaWindow);
            _avaloniaWindow.Opened -= Window_OnShownOrActivated;
            _avaloniaWindow.Activated -= Window_OnShownOrActivated;
            _avaloniaWindow.KeyDown -= Window_OnKeyDown;
            _avaloniaWindow.Closing -= Window_OnClosing;
            _avaloniaWindow.Closed -= Window_OnClosed;
        }
    }

    internal static void PrepareToShow(Window window)
    {
        if (Coordinators.TryGetValue(window, out var coordinator))
        {
            coordinator.NotifyShownOrActivated();
        }
    }

    private void RequestNativeExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        if (!_fullScreenTransition.RequestExitFullScreen())
        {
            CancelDeferredHide();
            return;
        }

        if (!_deferredHide || _shutdown || _disposed)
        {
            return;
        }

        RestartReconciliation();
    }

    private void FullScreenTransition_OnChanged(
        object? sender,
        NativeFullScreenTransitionEventArgs e)
    {
        if (
            _shutdown
            || _disposed
            || e.WindowHandle != _fullScreenTransition.WindowHandle
        )
        {
            return;
        }

        switch (e.Kind)
        {
            case NativeFullScreenTransitionKind.WillEnter:
            case NativeFullScreenTransitionKind.WillExit:
                return;
            case NativeFullScreenTransitionKind.DidEnter:
                if (_deferredHide)
                {
                    RequestNativeExit();
                }
                return;
            case NativeFullScreenTransitionKind.DidExit:
            case NativeFullScreenTransitionKind.DidFailToEnter:
                if (_deferredHide)
                {
                    HideNow();
                }
                return;
            case NativeFullScreenTransitionKind.DidFailToExit:
                CancelDeferredHide();
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void HideNow()
    {
        CancelDeferredHide();
        try
        {
            _hiding();
            _window.CloseOwnedWindows();
            _persistWindowState();
        }
        finally
        {
            _window.Hide();
        }
    }

    private void BeginDeferredHide()
    {
        _deferredHide = true;
        _exitRequested = false;
        RestartReconciliation();
    }

    private void RestartReconciliation()
    {
        _reconciliationAttempts = 0;
        ScheduleReconciliation();
    }

    private void ScheduleReconciliation()
    {
        CancelScheduledReconciliation();
        var generation = _reconciliationGeneration;
        _scheduledReconciliation = _reconciliationScheduler.Schedule(
            ReconciliationDelay,
            () => ReconcileDeferredHide(generation));
    }

    private void ReconcileDeferredHide(int generation)
    {
        if (
            generation != _reconciliationGeneration
            || !_deferredHide
            || _shutdown
            || _disposed
        )
        {
            return;
        }

        _scheduledReconciliation = null;
        NativeFullScreenState state;
        try
        {
            state = _fullScreenTransition.ReconcileState();
        }
        catch
        {
            CancelDeferredHide();
            return;
        }

        switch (state)
        {
            case NativeFullScreenState.Windowed:
                HideNow();
                return;
            case NativeFullScreenState.FullScreen:
                if (_exitRequested)
                {
                    CancelDeferredHide();
                }
                else
                {
                    RequestNativeExit();
                }
                return;
            case NativeFullScreenState.Entering:
            case NativeFullScreenState.Exiting:
                _reconciliationAttempts++;
                if (_reconciliationAttempts >= MaximumReconciliationAttempts)
                {
                    CancelDeferredHide();
                }
                else
                {
                    ScheduleReconciliation();
                }
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void CancelDeferredHide()
    {
        _deferredHide = false;
        _exitRequested = false;
        _reconciliationAttempts = 0;
        CancelScheduledReconciliation();
    }

    private void CancelScheduledReconciliation()
    {
        _reconciliationGeneration++;
        var scheduled = _scheduledReconciliation;
        _scheduledReconciliation = null;
        scheduled?.Dispose();
    }

    private void Window_OnShownOrActivated(object? sender, EventArgs e) => NotifyShownOrActivated();

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var closeFromKeyboard =
            OperatingSystem.IsMacOS()
            && e.Key == Key.W
            && (e.KeyModifiers & KeyModifiers.Meta) != 0;
        if (!closeFromKeyboard && !(_closeOnEscape && e.Key == Key.Escape))
        {
            return;
        }

        e.Handled = true;
        _avaloniaWindow?.Close();
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (HandleCloseRequest())
        {
            e.Cancel = true;
        }
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _closed();
        }
        finally
        {
            Dispose();
        }
    }
}

internal interface ICloseToHideWindow
{
    void CloseOwnedWindows();

    void Hide();

    void Close();
}

internal sealed class AvaloniaCloseToHideWindow(Window window) : ICloseToHideWindow
{
    public void CloseOwnedWindows()
    {
        foreach (var ownedWindow in window.OwnedWindows.ToArray())
        {
            ownedWindow.Close();
        }
    }

    public void Hide() => window.Hide();

    public void Close() => window.Close();
}

internal enum NativeFullScreenState
{
    Windowed,
    Entering,
    FullScreen,
    Exiting,
}

internal enum NativeFullScreenTransitionKind
{
    WillEnter,
    DidEnter,
    DidFailToEnter,
    WillExit,
    DidExit,
    DidFailToExit,
}

internal sealed class NativeFullScreenTransitionEventArgs(
    IntPtr windowHandle,
    NativeFullScreenTransitionKind kind) : EventArgs
{
    public IntPtr WindowHandle { get; } = windowHandle;

    public NativeFullScreenTransitionKind Kind { get; } = kind;
}

internal interface INativeFullScreenTransition : IDisposable
{
    event EventHandler<NativeFullScreenTransitionEventArgs>? Changed;

    IntPtr WindowHandle { get; }

    NativeFullScreenState State { get; }

    NativeFullScreenState ReconcileState();

    void StartObserving();

    bool RequestExitFullScreen();
}

internal interface IFullScreenReconciliationScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class DispatcherFullScreenReconciliationScheduler
    : IFullScreenReconciliationScheduler
{
    public static DispatcherFullScreenReconciliationScheduler Instance { get; } = new();

    private DispatcherFullScreenReconciliationScheduler() { }

    public IDisposable Schedule(TimeSpan delay, Action callback) =>
        new DispatcherScheduledAction(delay, callback);

    private sealed class DispatcherScheduledAction : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private Action? _callback;

        public DispatcherScheduledAction(TimeSpan delay, Action callback)
        {
            _callback = callback;
            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += Timer_OnTick;
            _timer.Start();
        }

        public void Dispose()
        {
            _callback = null;
            _timer.Stop();
            _timer.Tick -= Timer_OnTick;
        }

        private void Timer_OnTick(object? sender, EventArgs e)
        {
            var callback = _callback;
            Dispose();
            callback?.Invoke();
        }
    }
}
