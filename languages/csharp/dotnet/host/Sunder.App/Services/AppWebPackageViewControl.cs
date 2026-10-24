using Avalonia.Controls;

namespace Sunder.App.Services;

internal sealed class AppWebPackageViewControl : ContentControl, IDisposable, IAppDevelopmentDocumentOwner
{
    private readonly IAppWebView _webView;
    private readonly AppWebBridge _bridge;
    private readonly AppWebContentServer _contentServer;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public AppWebPackageViewControl(
        Uri initialUri,
        string userDataDirectory,
        AppWebContentServer contentServer,
        IAppWebViewFactory webViewFactory,
        IAppWebRpcClient rpcClient,
        AppWebRpcContractCatalog contracts,
        ExternalBrowserService externalBrowser)
    {
        _contentServer = contentServer;
        _webView = webViewFactory.Create(userDataDirectory);
        _bridge = new AppWebBridge(
            _webView,
            contentServer,
            rpcClient,
            contracts,
            externalBrowser,
            Navigate);
        DevelopmentDocument = new AppWebDevelopmentDocument(_webView, _bridge, this);
        Content = _webView.Control;
        _webView.NavigationStarting += OnNavigationStarting;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.ResourceRequested += OnResourceRequested;
        _webView.MessageReceived += OnMessageReceived;
        _webView.Navigate(initialUri);
    }

    internal AppWebBridge Bridge => _bridge;
    public IAppDevelopmentDocument DevelopmentDocument { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        _webView.NavigationStarting -= OnNavigationStarting;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.NewWindowRequested -= OnNewWindowRequested;
        _webView.ResourceRequested -= OnResourceRequested;
        _webView.MessageReceived -= OnMessageReceived;
        var cleanup = DisposeBridgeAndViewAsync();
        if (!cleanup.IsCompletedSuccessfully)
        {
            AppCleanupQuarantine.Retain(cleanup, "disposing a package browser bridge");
        }
    }

    private async Task DisposeBridgeAndViewAsync()
    {
        try
        {
            // Notify the live document before destroying its WebView. Each bridge
            // owns its requests; the non-disposing client preserves sibling views.
            await _bridge.DisposeAsync();
        }
        finally
        {
            Content = null;
            _webView.Dispose();
            _lifetime.Dispose();
        }
    }

    private void Navigate(Uri uri)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _webView.Navigate(uri);
        }
    }

    private void OnNavigationStarting(object? sender, AppWebNavigationStartingEventArgs args)
    {
        if (!AppWebNavigationPolicy.IsPackageUri(_contentServer, args.Uri))
        {
            args.Cancel = true;
            return;
        }
        Observe(_bridge.NavigationStartingAsync(), "retiring the package browser document");
    }

    private void OnNavigationCompleted(object? sender, AppWebNavigationCompletedEventArgs args)
    {
        if (!args.Succeeded || !AppWebNavigationPolicy.IsPackageUri(_contentServer, args.Uri))
        {
            return;
        }
        Observe(
            _bridge.EstablishAsync(args.Uri!, _lifetime.Token),
            "injecting the package browser bridge");
    }

    private static void OnNewWindowRequested(object? sender, AppWebNewWindowRequestedEventArgs args)
        => args.Handled = true;

    private void OnResourceRequested(object? sender, AppWebResourceRequestedEventArgs args)
    {
        if (!AppWebNavigationPolicy.IsPackageUri(_contentServer, args.Uri)
            || args.Method is not "GET" and not "HEAD")
        {
            args.Cancel = true;
        }
    }

    private void OnMessageReceived(object? sender, AppWebMessageReceivedEventArgs args)
        => Observe(
            _bridge.HandleMessageAsync(args.Body, _lifetime.Token),
            "handling a package browser bridge message");

    private static async void Observe(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError($"Failed while {operation}.", exception);
        }
    }
}

internal sealed class NonDisposingAppWebRpcClient(IAppWebRpcClient inner) : IAppWebRpcClient
{
    public Sunder.Runtime.Client.RuntimeClientResourceLimits ResourceLimits => inner.ResourceLimits;

    public ValueTask<Sunder.Sdk.Rpc.SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken)
        => inner.DiscoverAsync(contractId, cancellationToken);

    public IAsyncEnumerable<Sunder.Sdk.Rpc.SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken)
        => inner.WatchAsync(afterRevision, afterSequence, cancellationToken);

    public ValueTask<System.Text.Json.JsonElement> InvokeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        System.Text.Json.JsonElement request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken)
        => inner.InvokeAsync(
            endpointReference,
            serviceId,
            methodId,
            request,
            deadlineUtc,
            cancellationToken);

    public IAsyncEnumerable<System.Text.Json.JsonElement> SubscribeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        System.Text.Json.JsonElement request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken)
        => inner.SubscribeAsync(
            endpointReference,
            serviceId,
            methodId,
            request,
            deadlineUtc,
            cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
