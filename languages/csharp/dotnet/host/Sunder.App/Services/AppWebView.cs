using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace Sunder.App.Services;

internal interface IAppWebView : IDisposable
{
    Control Control { get; }
    Rect CaptureViewport => new(Control.Bounds.Size);
    bool CanCapture => false;
    int? NativeBrowserProcessId => null;

    event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;

    event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;

    event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;

    event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;

    event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;
    event EventHandler<AppWebViewFailedEventArgs>? Failed { add { } remove { } }

    void Navigate(Uri uri);

    Task<string?> InvokeScriptAsync(string script);
    Task<byte[]> CaptureAsync(Rect bounds, int maximumEdge, CancellationToken token)
        => throw new NotSupportedException("This native browser backend does not support package capture.");

    void Stop();

    Task DrainNativeRetirementAsync() => Task.CompletedTask;

    bool CanReset => false;
    // An activation being removed has no future consumer for a clean spare.
    void PreventReuse() { }
    Task ResetAsync() => throw new NotSupportedException("This browser backend requires a fresh native view.");
    Task RetireAsync() { Dispose(); return Task.CompletedTask; }
    Task RetireDocumentAsync() => Task.CompletedTask;
}

internal interface IAppWebViewFactory
{
    IAppWebView Create(string userDataDirectory);

    Task RetireActivationAsync(string userDataDirectory) => Task.CompletedTask;
}

internal sealed class AppWebNavigationStartingEventArgs(Uri? uri) : EventArgs
{
    public Uri? Uri { get; } = uri;

    public bool Cancel { get; set; }
}

internal sealed class AppWebNavigationCompletedEventArgs(Uri? uri, bool succeeded) : EventArgs
{
    public Uri? Uri { get; } = uri;

    public bool Succeeded { get; } = succeeded;
}

internal sealed class AppWebNewWindowRequestedEventArgs(Uri? uri) : EventArgs
{
    public Uri? Uri { get; } = uri;

    public bool Handled { get; set; }
}

internal sealed class AppWebResourceRequestedEventArgs(Uri uri, string method) : EventArgs
{
    public Uri Uri { get; } = uri;

    public string Method { get; } = method;

    public bool Cancel { get; set; }
}

internal sealed class AppWebMessageReceivedEventArgs(string? body) : EventArgs
{
    public string? Body { get; } = body;
}

internal sealed class AppNativeWebViewFactory(IUiDispatcher? uiDispatcher = null) : IAppWebViewFactory
{
    private readonly AppBrowserManager _manager = new(uiDispatcher ?? AvaloniaUiDispatcher.Instance);
    public IAppWebView Create(string userDataDirectory)
        => _manager.Create(userDataDirectory);

    public async Task RetireActivationAsync(string userDataDirectory)
    {
        await _manager.RetireActivationAsync(userDataDirectory);
        await _manager.DisposeAsync();
    }
}

internal sealed class AppWebViewFailedEventArgs(Exception error) : EventArgs
{
    internal Exception Error { get; } = error;
}

internal sealed class AppNativeWebView : IAppWebView
{
    private readonly NativeWebView _webView = new();
    private readonly AppNativeWebViewViewport _viewport;
    private readonly string _userDataDirectory;
    private readonly IUiDispatcher _uiDispatcher;
    private int _disposed;
    private long _adapterGeneration;
    private bool _captureInProgress;
    private TaskCompletionSource? _captureSettled;
    private IDisposable? _nativeOwnership;
    private Task? _retirement;
    private readonly HashSet<Task> _scripts = [];
    private bool _retiringDocument;
    private bool _failed;

