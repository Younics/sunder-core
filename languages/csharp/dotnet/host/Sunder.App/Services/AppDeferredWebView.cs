using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Services;

// Only used when the same view is reopened while its old native owner drains.
// The placeholder can be closed/reopened repeatedly without creating controllers.
internal sealed class AppDeferredWebView : IAppWebView
{
    private readonly ContentControl _host = new() { Content = new TextBlock { Text = "Loading view…" } };
    private readonly Task _creation;
    private IAppWebView? _browser;
    private Uri? _requestedUri;
    private Task? _retirement;
    private bool _disposed;
    private Exception? _failure;

    internal AppDeferredWebView(Task predecessor, Func<IAppWebView> create)
        => _creation = CreateAsync(predecessor, create);

    public Control Control => _host;
    public Rect CaptureViewport => _browser?.CaptureViewport ?? default;
    public bool CanCapture => !_disposed && _browser?.CanCapture == true;
    public bool CanReset => !_disposed && _browser?.CanReset == true;
    public int? NativeBrowserProcessId => _browser?.NativeBrowserProcessId;
    public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;
    public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;
    public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<AppWebViewFailedEventArgs>? Failed;

    private async Task CreateAsync(Task predecessor, Func<IAppWebView> create)
    {
        try
        {
            // Publish our task and let the new control install its handlers first.
            await Task.Yield();
            await predecessor.WaitAsync(AppShutdownBudgets.BrowserCleanup);
            if (_disposed) return;
            var browser = _browser = create();
            browser.NavigationStarting += OnStarting;
            browser.NavigationCompleted += OnCompleted;
            browser.NewWindowRequested += OnNewWindow;
            browser.ResourceRequested += OnResource;
            browser.MessageReceived += OnMessage;
            browser.Failed += OnFailure;
            if (_requestedUri is { } uri) browser.Navigate(uri);
            _host.Content = browser.Control;
        }
        catch (Exception error)
        {
            _failure = error;
            AppSessionLog.WriteError("Deferred package browser initialization failed.", error);
            if (!_disposed) Failed?.Invoke(this, new(error));
        }
    }

    public void Navigate(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _requestedUri = uri;
        _browser?.Navigate(uri);
    }

    public async Task<string?> InvokeScriptAsync(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _creation;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw new InvalidOperationException("The package browser could not initialize.", _failure);
        return await _browser!.InvokeScriptAsync(script);
    }

    public Task<byte[]> CaptureAsync(Rect bounds, int maximumEdge, CancellationToken token)
        => CanCapture ? _browser!.CaptureAsync(bounds, maximumEdge, token)
            : Task.FromException<byte[]>(new InvalidOperationException("The package browser is not ready for capture."));
    public void Stop() { if (!_disposed) _browser?.Stop(); }
    public Task ResetAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _browser!.ResetAsync();
    }
    internal IAppWebView DetachForReuse()
    {
        // Transfer a cleaned native view, not a growing chain of placeholders.
        var browser = _browser ?? throw new InvalidOperationException("The deferred browser is not ready for reuse.");
        _host.Content = null;
        Unsubscribe(browser);
        _browser = null;
        _disposed = true;
        return browser;
    }
    public Task RetireDocumentAsync()
    {
        if (CanReset) return ResetAsync();
        Dispose();
        return DrainNativeRetirementAsync();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requestedUri = null;
        _host.Content = null;
        _retirement = CloseAsync();
    }
    public Task DrainNativeRetirementAsync() => _retirement ?? Task.CompletedTask;

    private async Task CloseAsync()
    {
        await _creation;
        if (_browser is { } browser)
        {
            try { browser.Dispose(); await browser.DrainNativeRetirementAsync(); }
            finally { Unsubscribe(browser); }
        }
        if (_failure is not null) throw new InvalidOperationException("The deferred browser owner did not settle cleanly.", _failure);
    }

    private void OnStarting(object? sender, AppWebNavigationStartingEventArgs args) => NavigationStarting?.Invoke(this, args);
    private void OnCompleted(object? sender, AppWebNavigationCompletedEventArgs args) => NavigationCompleted?.Invoke(this, args);
    private void OnNewWindow(object? sender, AppWebNewWindowRequestedEventArgs args) => NewWindowRequested?.Invoke(this, args);
    private void OnResource(object? sender, AppWebResourceRequestedEventArgs args) => ResourceRequested?.Invoke(this, args);
    private void OnMessage(object? sender, AppWebMessageReceivedEventArgs args) => MessageReceived?.Invoke(this, args);
    private void OnFailure(object? sender, AppWebViewFailedEventArgs args) => Failed?.Invoke(this, args);
    private void Unsubscribe(IAppWebView browser)
    {
        browser.NavigationStarting -= OnStarting;
        browser.NavigationCompleted -= OnCompleted;
        browser.NewWindowRequested -= OnNewWindow;
        browser.ResourceRequested -= OnResource;
        browser.MessageReceived -= OnMessage;
        browser.Failed -= OnFailure;
    }
}
