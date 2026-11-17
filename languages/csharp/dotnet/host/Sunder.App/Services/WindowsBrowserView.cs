using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Microsoft.Web.WebView2.Core;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace Sunder.App.Services;

// A stable child HWND belongs to this session, never to a removable Avalonia
// parent. NativeControlHost only attaches/parks it. Creation and final disposal
// remain on the original STA, including when demand disappears before readiness.
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserView : IAppWebView
{
    private readonly WindowsBrowserEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly BrowserHost _host;
    private readonly AppNativeWebViewViewport _viewport;
    private readonly string _profile = "view-" + Guid.NewGuid().ToString("N");
    private readonly HashSet<Task> _operations = [];
    private Task? _creation, _retirement;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private nint _child;
    private Uri? _requestedUri;
    private bool _disposed, _idle = true, _internalNavigation, _failed;
    private long _presentationEpoch;

    internal WindowsBrowserView(WindowsBrowserEngine engine, IUiDispatcher ui)
    {
        _engine = engine; _ui = ui;
        _host = new(this) { Focusable = true };
        _viewport = new(_host);
        _host.LayoutUpdated += OnLayout;
        _host.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.BoundsProperty || args.Property == Visual.IsVisibleProperty)
                _presentationEpoch++;
        };
    }

    public Control Control => _viewport;
    public Rect CaptureViewport => _host.Bounds;
    public bool CanCapture => !_disposed && !_idle && !_failed && _controller is not null && _host.IsEffectivelyVisible && TopLevel.GetTopLevel(_host) is not null;
    public bool CanReset => !_disposed && !_failed;
    public int? NativeBrowserProcessId => _controller is null ? null : checked((int)_controller.CoreWebView2.BrowserProcessId);
    internal CoreWebView2? Native => _controller?.CoreWebView2;
    private bool _capturing;
    private ulong? _neutralNavigationId;
    internal Task? Initialization => _creation;
    public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;
    public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;
    public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<AppWebViewFailedEventArgs>? Failed;

    public void Navigate(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed || _failed, this);
        _requestedUri = uri;
        _idle = false;
        if (_controller is { } controller)
        {
            controller.CoreWebView2.Resume();
            controller.CoreWebView2.Settings.IsScriptEnabled = true;
            controller.CoreWebView2.Settings.IsWebMessageEnabled = true;
            controller.CoreWebView2.IsMuted = false;
            controller.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        // Native acquisition is demand + attachment, not package registration.
    }

    private async Task CreateAsync()
    {
        try
        {
            _child = _engine.CreateChild();
            if (_child == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var acquired = await _engine.CreateAsync(_child, _profile);
            _environment = acquired.Environment;
            _controller = acquired.Controller;
            var web = _controller.CoreWebView2;
            _controller.IsVisible = false;
            _controller.ShouldDetectMonitorScaleChanges = false;
            _controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.IsBuiltInErrorPageEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.SavesInProfile = false; args.Handled = true; };
            web.DownloadStarting += (_, args) => args.Cancel = true;
            web.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!_disposed && !_idle) NewWindowRequested?.Invoke(this, new(Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri : null));
            };
            web.ProcessFailed += (_, args) =>
            {
                if (_disposed) return;
                _failed = true;
                AppSessionLog.WriteError($"Package browser process failed: {args.ProcessFailedKind}.");
                Failed?.Invoke(this, new(new InvalidOperationException($"The package browser process failed ({args.ProcessFailedKind}). Reload the view, or restart Sunder if recovery fails.")));
                NavigationCompleted?.Invoke(this, new(_requestedUri, false));
            };
            web.NavigationStarting += OnStarting;
            web.NavigationCompleted += OnCompleted;
            web.WebMessageReceived += (_, args) =>
            {
                if (_disposed || _idle) return;
                try { MessageReceived?.Invoke(this, new(args.TryGetWebMessageAsString())); }
                catch (ArgumentException) { /* Non-string payloads carry no bridge authority. */ }
            };
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            web.WebResourceRequested += OnResource;
            _controller.MoveFocusRequested += (_, args) =>
            {
                args.Handled = true;
                _host.MoveFocus(args.Reason == CoreWebView2MoveFocusReason.Previous ? NavigationDirection.Previous : NavigationDirection.Next);
            };
            if (!_disposed)
            {
                if (_host.NativeParent is { } parent) WindowsBrowserEngine.Reparent(_child, parent.Handle);
                _viewport.SetPlatformHandle(new PlatformHandle(_child, "HWND"));
                OnLayout(null, EventArgs.Empty);
                if (_requestedUri is { } requested) Navigate(requested);
            }
        }
        catch (Exception error)
        {
            _failed = true;
            AppSessionLog.WriteError("Native package browser initialization failed.", error);
            Failed?.Invoke(this, new(error));
            NavigationCompleted?.Invoke(this, new(_requestedUri, false));
            throw;
        }
    }

    private void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_internalNavigation && args.Uri == "about:blank") { _neutralNavigationId = args.NavigationId; return; }
        if (_disposed || _idle || _failed) { args.Cancel = true; return; }
        var translated = new AppWebNavigationStartingEventArgs(Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri : null);
        NavigationStarting?.Invoke(this, translated);
        args.Cancel = translated.Cancel || NavigationStarting is null;
    }

    private void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_disposed || _idle || _internalNavigation) return;
        var uri = Uri.TryCreate(_controller?.CoreWebView2.Source, UriKind.Absolute, out var current) ? current : null;
        NavigationCompleted?.Invoke(this, new(uri, args.IsSuccess));
    }

    private void OnResource(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var uri = new Uri(args.Request.Uri, UriKind.Absolute);
        var translated = new AppWebResourceRequestedEventArgs(uri, args.Request.Method) { Cancel = _disposed || _idle || _failed || ResourceRequested is null };
        if (!translated.Cancel) ResourceRequested?.Invoke(this, translated);
        if (translated.Cancel)
            args.Response = _environment!.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store\r\n");
    }

    public Task<string?> InvokeScriptAsync(string script) => _ui.InvokeAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed || _idle || _failed, this);
        if (_creation is { } creation) await creation;
        ObjectDisposedException.ThrowIf(_disposed || _idle || _failed, this);
        if (_controller is null) throw new InvalidOperationException("The browser document is not ready.");
        // The host supplies the transport binding, never package lifecycle code.
        return await TrackAsync(WindowsBrowserScript.ExecuteAsync(_controller.CoreWebView2,
            "window.invokeCSharpAction ??= body => chrome.webview.postMessage(body);\n" + script));
    });

    public Task<byte[]> CaptureAsync(Rect bounds, int edge, CancellationToken token) => _ui.InvokeAsync(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (!CanCapture) throw new InvalidOperationException("The package browser is not attached and visible.");
        if (_capturing) throw new InvalidOperationException("A native browser capture is already in progress.");
        var viewport = CaptureViewport;
        var local = bounds.Translate(new Vector(-viewport.X, -viewport.Y));
        AppNativeWebViewCapture.ValidateRegion(local, viewport.Size, edge);
        var epoch = _presentationEpoch;
        var rectangle = _controller!.Bounds;
        AppNativeWebViewCapture.RequirePixels(rectangle.Width, rectangle.Height);
        var view = _controller.CoreWebView2.GetComICoreWebView2();
        _capturing = true;
        try
        {
            var png = await TrackAsync(WindowsWebViewCapture.CaptureCoreAsync(view, 0, local, viewport.Size, edge, new PixelSize(rectangle.Width, rectangle.Height)));
            token.ThrowIfCancellationRequested();
            if (!CanCapture || epoch != _presentationEpoch || viewport != CaptureViewport || rectangle != _controller.Bounds)
                throw new InvalidOperationException("The native viewport changed during capture.");
            return png;
        }
        finally { _capturing = false; Marshal.Release(view); }
    });

    public void Stop() { if (_controller is not null && !_disposed) _controller.CoreWebView2.Stop(); }

    public Task RetireDocumentAsync()
    {
        if (CanReset) return ResetAsync();
        Dispose();
        return DrainNativeRetirementAsync();
    }

    public Task ResetAsync() => _ui.InvokeAsync(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed || _failed, this);
        _idle = true; _requestedUri = null;
        if (_creation is { } creation) await creation;
        if (_controller is null) return;
        try { await ResetCoreAsync(); }
        catch
        {
            _failed = true;
            Dispose();
            await DrainNativeRetirementAsync();
            throw;
        }
        _controller.IsVisible = false;
        await _controller.CoreWebView2.TrySuspendAsync(); // Best effort after real cleanup, not cleanup proof.
    });

    private async Task ResetCoreAsync()
    {
        var web = _controller!.CoreWebView2;
        web.Resume();
        web.Settings.IsWebMessageEnabled = false;
        web.Settings.IsScriptEnabled = false;
        web.IsMuted = true;
        web.Stop();
        _internalNavigation = true;
        _neutralNavigationId = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != _neutralNavigationId) return;
            if (args.IsSuccess) completed.TrySetResult();
            else completed.TrySetException(new InvalidOperationException("Neutral browser navigation failed."));
        }
        web.NavigationCompleted += Navigated;
        try { web.Navigate("about:blank"); await completed.Task; }
        finally { web.NavigationCompleted -= Navigated; _internalNavigation = false; }
        await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        await web.CallDevToolsProtocolMethodAsync("Page.resetNavigationHistory", "{}");
        await DrainOperationsAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _idle = true; _requestedUri = null;
        _retirement = _ui.InvokeAsync(CloseAsync);
        AppCleanupQuarantine.Retain(_retirement, "closing an isolated browser profile");
    }

    public Task DrainNativeRetirementAsync() => _retirement ?? Task.CompletedTask;

    private async Task CloseAsync()
    {
        try
        {
            if (_creation is { } creation)
            {
                try { await creation; }
                catch when (_controller is not null) { /* An acquired controller still needs real cleanup after bootstrap failure. */ }
            }
            if (_controller is not null)
            {
                try { if (!_failed) await ResetCoreAsync(); }
                finally
                {
                    try
                    {
                        var profile = _controller.CoreWebView2.Profile;
                        // Delete revokes the isolated profile and closes its views.
                        // Physical shared-UDF reclamation belongs to the engine owner.
                        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        void Deleted(object? sender, object args) => deleted.TrySetResult();
                        profile.Deleted += Deleted;
                        try { profile.Delete(); await deleted.Task; }
                        finally { profile.Deleted -= Deleted; }
                    }
                    finally
                    {
                        _controller.Close();
                        _controller = null;
                        // A failed process can release callbacks only on close.
                        // Join native ownership even when profile deletion failed.
                        await DrainOperationsAsync();
                    }
                }
            }
        }
        finally
        {
            _host.LayoutUpdated -= OnLayout;
            _viewport.Dispose(); _viewport.Child = null;
            WindowsBrowserEngine.DestroyChild(_child); _child = 0;
        }
    }

    private async Task DrainOperationsAsync()
    {
        foreach (var operation in _operations.ToArray())
        {
            try { await operation; }
            catch (Exception error)
            {
                // The operation's caller retains its failed/canceled verdict.
                // Cleanup requires real settlement, not a successful old result.
                AppSessionLog.WriteInfo($"Retired browser operation settled without a result: {error.GetType().Name}.", visibleInDeveloperLog: false);
            }
        }
    }

    private async Task<T> TrackAsync<T>(Task<T> task)
    {
        _operations.Add(task);
        try { return await task; }
        finally { _operations.Remove(task); }
    }
    private async Task TrackAsync(Task task)
    {
        _operations.Add(task);
        try { await task; }
        finally { _operations.Remove(task); }
    }

    private void OnLayout(object? sender, EventArgs args)
    {
        if (_controller is null || _disposed) return;
        if (_host.NativeParent is { } parent && _host.IsEffectivelyVisible
            && WindowsBrowserEngine.ParentOf(_child) != parent.Handle)
            WindowsBrowserEngine.Reparent(_child, parent.Handle);
        var scaling = TopLevel.GetTopLevel(_host)?.RenderScaling ?? 1;
        _controller.RasterizationScale = scaling;
        _controller.Bounds = new Rectangle(0, 0, Math.Max(1, (int)Math.Ceiling(_host.Bounds.Width * scaling)), Math.Max(1, (int)Math.Ceiling(_host.Bounds.Height * scaling)));
        _controller.IsVisible = !_idle && _host.IsEffectivelyVisible && _host.NativeParent is not null;
        _controller.NotifyParentWindowPositionChanged();
    }

    private sealed class BrowserHost(WindowsBrowserView owner) : NativeControlHost
    {
        private Window? _window;
        internal IPlatformHandle? NativeParent { get; private set; }
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.Closing += OnWindowClosing;
        }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_window is not null) _window.Closing -= OnWindowClosing;
            _window = null;
            base.OnDetachedFromVisualTree(e);
        }
        private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            // Win32 destroys child HWNDs before Avalonia's later detach callback.
            // Move the session-owned child out while the old parent still exists,
            // even when native controller creation has not finished yet.
            if (owner._child == 0) return;
            if (owner._controller is { } controller) controller.IsVisible = false;
            owner._engine.Park(owner._child);
            // If another handler cancels the close, normal layout reattaches the
            // same child/document. Do not create a replacement controller.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_window is { IsVisible: true }) owner.OnLayout(null, EventArgs.Empty);
            });
        }
        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            NativeParent = parent;
            owner._creation ??= owner.CreateAsync();
            WindowsBrowserEngine.Reparent(owner._child, parent.Handle);
            return new PlatformHandle(owner._child, "HWND");
        }
        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            NativeParent = null; owner._presentationEpoch++;
            if (owner._child == 0) return;
            if (owner._controller is { } controller) controller.IsVisible = false;
            owner._engine.Park(owner._child);
        }
        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            owner._controller?.MoveFocus(e.NavigationMethod == NavigationMethod.Tab
                ? e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? CoreWebView2MoveFocusReason.Previous : CoreWebView2MoveFocusReason.Next
                : CoreWebView2MoveFocusReason.Programmatic);
        }
        internal void MoveFocus(NavigationDirection direction)
            => TopLevel.GetTopLevel(this)?.FocusManager?.TryMoveFocus(direction, new());
    }
}