    public AppNativeWebView(string userDataDirectory, IUiDispatcher? uiDispatcher = null)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows browsers are owned by AppBrowserManager's WebView2 backend.");
        // GTK is the supported Linux backend. WPE's wrapper cannot keep its
        // compositor adapter alive through asynchronous capture/retirement.
        if (OperatingSystem.IsLinux()) _webView.AdapterPreference = [WebViewAdapterType.WebKitGtk];
        _viewport = new(_webView);
        _userDataDirectory = Path.GetFullPath(userDataDirectory);
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        Directory.CreateDirectory(_userDataDirectory);
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += OnAdapterCreated;
        _webView.AdapterDestroyed += OnAdapterDestroyed;
        _webView.DetachedFromVisualTree += OnNativeDetached;
        _webView.PropertyChanged += OnNativeGeometryChanged;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebResourceRequested += OnWebResourceRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.ProcessTerminated += OnProcessTerminated;
    }

    public Control Control => _viewport;

    public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;

    public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;

    public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;

    public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler<AppWebViewFailedEventArgs>? Failed;

    public void Navigate(Uri uri)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || _failed, this);
        _webView.Navigate(uri);
    }

    public Task<string?> InvokeScriptAsync(string script)
        => _uiDispatcher.InvokeAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var operation = _webView.InvokeScript(script);
            _scripts.Add(operation);
            try { return await operation; }
            finally { _scripts.Remove(operation); }
        });

    public bool CanCapture => Volatile.Read(ref _disposed) == 0 && !_failed && ReferenceEquals(_viewport.Child, _webView)
        && _webView.IsEffectivelyVisible && TopLevel.GetTopLevel(_webView) is not null
        && _webView.TryGetPlatformHandle() switch
    {
        IAppleWKWebViewPlatformHandle => OperatingSystem.IsMacOS(),
        IGtkWebViewPlatformHandle => OperatingSystem.IsLinux(),
        _ => false,
    };

    public void Stop() => _webView.Stop();

    public Task<byte[]> CaptureAsync(Rect bounds, int maximumEdge, CancellationToken token)
        => _uiDispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_captureInProgress) throw new InvalidOperationException("A native browser capture is already in progress.");
            var handle = _webView.TryGetPlatformHandle();
            var viewport = CaptureViewport;
            var localBounds = bounds.Translate(new Vector(-viewport.X, -viewport.Y));
            AppNativeWebViewCapture.ValidateRegion(localBounds, viewport.Size, maximumEdge);
            if (TopLevel.GetTopLevel(_webView) is not { } owner || !_webView.IsEffectivelyVisible
                || !ReferenceEquals(_viewport.Child, _webView) || !ReferenceEquals(owner, TopLevel.GetTopLevel(_viewport)))
                throw new InvalidOperationException("The native capture viewport is not attached and visible.");
            var generation = Volatile.Read(ref _adapterGeneration);
            _captureInProgress = true;
            _captureSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Keep the native wrapper alive even if its visual detaches.
                // A native object reference alone does not defer adapter teardown.
                // The session's ownership lease also spans visual detachment.
                // BeginReparenting scopes cannot be nested by individual captures.
                using var nativeLifetime = _nativeOwnership is null ? _webView.BeginReparenting(yieldOnLayoutBeforeExiting: false) : null;
                // Once submitted, await the real native callback even on cancellation.
                // Owner retirement drains this task before releasing the browser.
                byte[] png;
                if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
                {
                    var retained = apple.GetWKWebViewRetained();
                    try { png = await MacOsWebViewCapture.CaptureAsync(retained, localBounds, maximumEdge); }
                    finally { MacOsWebViewCapture.Release(retained); }
                }
                else if (OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle gtk)
                    png = await GtkWebViewCapture.CaptureAsync(gtk.WebKitWebView, localBounds, viewport.Size, maximumEdge);
                else throw new NotSupportedException("Native capture is not supported by this browser backend.");
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _adapterGeneration), this);
                if (viewport != CaptureViewport || !ReferenceEquals(owner, TopLevel.GetTopLevel(_webView)) || !_webView.IsEffectivelyVisible)
                    throw new InvalidOperationException("The native viewport changed during capture. Capture its current geometry again.");
                return png;
            }
            finally { _captureInProgress = false; _captureSettled.TrySetResult(); }
        });

    public Rect CaptureViewport => _webView.Bounds;

    public Task DrainNativeRetirementAsync() => _uiDispatcher.InvokeAsync(async () =>
    {
        if (_retirement is { } retirement) await retirement;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _retirement = _uiDispatcher.InvokeAsync(DisposeCoreAsync);
        AppCleanupQuarantine.Retain(_retirement, "retiring a native WebKit session");
    }

    private async Task DisposeCoreAsync()
    {
        void Stage(string stage) => AppSessionLog.WriteInfo($"Native WebKit retirement: stage={stage}; capturing={_captureInProgress}; scripts={_scripts.Count}.", visibleInDeveloperLog: false);
        Stage("capture-drain-started");
        if (_captureSettled is { } capture) await capture.Task;
        Stage("capture-drained");
        Task nativeRetirement = Task.CompletedTask;
        var failures = new List<Exception>();
        try
        {
            var handle = _webView.TryGetPlatformHandle();
            _webView.Stop();
            try { if (!_failed && handle is not null) { Stage("neutral-navigation-started"); await NavigateNeutralAsync(); Stage("neutral-navigation-completed"); } }
            catch (Exception error) { failures.Add(error); }
            try
            {
                Stage("data-clear-started");
                if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
                    await MacOsWebViewDataRetirement.ClearAsync(apple.WKWebView);
                else if (OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle gtk)
                    await GtkWebViewDataRetirement.ClearAsync(gtk.WebKitWebView);
                Stage("data-cleared");
            }
            catch (Exception error) { failures.Add(error); }
            if (OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle retiringGtk)
            {
                // The wrapper queues GTK disposal. Observe actual native object
                // finalization rather than treating AdapterDestroyed as its end.
                nativeRetirement = await GtkWebViewDataRetirement.ObserveNativeRetirementAsync(retiringGtk.WebKitWebView);
            }
        }
        finally { Stage("native-detach-started"); DetachNative(); Stage("native-detached"); }
        await nativeRetirement;
        Stage("native-retired");
        foreach (var script in _scripts.ToArray())
        {
            try { await script; }
            catch (Exception error)
            {
                AppSessionLog.WriteInfo($"Retired WebKit operation settled without a result: {error.GetType().Name}.", visibleInDeveloperLog: false);
            }
        }
        Stage("scripts-drained");
        if (failures.Count != 0) throw new AggregateException("Native WebKit document cleanup failed.", failures);
    }

    public Task RetireDocumentAsync()
    {
        Dispose();
        return DrainNativeRetirementAsync();
    }

    private async Task NavigateNeutralAsync()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Navigated(object? sender, WebViewNavigationCompletedEventArgs args)
        {
            AppSessionLog.WriteInfo($"Retiring WebKit navigation completed: neutral={args.Request?.AbsoluteUri == "about:blank"}; missingRequest={args.Request is null}; succeeded={args.IsSuccess}.", visibleInDeveloperLog: false);
            if (args.Request?.AbsoluteUri != "about:blank") return;
            if (args.IsSuccess) completed.TrySetResult();
            else completed.TrySetException(new InvalidOperationException("The retiring WebKit document did not reach its neutral page."));
        }
        _retiringDocument = true;
        _webView.NavigationCompleted += Navigated;
        try { _webView.Navigate(new Uri("about:blank")); await completed.Task; }
        finally { _retiringDocument = false; _webView.NavigationCompleted -= Navigated; }
    }

    private void DetachNative()
    {
        _webView.Stop();
        _viewport.Dispose();
        // ContentControl clearing can defer its visual update until layout. Detach
        // the actual native host now; capture's reparenting lease still protects
        // any submitted callback until it settles.
        _viewport.Child = null;
        var ownership = _nativeOwnership;
        _nativeOwnership = null;
        ownership?.Dispose();
        UnsubscribeNativeEvents();
    }

    private void UnsubscribeNativeEvents()
    {
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.AdapterCreated -= OnAdapterCreated;
        _webView.AdapterDestroyed -= OnAdapterDestroyed;
        _webView.DetachedFromVisualTree -= OnNativeDetached;
        _webView.PropertyChanged -= OnNativeGeometryChanged;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.WebResourceRequested -= OnWebResourceRequested;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _webView.ProcessTerminated -= OnProcessTerminated;
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs args)
    {
        Interlocked.Increment(ref _adapterGeneration);
        var handle = args.TryGetPlatformHandle();
        _nativeOwnership ??= _webView.BeginReparenting(yieldOnLayoutBeforeExiting: false);
        if (Volatile.Read(ref _disposed) == 0) _viewport.SetPlatformHandle(handle);
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs args)
    { Interlocked.Increment(ref _adapterGeneration); _viewport.SetPlatformHandle(null); }

    private void OnNativeDetached(object? sender, VisualTreeAttachmentEventArgs args)
        => Interlocked.Increment(ref _adapterGeneration);

    private void OnNativeGeometryChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.BoundsProperty || args.Property == Visual.IsVisibleProperty)
            Interlocked.Increment(ref _adapterGeneration);
        if (args.Property == NativeWebView.AdapterInfoProperty
            && _webView.AdapterInfo is DetailedWebViewAdapterInfo { UnavailableReason: { } reason })
        {
            _failed = true;
            Failed?.Invoke(this, new(new InvalidOperationException("Native package browser initialization failed: " + reason)));
        }
    }

    private void OnProcessTerminated(object? sender, WebViewProcessTerminatedEventArgs args)
    {
        _failed = true;
        if (Volatile.Read(ref _disposed) == 0)
            Failed?.Invoke(this, new(new InvalidOperationException("The native package browser terminated: " + args.Reason)));
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;
        switch (args)
        {
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                apple.DataStoreIdentifier = Guid.NewGuid();
                apple.UpgradeKnownHostsToHTTPS = false;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                gtk.DisableCache = true;
                gtk.SharedProcessModel = false;
                gtk.BaseDataDirectory = Path.Combine(_userDataDirectory, "data");
                gtk.BaseCacheDirectory = Path.Combine(_userDataDirectory, "cache");
                break;
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        if (_retiringDocument && args.Request?.AbsoluteUri == "about:blank") return;
        if (Volatile.Read(ref _disposed) != 0) { args.Cancel = true; return; }
        var translated = new AppWebNavigationStartingEventArgs(args.Request);
        NavigationStarting?.Invoke(this, translated);
        args.Cancel = translated.Cancel;
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0)
            NavigationCompleted?.Invoke(this, new AppWebNavigationCompletedEventArgs(args.Request, args.IsSuccess));
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) { args.Handled = true; return; }
        var translated = new AppWebNewWindowRequestedEventArgs(args.Request);
        NewWindowRequested?.Invoke(this, translated);
        args.Handled = translated.Handled;
    }

    private void OnWebResourceRequested(object? sender, WebResourceRequestedEventArgs args)
        => DispatchResourceRequest(args.Request.Uri, args.Request.Method.Method,
            Volatile.Read(ref _disposed) != 0, _retiringDocument, () => _webView.Stop(),
            resource => ResourceRequested?.Invoke(this, resource));

    internal static void DispatchResourceRequest(Uri uri, string method, bool disposed, bool retiringDocument,
        Action stop, Action<AppWebResourceRequestedEventArgs> forward)
    {
        // WKWebView reports the host's neutral navigation as a resource request
        // before NavigationStarted. Stopping it after disposal deadlocks cleanup.
        // Only this exact, internally owned page bypasses the retired guard; it
        // never reaches package policy or restores any bridge authority.
        if (retiringDocument && uri.AbsoluteUri == "about:blank") return;
        if (disposed) { stop(); return; }
        var translated = new AppWebResourceRequestedEventArgs(uri, method);
        forward(translated);
        if (translated.Cancel)
        {
            stop();
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0)
            MessageReceived?.Invoke(this, new AppWebMessageReceivedEventArgs(args.Body));
    }
}

