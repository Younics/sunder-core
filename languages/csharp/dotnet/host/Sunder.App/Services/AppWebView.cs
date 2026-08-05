using Avalonia.Controls;
using Avalonia.Platform;

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
    private readonly string _userDataDirectory;
    private readonly IUiDispatcher _uiDispatcher;
    private int _disposed;

    public AppNativeWebView(string userDataDirectory, IUiDispatcher? uiDispatcher = null)
    {
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

    public Control Control => _webView;

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
