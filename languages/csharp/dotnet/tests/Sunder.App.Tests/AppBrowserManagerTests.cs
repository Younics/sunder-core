using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppBrowserManagerTests
{
    [Fact]
    public Task NoDemandDoesNotCreateBrowserAndOnlyCleanSameViewIsReused() => RunAsync(async () =>
    {
        var created = new List<Browser>();
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ => { var view = new Browser(); created.Add(view); return view; });
        Assert.Empty(created);
        var first = manager.Create(Path.Combine(Path.GetTempPath(), "browser-owner", "a"));
        Assert.Single(created);
        var control = first.Control;
        await first.RetireAsync();
        Assert.Equal(1, created[0].Resets);
        Assert.False(created[0].Closed);
        var second = manager.Create(Path.Combine(Path.GetTempPath(), "browser-owner", "a"));
        Assert.Same(control, second.Control);
        var sibling = manager.Create(Path.Combine(Path.GetTempPath(), "browser-owner", "b"));
        Assert.Equal(2, created.Count);
        Assert.NotSame(control, sibling.Control);
        await second.RetireAsync();
        await sibling.RetireAsync();
        Assert.True(created[0].Closed); // The single spare was replaced, not accumulated.
        Assert.False(created[1].Closed);
        await manager.RetireActivationAsync(Path.Combine(Path.GetTempPath(), "browser-owner"));
        Assert.True(created[1].Closed);
    });

    [Fact]
    public Task FailedResetIsNeverCachedAndActivationRetirementPreservesSibling() => RunAsync(async () =>
    {
        var created = new List<Browser>();
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ => { var view = new Browser(); created.Add(view); return view; });
        var aRoot = Path.Combine(Path.GetTempPath(), "owner-a");
        var a = manager.Create(Path.Combine(aRoot, "view"));
        var b = manager.Create(Path.Combine(Path.GetTempPath(), "owner-b", "view"));
        created[0].FailReset = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.RetireAsync());
        Assert.True(created[0].Closed);
        Assert.False(created[1].Closed);
        var replacement = manager.Create(Path.Combine(aRoot, "view"));
        Assert.Equal(3, created.Count);
        await manager.RetireActivationAsync(aRoot);
        Assert.True(created[2].Closed);
        Assert.False(created[1].Closed);
        Assert.Throws<ObjectDisposedException>(() => manager.Create(Path.Combine(aRoot, "another")));
        await b.RetireAsync();
        await replacement.RetireAsync();
    });

    [Fact]
    public Task SynchronousSessionClosureCannotMutateTheShutdownEnumeration() => RunAsync(async () =>
    {
        var closed = new List<Browser>();
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ =>
        {
            var browser = new Browser { Reusable = false }; closed.Add(browser); return browser;
        });
        manager.Create(Path.Combine(Path.GetTempPath(), "first"));
        manager.Create(Path.Combine(Path.GetTempPath(), "second"));
        await manager.DisposeAsync();
        Assert.All(closed, browser => Assert.True(browser.Closed));
        Assert.Equal(0, manager.NativeSessionCount);
    });

    [Fact]
    public Task ConcurrentResetAndActivationRetirementCannotOverwriteOrRecacheAView() => RunAsync(async () =>
    {
        var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new List<Browser>();
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ =>
        {
            var browser = new Browser(); created.Add(browser); return browser;
        });
        var root = Path.Combine(Path.GetTempPath(), "browser-concurrent-retirement");
        var first = manager.Create(Path.Combine(root, "first"));
        var second = manager.Create(Path.Combine(root, "second"));
        var third = manager.Create(Path.Combine(root, "third"));
        created[0].NativeDrain = evicted.Task;
        await first.RetireAsync();
        var secondRelease = second.RetireAsync(); // Evicts first and yields.
        Assert.False(secondRelease.IsCompleted);
        await third.RetireAsync(); // Installs a different idle slot.
        var retirement = manager.RetireActivationAsync(root);
        Assert.False(retirement.IsCompleted);
        evicted.SetResult();
        await Task.WhenAll(secondRelease, retirement);
        Assert.All(created, browser => Assert.True(browser.Closed));
        Assert.Equal(0, manager.NativeSessionCount);
    });

    [Fact]
    public Task FailedIdleEvictionDoesNotSkipOtherViewsOrEngineShutdown() => RunAsync(async () =>
    {
        var created = new List<Browser>();
        var engineClosed = false;
        var manager = new AppBrowserManager(new InlineDispatcher(), _ =>
        {
            var browser = new Browser(); created.Add(browser); return browser;
        }, () => { engineClosed = true; return Task.CompletedTask; });
        var idle = manager.Create(Path.Combine(Path.GetTempPath(), "browser-failed-idle"));
        await idle.RetireAsync();
        created[0].NativeDrain = Task.FromException(new InvalidOperationException("native drain failed"));
        manager.Create(Path.Combine(Path.GetTempPath(), "browser-live-sibling"));
        await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
        Assert.All(created, browser => Assert.True(browser.Closed));
        Assert.True(engineClosed);
    });

    [Fact]
    public Task IdleExpiryStillOwnsNativeDrainWhenItsActivationRetires() => RunAsync(async () =>
    {
        var nativeDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser { NativeDrain = nativeDrain.Task };
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ => browser,
            idleRetention: TimeSpan.FromMilliseconds(10));
        var root = Path.Combine(Path.GetTempPath(), "browser-expired-activation");
        var view = manager.Create(Path.Combine(root, "view"));
        await view.RetireAsync();
        while (!browser.Closed) await Task.Delay(5);
        var retirement = manager.RetireActivationAsync(root);
        Assert.False(retirement.IsCompleted);
        nativeDrain.SetResult();
        await retirement;
        Assert.Equal(0, manager.NativeSessionCount);
    });

    [Fact]
    public Task EngineShutdownWaitsForActualSessionDrain() => RunAsync(async () =>
    {
        var nativeDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser { NativeDrain = nativeDrain.Task, Reusable = false };
        var engineClosed = false;
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ => browser, () => { engineClosed = true; return Task.CompletedTask; });
        var view = manager.Create(Path.Combine(Path.GetTempPath(), "browser-drain"));
        var retirement = view.RetireAsync();
        var shutdown = manager.DisposeAsync().AsTask();
        Assert.True(browser.Closed);
        Assert.False(retirement.IsCompleted);
        Assert.False(shutdown.IsCompleted);
        Assert.False(engineClosed);
        nativeDrain.SetResult();
        await Task.WhenAll(retirement, shutdown);
        Assert.True(engineClosed);
    });

    private static Task RunAsync(Func<Task> work)
    {
        var previous = SynchronizationContext.Current;
        using var context = new SingleThreadSynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var task = work(); context.RunUntilCompleted(task); task.GetAwaiter().GetResult();
            return Task.CompletedTask;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
    private sealed class Browser : IAppWebView
    {
        public Control Control { get; } = new Border();
        public bool CanReset => Reusable;
        internal bool Reusable = true, Closed, FailReset;
        internal int Resets;
        internal Task NativeDrain = Task.CompletedTask;
        public Task ResetAsync() { Resets++; return FailReset ? Task.FromException(new InvalidOperationException("reset failed")) : Task.CompletedTask; }
        public Task DrainNativeRetirementAsync() => NativeDrain;
        public void Dispose() => Closed = true;
        public void Navigate(Uri uri) { }
        public void Stop() { }
        public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>("true");
        public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting { add { } remove { } }
        public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested { add { } remove { } }
        public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested { add { } remove { } }
        public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
    }
}
