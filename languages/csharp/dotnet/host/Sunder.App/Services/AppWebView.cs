using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Sunder.App.Services;

internal interface IAppWebView : IDisposable
{
    Control Control { get; }

    event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting;

    event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted;

    event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested;

    event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested;

    event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived;

    void Navigate(Uri uri);

    Task<string?> InvokeScriptAsync(string script);

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

    public AppNativeWebView(string userDataDirectory, IUiDispatcher? uiDispatcher = null)
    {
        _viewport = new(_webView);
        _userDataDirectory = Path.GetFullPath(userDataDirectory);
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        Directory.CreateDirectory(_userDataDirectory);
        _webView.EnvironmentRequested += OnEnvironmentRequested;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.NavigationStarted -= OnNavigationStarted;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.WebResourceRequested -= OnWebResourceRequested;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _webView.Stop();
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;
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
                _viewport.RequiresNativeInset = false;
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
/// HWND/NSView/GTK WebViews cannot inherit the shell's Avalonia rounded clip.
/// Give only those browsers a small graphite frame and a genuinely smaller,
/// reflowed native viewport, rather than cropping HTML or changing every package view.
/// </summary>
internal sealed class AppNativeWebViewViewport : Decorator
{
    private const double DefaultShellRadius = 18;
    private bool _requiresNativeInset = true;
    private Rect _arrangedViewport;
    private readonly List<Border> _clipBorders = [];

    internal AppNativeWebViewViewport(Control browser)
    {
        Child = browser;
        ClipToBounds = true;
    }

    internal bool RequiresNativeInset
    {
        get => _requiresNativeInset;
        set
        {
            if (_requiresNativeInset == value) return;
            _requiresNativeInset = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = RequiresNativeInset ? Inset(DefaultShellRadius, Scaling) : 0;
        Child?.Measure(new Size(Math.Max(0, availableSize.Width - 2 * inset), Math.Max(0, availableSize.Height - 2 * inset)));
        return Child is { } browser ? new Size(browser.DesiredSize.Width + 2 * inset, browser.DesiredSize.Height + 2 * inset) : default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var viewport = CalculateViewport(finalSize);
        _arrangedViewport = viewport;
        if (Child is { } browser)
        {
            // NativeControlHost clamps zero dimensions to one pixel. Visibility is
            // essential when a splitter leaves no contained rectangle to display.
            browser.IsVisible = viewport.Width >= 1 / Scaling && viewport.Height >= 1 / Scaling;
            browser.Arrange(viewport);
        }
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
        LayoutUpdated -= OnLayoutUpdated;
        foreach (var border in _clipBorders) border.PropertyChanged -= OnBoundaryPropertyChanged;
        _clipBorders.Clear();
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
        if (CalculateViewport(Bounds.Size) != _arrangedViewport) InvalidateArrange();
    }

    private Rect CalculateViewport(Size size)
    {
        if (!RequiresNativeInset) return new Rect(size);
        var viewport = ContainedRectangle(new Rect(size), DefaultShellRadius, Scaling);
        foreach (var border in ShellClipBorders().Where(border => border.ClipToBounds))
        {
            if (border.TransformToVisual(this) is not { } transform
                || Math.Abs(transform.M11 - 1) > 0.000001 || Math.Abs(transform.M22 - 1) > 0.000001
                || Math.Abs(transform.M12) > 0.000001 || Math.Abs(transform.M21) > 0.000001) return default;
            var origin = transform.Transform(default(Point));
            var radius = NormalizedMaximumRadius(border.Bounds.Size, border.CornerRadius);
            viewport = viewport.Intersect(ContainedRectangle(new Rect(origin, border.Bounds.Size), radius, Scaling));
        }
        return viewport;
    }

    internal static Rect ContainedRectangle(Rect boundary, double radius, double scaling)
    {
        var inset = Inset(radius, scaling);
        return boundary.Width <= 2 * inset || boundary.Height <= 2 * inset
            ? default : boundary.Deflate(new Thickness(inset));
    }

    private static double NormalizedMaximumRadius(Size size, CornerRadius radius)
    {
        // Avalonia normalizes all four radii with one scale factor. Clamping the
        // maximum to half the shortest side would leak for a single large corner.
        var horizontal = Math.Max(radius.TopLeft + radius.TopRight, radius.BottomLeft + radius.BottomRight);
        var vertical = Math.Max(radius.TopLeft + radius.BottomLeft, radius.TopRight + radius.BottomRight);
        var factor = Math.Min(1, Math.Min(horizontal > 0 ? size.Width / horizontal : 1,
            vertical > 0 ? size.Height / vertical : 1));
        return Math.Max(Math.Max(radius.TopLeft, radius.TopRight), Math.Max(radius.BottomLeft, radius.BottomRight)) * factor;
    }

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