/// <summary>
/// Native browsers fill the content bounds; Sunder applies the shell's rounded
/// clips to their native surface. The old contained rectangle remains a fail-safe
/// only for an unavailable/unsupported native mask, never package-authored padding.
/// </summary>
internal sealed class AppNativeWebViewViewport : Decorator, IDisposable
{
    private const double DefaultShellRadius = 18;
    private bool _usesCompositedRendering;
    private IAppNativeWebViewClip? _nativeClip;
    private Rect _arrangedViewport;
    private AppNativeWebViewClipRegion[] _arrangedRegions = [];
    private double _arrangedScaling;
    private readonly List<Border> _clipBorders = [];

    internal AppNativeWebViewViewport(Control browser, IAppNativeWebViewClip? nativeClip = null)
    {
        Child = browser;
        ClipToBounds = true;
        _nativeClip = nativeClip;
    }

    internal bool UsesCompositedRendering
    {
        get => _usesCompositedRendering;
        set
        {
            if (_usesCompositedRendering == value) return;
            _usesCompositedRendering = value;
            InvalidateMeasure();
        }
    }
    internal IReadOnlyList<AppNativeWebViewClipRegion> CaptureRegions => _arrangedRegions;

    internal void SetPlatformHandle(IPlatformHandle? handle)
    {
        try
        {
            // This adapter is rendered into Avalonia's compositor, not an HWND.
            UsesCompositedRendering = handle?.HandleDescriptor == "Windows.UI.Composition.ContainerVisual"
                || handle is ILinuxWpePlatformHandle;
            SetNativeClip(handle is null ? null : AppNativeWebViewClip.Create(handle));
        }
        catch (Exception error) when (IsNativeClipFailure(error))
        {
            SetNativeClip(null);
            AppSessionLog.WriteError("Native browser clipping is unavailable; using the contained viewport.", error);
        }
    }

