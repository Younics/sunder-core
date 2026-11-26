using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Package.Format;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppBrowserManagerTests
{
    [Fact]
    public Task UnregisteringWebPackageDrainsItsNativeLeaseWithoutPreparingItForReuse() => RunAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("sunder-unregister-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "<!doctype html>");
            await using var server = await AppWebContentServer.StartAsync(root.FullName,
                new SunderPackageTargetManifest { EntryPoint = "index.html" }, default);
            var ui = new InlineDispatcher();
            var created = new List<Browser>();
            await using var manager = new AppBrowserManager(ui, _ => { var browser = new Browser(); created.Add(browser); return browser; });
            var registry = new AppPackageViewRegistry(ui);
            var catalog = AppWebRpcContractCatalog.Load(root.FullName, new SunderPackageManifest { ContractBundles = [] },
                new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, []));
            registry.RegisterPackageView("target", new PackageViewRegistration("view", "View"), () =>
                new AppWebPackageViewControl(server.BaseUri, Path.Combine(root.FullName, "target"), server, manager,
                    UnavailableAppWebRpcClient.Instance, catalog, new ExternalBrowserService()), isWebView: true);
            var view = Assert.IsType<AppWebPackageViewControl>(registry.GetOrCreateView("view", _ => false, (_, _, _) => false));
            var sibling = manager.Create(Path.Combine(root.FullName, "sibling"));
            var nativeExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            created[0].NativeDrain = nativeExit.Task;
            try
            {
                Assert.Equal(["view"], registry.UnregisterPackage("target"));
                Assert.Equal(0, created[0].Resets);
                Assert.True(created[0].Closed);
                Assert.Null(view.Content);
                Assert.False(view.Retired.IsCompleted);
                Assert.False(created[1].Closed);
            }
            finally { nativeExit.TrySetResult(); }
            await view.Retired;
            Assert.Equal(1, manager.NativeSessionCount);
            await sibling.RetireAsync();
        }
        finally { root.Delete(recursive: true); }
    });

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

    [Fact]
    public Task ReopenWaitsForCleanupAndSkippedReopensNeverCreateNativeViews() => RunAsync(async () =>
    {
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new List<Browser>();
        await using var manager = new AppBrowserManager(new InlineDispatcher(), _ =>
        {
            var browser = new Browser { DocumentDrain = reset.Task, OnNavigate = () => navigated.TrySetResult() };
            created.Add(browser); return browser;
        });
        var key = Path.Combine(Path.GetTempPath(), "browser-pending-reopen");
        var old = manager.Create(key);
        var documentCleanup = old.RetireDocumentAsync();
        Assert.False(documentCleanup.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => old.Navigate(new Uri("https://retired.invalid")));
        var skipped = new List<Task>();
        for (var i = 0; i < 12; i++)
        {
            var abandoned = manager.Create(key);
            abandoned.Navigate(new Uri("https://abandoned.invalid"));
            skipped.Add(abandoned.RetireAsync());
        }
        var reopened = manager.Create(key);
        reopened.Navigate(new Uri("https://current.invalid"));
        Assert.Single(created);
        Assert.False(navigated.Task.IsCompleted);
        var oldCleanup = old.RetireAsync();
        reset.SetResult();
        await Task.WhenAll(skipped.Append(oldCleanup));
        await navigated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(created); // The cleaned original is reused, not a replacement for every click.
        Assert.False(created[0].Closed);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var nextReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            created[0].DocumentDrain = nextReset.Task;
            navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = reopened.RetireDocumentAsync();
            var next = manager.Create(key);
            next.Navigate(new Uri("https://current.invalid"));
            var previousCleanup = reopened.RetireAsync();
            nextReset.SetResult();
            await previousCleanup;
            await navigated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Same(created[0].Control, Assert.IsType<ContentControl>(next.Control).Content);
            reopened = next;
        }
        await reopened.RetireAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WatchdogTimeoutRetainsTheActualDrainAndNeverClaimsCompletion(bool delayEngine) => RunAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser { Reusable = false, NativeDrain = delayEngine ? Task.CompletedTask : release.Task };
        var engineEntered = false;
        var manager = new AppBrowserManager(new InlineDispatcher(), _ => browser, () =>
        {
            engineEntered = true;
            return delayEngine ? release.Task : Task.CompletedTask;
        });
        var lease = manager.Create(Path.Combine(Path.GetTempPath(), "browser-watchdog"));
        var cleanup = manager.DisposeAsync().AsTask();
        Assert.False(lease.CanCapture);
        Assert.Throws<ObjectDisposedException>(() => lease.Navigate(new Uri("https://retired.invalid")));
        Assert.False(await BoundedCleanup.RunAsync("testing retained browser cleanup", () => cleanup, TimeSpan.FromTicks(1)));
        Assert.False(cleanup.IsCompleted);
        Assert.True(AppCleanupQuarantine.IsRetained(cleanup));
        Assert.Same(cleanup, manager.DisposeAsync().AsTask());
        Assert.Equal(delayEngine, engineEntered);
        release.SetResult();
        await cleanup;
        Assert.True(engineEntered);
        Assert.Equal(0, manager.NativeSessionCount);
    });

    [Fact]
    public Task FailedPredecessorDoesNotCreateOrPublishAReplacement() => RunAsync(async () =>
    {
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var manager = new AppBrowserManager(new InlineDispatcher(), _ =>
        {
            created++;
            return new Browser { DocumentDrain = reset.Task };
        });
        var key = Path.Combine(Path.GetTempPath(), "browser-failed-reopen");
        var first = manager.Create(key);
        var documentCleanup = first.RetireDocumentAsync();
        var reopened = manager.Create(key);
        reopened.Failed += (_, _) => failure.TrySetResult();
        var cleanup = first.RetireAsync();
        reset.SetException(new InvalidOperationException("native reset failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => documentCleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup);
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, created);
        Assert.False(reopened.CanCapture);
        await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
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
        internal Task DocumentDrain = Task.CompletedTask;
        internal Action? OnNavigate;
        public Task ResetAsync() { Resets++; return FailReset ? Task.FromException(new InvalidOperationException("reset failed")) : DocumentDrain; }
        public Task DrainNativeRetirementAsync() => NativeDrain;
        public void Dispose() => Closed = true;
        public void Navigate(Uri uri) => OnNavigate?.Invoke();
        public void Stop() { }
        public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>("true");
        public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting { add { } remove { } }
        public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested { add { } remove { } }
        public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested { add { } remove { } }
        public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
    }
}
