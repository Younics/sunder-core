using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Services;

// Process/session owner, not a package service. The cache contains at most one
// cleaned native view; neither documents nor activation authority are pooled.
internal sealed class AppBrowserManager : IAppWebViewFactory, IAsyncDisposable
{
    internal static readonly TimeSpan IdleRetention = TimeSpan.FromSeconds(60);
    private readonly IUiDispatcher _ui;
    private readonly Func<string, IAppWebView> _create;
    private readonly Func<Task> _closeEngine;
    private readonly TimeSpan _idleRetention;
    private readonly HashSet<Lease> _leases = [];
    private readonly HashSet<string> _retiring = new(StringComparer.Ordinal);
    private readonly Dictionary<Task, string> _cleanups = [];
    private readonly List<Exception> _cleanupFailures = [];
    private readonly Lazy<Task> _disposal;
    private CachedView? _idle;
    private bool _disposed;
    internal int NativeSessionCount => _leases.Count + (_idle is null ? 0 : 1);

    internal AppBrowserManager(IUiDispatcher ui, Func<string, IAppWebView>? create = null,
        Func<Task>? closeEngine = null, TimeSpan? idleRetention = null)
    {
        _ui = ui;
        _disposal = new(() => _ui.InvokeAsync(DisposeCoreAsync));
        _idleRetention = idleRetention ?? IdleRetention;
        if (create is not null) { _create = create; _closeEngine = closeEngine ?? (() => Task.CompletedTask); }
        else if (OperatingSystem.IsWindows())
        {
            var engine = new WindowsBrowserEngine(ui);
            _create = _ => OperatingSystem.IsWindows() ? new WindowsBrowserView(engine, ui) : throw new PlatformNotSupportedException();
            _closeEngine = () => OperatingSystem.IsWindows() ? engine.DisposeAsync().AsTask() : throw new PlatformNotSupportedException();
        }
        else
        {
            // WKWebView/WebKitGTK own native process sharing. Each new session
            // gets a separate ephemeral data context; unqualified reset is never
            // advertised as reuse. Do not substitute Windows profile semantics.
            _create = path => new AppNativeWebView(path, ui);
            _closeEngine = () => Task.CompletedTask;
        }
    }

    public IAppWebView Create(string userDataDirectory)
    {
        RequireUi();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = Path.GetFullPath(userDataDirectory);
        if (_retiring.Any(root => IsWithin(key, root))) throw new ObjectDisposedException("Package browser activation");
        IAppWebView browser;
        if (_idle is { } cached && cached.Key == key)
        {
            _idle = null;
            cached.Expiry.Cancel();
            browser = cached.Browser;
        }
        else
        {
            var predecessors = _leases.Where(lease => lease.Key == key && lease.RetirementStarted)
                .Select(lease => lease.WhenRetired)
                .Concat(_cleanups.Where(item => item.Value == key).Select(item => item.Key)).ToArray();
            var path = Path.Combine(key, Guid.NewGuid().ToString("N"));
            browser = predecessors.Length == 0 ? _create(path)
                : new AppDeferredWebView(Task.WhenAll(predecessors), () =>
                {
                    if (_idle is not { } ready || ready.Key != key) return _create(path);
                    _idle = null;
                    ready.Expiry.Cancel();
                    return ready.Browser is AppDeferredWebView deferred ? deferred.DetachForReuse() : ready.Browser;
                });
        }
        var lease = new Lease(this, key, browser);
        _leases.Add(lease);
        return lease;
    }

    public Task RetireActivationAsync(string userDataDirectory) => _ui.InvokeAsync(async () =>
    {
        var root = Path.GetFullPath(userDataDirectory);
        _retiring.Add(root);
        var leases = _leases.Where(item => IsWithin(item.Key, root)).ToArray();
        foreach (var lease in leases) lease.Reusable = false;
        var retirements = leases.Select(lease => lease.RetireAsync()).ToList();
        if (_idle is { } cached && IsWithin(cached.Key, root)) retirements.Add(EvictAsync(cached));
        retirements.AddRange(_cleanups.Where(item => IsWithin(item.Value, root)).Select(item => item.Key).ToArray());
        await Task.WhenAll(retirements);
    });