    internal void SetNativeClip(IAppNativeWebViewClip? clip)
    {
        _nativeClip?.Dispose();
        _nativeClip = clip;
        _arrangedRegions = [];
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = UsesCompositedRendering || _nativeClip is not null ? 0 : Inset(DefaultShellRadius, Scaling);
        Child?.Measure(new Size(Math.Max(0, availableSize.Width - 2 * inset), Math.Max(0, availableSize.Height - 2 * inset)));
        return Child is { } browser ? new Size(browser.DesiredSize.Width + 2 * inset, browser.DesiredSize.Height + 2 * inset) : default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var regions = ClipRegions(finalSize);
        var viewport = regions is null ? default : CalculateViewport(finalSize, regions);
        if (Child is { } browser)
        {
            // NativeControlHost clamps zero dimensions to one pixel. Visibility is
            // essential when a splitter leaves no contained rectangle to display.
            browser.IsVisible = viewport.Width >= 1 / Scaling && viewport.Height >= 1 / Scaling;
            browser.Arrange(viewport);
            if (regions is not null && _nativeClip is { } clip)
            {
                var applied = false;
                try { applied = clip.TryApply(finalSize, regions, Scaling); }
                catch (Exception error) when (IsNativeClipFailure(error))
                { AppSessionLog.WriteError("Native browser clip update failed; using the contained viewport.", error); }
                if (!applied)
                {
                    SetNativeClip(null);
                    viewport = CalculateViewport(finalSize, regions);
                    browser.IsVisible = viewport.Width >= 1 / Scaling && viewport.Height >= 1 / Scaling;
                    browser.Arrange(viewport);
                }
            }
        }
        _arrangedViewport = viewport;
        _arrangedRegions = regions ?? [];
        _arrangedScaling = Scaling;
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var border in ShellClipBorders())
        {
            _clipBorders.Add(border);
            border.PropertyChanged += OnBoundaryPropertyChanged;
        }
        InvalidateArrange();
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeBoundaries();
        if (Child is { } browser) browser.IsVisible = false;
        base.OnDetachedFromVisualTree(e);
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void OnBoundaryPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        // CornerRadius changes only invalidate Border rendering; waiting for a
        // future unrelated layout would leave a native child outside the new clip.
        if (args.Property == Border.CornerRadiusProperty || args.Property == ClipToBoundsProperty || args.Property == BoundsProperty)
            InvalidateArrange();
    }

    private IEnumerable<Border> ShellClipBorders() => this.GetVisualAncestors().OfType<Border>()
        .Where(border => border.Classes.Contains("shell-panel-content-clip") || border.Classes.Contains("secondary-panel-content-clip"));

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        // Includes docking, splitter movement, theme-radius and display-scale changes.
        var regions = ClipRegions(Bounds.Size);
        if (regions is null)
        {
            if (_arrangedRegions.Length != 0 || _arrangedViewport != default) InvalidateArrange();
            return;
        }
        if (!regions.SequenceEqual(_arrangedRegions) || Scaling != _arrangedScaling
            || CalculateViewport(Bounds.Size, regions) != _arrangedViewport) InvalidateArrange();
    }

    private AppNativeWebViewClipRegion[]? ClipRegions(Size size)
    {
        var regions = new List<AppNativeWebViewClipRegion> { AppNativeWebViewClipRegion.Create(new Rect(size), default) };
        foreach (var border in ShellClipBorders().Where(border => border.ClipToBounds))
        {
            if (border.TransformToVisual(this) is not { } transform
                || Math.Abs(transform.M11 - 1) > 0.000001 || Math.Abs(transform.M22 - 1) > 0.000001
                || Math.Abs(transform.M12) > 0.000001 || Math.Abs(transform.M21) > 0.000001) return null;
            var origin = transform.Transform(default(Point));
            regions.Add(AppNativeWebViewClipRegion.Create(new Rect(origin, border.Bounds.Size), border.CornerRadius));
        }
        return regions.ToArray();
    }

    private Rect CalculateViewport(Size size, IReadOnlyList<AppNativeWebViewClipRegion> regions)
    {
        if (UsesCompositedRendering || _nativeClip is not null) return new Rect(size);
        var viewport = ContainedRectangle(new Rect(size), DefaultShellRadius, Scaling);
        foreach (var region in regions.Skip(1))
        {
            var r = region.Radius;
            var radius = Math.Max(Math.Max(r.TopLeft, r.TopRight), Math.Max(r.BottomLeft, r.BottomRight));
            viewport = viewport.Intersect(ContainedRectangle(region.Bounds, radius, Scaling));
        }
        return viewport;
    }

    internal static Rect ContainedRectangle(Rect boundary, double radius, double scaling)
    {
        var inset = Inset(radius, scaling);
        return boundary.Width <= 2 * inset || boundary.Height <= 2 * inset
            ? default : boundary.Deflate(new Thickness(inset));
    }

    public void Dispose()
    {
        UnsubscribeBoundaries();
        SetNativeClip(null);
        if (Child is { } browser) browser.IsVisible = false;
    }

    private void UnsubscribeBoundaries()
    {
        LayoutUpdated -= OnLayoutUpdated;
        foreach (var border in _clipBorders) border.PropertyChanged -= OnBoundaryPropertyChanged;
        _clipBorders.Clear();
    }

    private static bool IsNativeClipFailure(Exception error) => error is DllNotFoundException or EntryPointNotFoundException
        or ExternalException or InvalidOperationException or ArgumentOutOfRangeException;

    private static double Inset(double radius, double scaling)
        // r * (1 - 1/sqrt(2)) contains all four rectangle corners. One physical
        // pixel also covers native-host rounding at fractional layout positions.
        => radius <= 0 ? 0 : Math.Ceiling((1 - 1 / Math.Sqrt(2)) * radius * scaling + 1) / scaling;
}

internal static class AppWebNavigationPolicy
{
    public static bool IsPackageUri(AppWebContentServer server, Uri? uri)
        => server.Owns(uri)
           && uri!.UserInfo.Length == 0
           && uri.Fragment.Length == 0;

    public static bool IsExternalUri(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && uri.UserInfo.Length == 0
           && uri.Fragment.Length <= 2048
           && uri.OriginalString.Length <= 2048
           && uri.Scheme is "https" or "http";
}
