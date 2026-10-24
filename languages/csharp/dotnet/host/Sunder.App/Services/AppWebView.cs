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

    event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;

    event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;

    event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;

    event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;

    event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;

    void Navigate(Uri uri);

    Task<string?> InvokeScriptAsync(string script);
    Task<byte[]> CaptureAsync(Rect bounds, int maximumEdge, CancellationToken token)
        => throw new NotSupportedException("This native browser backend does not support package capture.");

    void Stop();
}

internal interface IAppWebViewFactory
{
    IAppWebView Create(string userDataDirectory);
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
    public IAppWebView Create(string userDataDirectory)
        => new AppNativeWebView(userDataDirectory, uiDispatcher);
}

internal sealed class AppNativeWebView : IAppWebView
{
    private readonly NativeWebView _webView = new();
    private readonly AppNativeWebViewViewport _viewport;
    private readonly string _userDataDirectory;
    private readonly IUiDispatcher _uiDispatcher;
    private int _disposed;
    private long _adapterGeneration;

    public AppNativeWebView(string userDataDirectory, IUiDispatcher? uiDispatcher = null)
    {
        _viewport = new(_webView);
        _userDataDirectory = Path.GetFullPath(userDataDirectory);
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        Directory.CreateDirectory(_userDataDirectory);
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += OnAdapterCreated;
        _webView.AdapterDestroyed += OnAdapterDestroyed;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebResourceRequested += OnWebResourceRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;
    }

    public Control Control => _viewport;

    public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;

    public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;

    public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;

    public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;

    public void Navigate(Uri uri) => _webView.Navigate(uri);

    public Task<string?> InvokeScriptAsync(string script)
        => _uiDispatcher.InvokeAsync(() => _webView.InvokeScript(script));

    public void Stop() => _webView.Stop();

    public Task<byte[]> CaptureAsync(Rect bounds, int maximumEdge, CancellationToken token)
        => _uiDispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var handle = _webView.TryGetPlatformHandle();
            if (!OperatingSystem.IsMacOS() || handle is not IAppleWKWebViewPlatformHandle apple)
                throw new NotSupportedException("Native capture is not supported by this browser backend.");
            var viewport = CaptureViewport;
            if (!viewport.Contains(bounds) || bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("The native capture region left its viewport.");
            var retained = apple.GetWKWebViewRetained();
            var generation = Volatile.Read(ref _adapterGeneration);
            try
            {
                // Once submitted, await the real native callback even on cancellation.
                // Owner retirement drains this task before releasing the browser.
                var png = await MacOsWebViewCapture.CaptureAsync(retained, bounds.Translate(new Vector(-viewport.X, -viewport.Y)), maximumEdge);
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _adapterGeneration), this);
                if (viewport != CaptureViewport) throw new InvalidOperationException("The native viewport changed during capture. Capture its current geometry again.");
                return png;
            }
            finally { MacOsWebViewCapture.Release(retained); }
        });

    public Rect CaptureViewport => _webView.Bounds;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.AdapterCreated -= OnAdapterCreated;
        _webView.AdapterDestroyed -= OnAdapterDestroyed;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.WebResourceRequested -= OnWebResourceRequested;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _viewport.Dispose();
        _webView.Stop();
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs args)
    { Interlocked.Increment(ref _adapterGeneration); _viewport.SetPlatformHandle(args.TryGetPlatformHandle()); }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs args)
    { Interlocked.Increment(ref _adapterGeneration); _viewport.SetPlatformHandle(null); }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;
        _viewport.UsesCompositedRendering = args is LinuxWpeWebViewEnvironmentRequestedEventArgs;
        switch (args)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = _userDataDirectory;
                windows.ProfileName = "sunder-package";
                windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
                windows.IsInPrivateModeEnabled = true;
                break;
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
            case LinuxWpeWebViewEnvironmentRequestedEventArgs linux:
                // WPE is composited by Avalonia and already obeys its rounded clips.
                linux.DataDirectory = Path.Combine(_userDataDirectory, "data");
                linux.CacheDirectory = Path.Combine(_userDataDirectory, "cache");
                break;
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        var translated = new AppWebNavigationStartingEventArgs(args.Request);
        NavigationStarting?.Invoke(this, translated);
        args.Cancel = translated.Cancel;
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
        => NavigationCompleted?.Invoke(
            this,
            new AppWebNavigationCompletedEventArgs(args.Request, args.IsSuccess));

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        var translated = new AppWebNewWindowRequestedEventArgs(args.Request);
        NewWindowRequested?.Invoke(this, translated);
        args.Handled = translated.Handled;
    }

    private void OnWebResourceRequested(object? sender, WebResourceRequestedEventArgs args)
    {
        var translated = new AppWebResourceRequestedEventArgs(
            args.Request.Uri,
            args.Request.Method.Method);
        ResourceRequested?.Invoke(this, translated);
        if (translated.Cancel)
        {
            _webView.Stop();
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
        => MessageReceived?.Invoke(this, new AppWebMessageReceivedEventArgs(args.Body));
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