    public ValueTask DisposeAsync() => new(_disposal.Value);

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;
        AppSessionLog.WriteInfo($"Browser manager retirement started; sessions={_leases.Count}; idle={_idle is not null}; cleanups={_cleanups.Count}.", visibleInDeveloperLog: false);
        // Actual drains survive any outer bounded wait. Do not end the native
        // owner/STA or discard callbacks on caller cancellation.
        var failures = new List<Exception>();
        try
        {
            foreach (var lease in _leases.ToArray()) lease.Reusable = false;
            var retirements = _leases.ToArray().Select(lease => lease.RetireAsync()).ToList();
            if (_idle is { } cached) retirements.Add(EvictAsync(cached));
            await Task.WhenAll(retirements.Concat(_cleanups.Keys.ToArray()));
        }
        catch (Exception error) { failures.Add(error); }
        AppSessionLog.WriteInfo($"Browser manager sessions settled; sessions={_leases.Count}; cleanups={_cleanups.Count}; failures={failures.Count}.", visibleInDeveloperLog: false);
        try { await _closeEngine(); }
        catch (Exception error) { failures.Add(error); }
        AppSessionLog.WriteInfo("Browser manager engine retirement settled.", visibleInDeveloperLog: false);
        failures.AddRange(_cleanupFailures);
        if (failures.Count != 0) throw new AggregateException("Browser manager retirement failed.", failures);
    }

    private async Task ReleaseAsync(Lease lease)
    {
        RequireUi();
        try
        {
            if (lease.Reusable && !_disposed && !_retiring.Any(root => IsWithin(lease.Key, root)) && lease.Browser.CanReset)
            {
                await lease.RetireDocumentAsync();
                // An eviction can yield while another view finishes its reset.
                // Recheck both the slot and the activation fence after every
                // await; never overwrite that other cleaned controller.
                while (_idle is { } previous) await EvictAsync(previous);
                if (lease.Reusable && !_disposed && !_retiring.Any(root => IsWithin(lease.Key, root)))
                {
                    var cached = _idle = new(lease.Key, lease.Browser, new());
                    Observe(ExpireAsync(cached), cached.Key);
                    return;
                }
            }
            await CloseAsync(lease.Browser);
        }
        catch (Exception failure)
        {
            // A failed reset never becomes a reusable slot. Still own final
            // destruction; preserve the original failure rather than hiding it.
            try { await CloseAsync(lease.Browser); }
            catch (Exception error) { AppSessionLog.WriteError("Failed to close a retired browser session.", error); }
            lease.FailWaitingReopen(failure);
            throw;
        }
        finally { _leases.Remove(lease); lease.CompleteWaitingReopen(); }
    }

    private async Task ExpireAsync(CachedView cached)
    {
        try { await Task.Delay(_idleRetention, cached.Expiry.Token); await EvictAsync(cached); }
        catch (OperationCanceledException) when (cached.Expiry.IsCancellationRequested) { }
        finally { cached.Expiry.Dispose(); }
    }

    private Task EvictAsync(CachedView cached)
    {
        if (!ReferenceEquals(_idle, cached)) return Task.CompletedTask;
        _idle = null;
        cached.Expiry.Cancel();
        var cleanup = CloseAsync(cached.Browser);
        Observe(cleanup, cached.Key);
        return cleanup;
    }

    private static async Task CloseAsync(IAppWebView browser)
    {
        browser.Dispose();
        await browser.DrainNativeRetirementAsync();
    }

    private void Observe(Task task, string key)
    {
        _cleanups.Add(task, key);
        _ = CompleteAsync();
        async Task CompleteAsync()
        {
            try { await task; }
            catch (Exception error) { _cleanupFailures.Add(error); AppSessionLog.WriteError("Browser cache cleanup failed.", error); }
            finally { _cleanups.Remove(task); }
        }
    }

    private void RequireUi()
    {
        if (!_ui.CheckAccess()) throw new InvalidOperationException("Browser sessions belong to the original UI thread.");
    }
    private static bool IsWithin(string key, string root) => key == root || key.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private sealed record CachedView(string Key, IAppWebView Browser, CancellationTokenSource Expiry);

    private sealed class Lease(AppBrowserManager owner, string key, IAppWebView browser) : IAppWebView
    {
        private Task? _retirement;
        private Task? _documentRetirement;
        private TaskCompletionSource? _waitingReopen;
        internal bool RetirementStarted => _retirement is not null || _documentRetirement is not null;
        internal Task WhenRetired => _retirement ?? (_waitingReopen ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        internal void FailWaitingReopen(Exception error) => _waitingReopen?.TrySetException(error);
        internal void CompleteWaitingReopen() => _waitingReopen?.TrySetResult();
        internal string Key => key;
        internal IAppWebView Browser => browser;
        internal bool Reusable { get; set; } = true;
        public Control Control => browser.Control;
        public Rect CaptureViewport => browser.CaptureViewport;
        public bool CanCapture => _retirement is null && _documentRetirement is null && browser.CanCapture;
        public int? NativeBrowserProcessId => browser.NativeBrowserProcessId;
        public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting { add => browser.NavigationStarting += value; remove => browser.NavigationStarting -= value; }
        public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted { add => browser.NavigationCompleted += value; remove => browser.NavigationCompleted -= value; }
        public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested { add => browser.NewWindowRequested += value; remove => browser.NewWindowRequested -= value; }
        public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested { add => browser.ResourceRequested += value; remove => browser.ResourceRequested -= value; }
        public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived { add => browser.MessageReceived += value; remove => browser.MessageReceived -= value; }
        public event EventHandler<AppWebViewFailedEventArgs>? Failed { add => browser.Failed += value; remove => browser.Failed -= value; }
        public void Navigate(Uri uri) { RequireActive(); browser.Navigate(uri); }
        public Task<string?> InvokeScriptAsync(string script) { RequireActive(); return browser.InvokeScriptAsync(script); }
        public Task<byte[]> CaptureAsync(Rect bounds, int edge, CancellationToken token) { RequireActive(); return browser.CaptureAsync(bounds, edge, token); }
        public void Stop() { if (_retirement is null) browser.Stop(); }
        public Task RetireAsync() => _retirement ??= owner.ReleaseAsync(this);
        public void PreventReuse() => Reusable = false;
        public Task RetireDocumentAsync() => _documentRetirement ??= !Reusable ? RetireAsync()
            : browser.CanReset ? browser.ResetAsync() : browser.RetireDocumentAsync();
        public void Dispose() { var task = RetireAsync(); AppCleanupQuarantine.Retain(task, "retiring a browser session"); }
        public Task DrainNativeRetirementAsync() => RetireAsync();
        private void RequireActive() => ObjectDisposedException.ThrowIf(_retirement is not null || _documentRetirement is not null, this);
    }
}
