using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

public sealed class WindowCloseToHideCoordinatorTests
{
    [Fact]
    public void WindowedClose_CancelsAndHidesImmediately()
    {
        var calls = new List<string>();
        var window = new FakeWindow(calls);
        var transition = new FakeFullScreenTransition(NativeFullScreenState.Windowed, calls);
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            persistWindowState: () => calls.Add("persist"),
            hiding: () => calls.Add("hiding"));

        var cancel = coordinator.HandleCloseRequest();

        Assert.True(cancel);
        Assert.Equal(["observe", "hiding", "owned", "persist", "hide"], calls);
        Assert.Equal(1, window.HideCount);
        Assert.Equal(0, transition.ExitRequestCount);
    }

    [Fact]
    public void FullScreenClose_HidesOnlyAfterExactNativeDidExitNotification()
    {
        var calls = new List<string>();
        var window = new FakeWindow(calls);
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen, calls);
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: scheduler);

        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(1, transition.ExitRequestCount);
        Assert.Equal(0, window.HideCount);

        transition.Raise(NativeFullScreenTransitionKind.WillExit);
        Assert.Equal(0, window.HideCount);

        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        Assert.Equal(1, window.HideCount);
        Assert.Equal("hide", calls[^1]);
        scheduler.FireNext(includeCanceled: true);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void RepeatedFullScreenClose_CoalescesNativeExitAndHide()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen);
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: scheduler);

        Assert.True(coordinator.HandleCloseRequest());
        Assert.True(coordinator.HandleCloseRequest());
        Assert.True(coordinator.HandleCloseRequest());

        Assert.Equal(1, transition.ExitRequestCount);
        Assert.Equal(1, scheduler.ScheduleCount);
        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void SynchronousSuccessfulExit_WinsBeforeFallbackIsScheduled()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen)
        {
            TransitionRaisedByExitRequest = NativeFullScreenTransitionKind.DidExit,
        };
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: scheduler);

        Assert.True(coordinator.HandleCloseRequest());

        Assert.Equal(1, window.HideCount);
        Assert.Equal(0, scheduler.ScheduleCount);
    }

    [Fact]
    public void FullScreenNotification_ForAnotherWindowIsIgnored()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen);
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: new FakeReconciliationScheduler());
        coordinator.HandleCloseRequest();

        transition.Raise(
            NativeFullScreenTransitionKind.DidExit,
            new IntPtr(transition.WindowHandle.ToInt64() + 1));

        Assert.Equal(0, window.HideCount);
        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void ReopenWhileExitPending_CancelsDeferredHide()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen);
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: scheduler);
        coordinator.HandleCloseRequest();

        coordinator.NotifyShownOrActivated();
        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        scheduler.FireNext(includeCanceled: true);

        Assert.Equal(0, window.HideCount);
        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void Shutdown_DisposesObserverBeforeRealCloseAndNeverHides()
    {
        var calls = new List<string>();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen, calls);
        var window = new FakeWindow(calls, () => Assert.True(transition.IsDisposed));
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            persistWindowState: () => calls.Add("persist"),
            reconciliationScheduler: scheduler);
        coordinator.HandleCloseRequest();

        coordinator.CloseForShutdown();

        Assert.Equal(1, window.CloseCount);
        Assert.Equal(0, window.HideCount);
        Assert.True(transition.IsDisposed);
        Assert.True(calls.IndexOf("dispose") < calls.IndexOf("close"));
        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        scheduler.FireNext(includeCanceled: true);
        Assert.Equal(0, window.HideCount);
        Assert.False(coordinator.HandleCloseRequest());
    }

    [Fact]
    public void CloseDuringEnter_WaitsForDidEnterThenRequestsExit()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.Entering);
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: new FakeReconciliationScheduler());

        coordinator.HandleCloseRequest();
        Assert.Equal(0, transition.ExitRequestCount);

        transition.Raise(NativeFullScreenTransitionKind.DidEnter);
        Assert.Equal(1, transition.ExitRequestCount);
        Assert.Equal(0, window.HideCount);

        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void CloseDuringExit_WaitsForExistingExit()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.Exiting);
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: new FakeReconciliationScheduler());

        coordinator.HandleCloseRequest();

        Assert.Equal(0, transition.ExitRequestCount);
        Assert.Equal(0, window.HideCount);
        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void FailedEnter_HidesWindowedWindowWhileFailedExitKeepsFullScreenWindowVisible()
    {
        var enterWindow = new FakeWindow();
        var enterTransition = new FakeFullScreenTransition(NativeFullScreenState.Entering);
        var enterScheduler = new FakeReconciliationScheduler();
        using var enterCoordinator = new WindowCloseToHideCoordinator(
            enterWindow,
            enterTransition,
            reconciliationScheduler: enterScheduler);
        enterCoordinator.HandleCloseRequest();

        enterTransition.Raise(NativeFullScreenTransitionKind.DidFailToEnter);
        enterScheduler.FireNext(includeCanceled: true);

        Assert.Equal(1, enterWindow.HideCount);

        var exitWindow = new FakeWindow();
        var exitTransition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen);
        var exitScheduler = new FakeReconciliationScheduler();
        using var exitCoordinator = new WindowCloseToHideCoordinator(
            exitWindow,
            exitTransition,
            reconciliationScheduler: exitScheduler);
        exitCoordinator.HandleCloseRequest();

        exitTransition.Raise(NativeFullScreenTransitionKind.DidFailToExit);
        exitScheduler.FireNext(includeCanceled: true);

        Assert.Equal(0, exitWindow.HideCount);
        Assert.True(exitCoordinator.HandleCloseRequest());
        Assert.Equal(2, exitTransition.ExitRequestCount);
    }

    [Fact]
    public void NativeExitRequestFailure_KeepsWindowVisibleAndAllowsRetry()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen)
        {
            ExitRequestSucceeds = false,
        };
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: new FakeReconciliationScheduler());

        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(0, window.HideCount);

        transition.ExitRequestSucceeds = true;
        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(2, transition.ExitRequestCount);
        transition.Raise(NativeFullScreenTransitionKind.DidExit);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void MissingTerminalCallback_ReconcilesWindowedStateAndHides()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen);
        transition.ReconciledStates.Enqueue(NativeFullScreenState.Windowed);
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: scheduler);

        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(0, window.HideCount);

        scheduler.FireNext();

        Assert.Equal(1, transition.ReconcileCount);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void MissingTerminalCallback_BoundsTransitionPollingAndAllowsAnotherClose()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.Exiting);
        transition.ReconciledStates.Enqueue(NativeFullScreenState.Exiting);
        transition.ReconciledStates.Enqueue(NativeFullScreenState.Exiting);
        transition.ReconciledStates.Enqueue(NativeFullScreenState.Exiting);
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            reconciliationScheduler: scheduler);

        Assert.True(coordinator.HandleCloseRequest());
        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(1, scheduler.ScheduleCount);

        scheduler.FireNext();
        scheduler.FireNext();
        scheduler.FireNext();

        Assert.Equal(3, transition.ReconcileCount);
        Assert.Equal(0, window.HideCount);
        Assert.True(coordinator.HandleCloseRequest());
        Assert.Equal(4, scheduler.ScheduleCount);
    }

    [Fact]
    public void MainPolicyDisabled_AllowsExistingNonMacClosePath()
    {
        var window = new FakeWindow();
        var transition = new FakeFullScreenTransition(NativeFullScreenState.Windowed);
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            hideOnClose: false);

        Assert.False(coordinator.HandleCloseRequest());
        Assert.Equal(0, window.HideCount);
        Assert.Equal(0, transition.StartObservingCount);
    }

    [Fact]
    public void Hide_ClosesAboutAndOwnedDialogsBeforeOwnerWindow()
    {
        var calls = new List<string>();
        var aboutOpen = true;
        var window = new FakeWindow(calls, beforeHide: () => Assert.False(aboutOpen));
        var transition = new FakeFullScreenTransition(NativeFullScreenState.FullScreen, calls);
        var scheduler = new FakeReconciliationScheduler();
        using var coordinator = new WindowCloseToHideCoordinator(
            window,
            transition,
            hiding: () =>
            {
                aboutOpen = false;
                calls.Add("about");
            },
            reconciliationScheduler: scheduler);
        coordinator.HandleCloseRequest();

        transition.Raise(NativeFullScreenTransitionKind.DidExit);

        Assert.Equal(["observe", "exit", "about", "owned", "hide"], calls);
    }

    private sealed class FakeWindow(
        List<string>? calls = null,
        Action? beforeClose = null,
        Action? beforeHide = null) : ICloseToHideWindow
    {
        public int HideCount { get; private set; }

        public int CloseCount { get; private set; }

        public void CloseOwnedWindows() => calls?.Add("owned");

        public void Hide()
        {
            beforeHide?.Invoke();
            HideCount++;
            calls?.Add("hide");
        }

        public void Close()
        {
            beforeClose?.Invoke();
            CloseCount++;
            calls?.Add("close");
        }
    }

    private sealed class FakeFullScreenTransition(
        NativeFullScreenState state,
        List<string>? calls = null) : INativeFullScreenTransition
    {
        public event EventHandler<NativeFullScreenTransitionEventArgs>? Changed;

        public IntPtr WindowHandle { get; } = new(42);

        public NativeFullScreenState State { get; private set; } = state;

        public int StartObservingCount { get; private set; }

        public int ExitRequestCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool ExitRequestSucceeds { get; set; } = true;

        public int ReconcileCount { get; private set; }

        public Queue<NativeFullScreenState> ReconciledStates { get; } = new();

        public NativeFullScreenTransitionKind? TransitionRaisedByExitRequest { get; set; }

        public void StartObserving()
        {
            StartObservingCount++;
            calls?.Add("observe");
        }

        public bool RequestExitFullScreen()
        {
            ExitRequestCount++;
            calls?.Add("exit");
            if (ExitRequestSucceeds)
            {
                State = NativeFullScreenState.Exiting;
                if (TransitionRaisedByExitRequest is { } transition)
                {
                    Raise(transition);
                }
            }
            return ExitRequestSucceeds;
        }

        public NativeFullScreenState ReconcileState()
        {
            ReconcileCount++;
            return ReconciledStates.TryDequeue(out var reconciled) ? reconciled : State;
        }

        public void Raise(NativeFullScreenTransitionKind kind, IntPtr? windowHandle = null)
        {
            if (windowHandle is null || windowHandle == WindowHandle)
            {
                State = kind switch
                {
                    NativeFullScreenTransitionKind.WillEnter => NativeFullScreenState.Entering,
                    NativeFullScreenTransitionKind.DidEnter => NativeFullScreenState.FullScreen,
                    NativeFullScreenTransitionKind.DidFailToEnter => NativeFullScreenState.Windowed,
                    NativeFullScreenTransitionKind.WillExit => NativeFullScreenState.Exiting,
                    NativeFullScreenTransitionKind.DidExit => NativeFullScreenState.Windowed,
                    NativeFullScreenTransitionKind.DidFailToExit => NativeFullScreenState.FullScreen,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
            }

            Changed?.Invoke(
                this,
                new NativeFullScreenTransitionEventArgs(windowHandle ?? WindowHandle, kind));
        }

        public void Dispose()
        {
            IsDisposed = true;
            calls?.Add("dispose");
        }
    }

    private sealed class FakeReconciliationScheduler : IFullScreenReconciliationScheduler
    {
        private readonly Queue<ScheduledCallback> _callbacks = new();

        public int ScheduleCount { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            ScheduleCount++;
            var scheduled = new ScheduledCallback(callback);
            _callbacks.Enqueue(scheduled);
            return scheduled;
        }

        public void FireNext(bool includeCanceled = false)
        {
            while (_callbacks.TryDequeue(out var scheduled))
            {
                if (!scheduled.IsCanceled || includeCanceled)
                {
                    scheduled.Invoke();
                    return;
                }
            }

            throw new InvalidOperationException("No scheduled reconciliation callback is available.");
        }

        private sealed class ScheduledCallback(Action callback) : IDisposable
        {
            public bool IsCanceled { get; private set; }

            public void Dispose() => IsCanceled = true;

            public void Invoke() => callback();
        }
    }
}
