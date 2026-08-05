using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageCallbackServer : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly ILogger<PackageCallbackServer> _logger;
    private readonly RuntimeAuthPolicyOptions _policy;
    private readonly int? _fallbackPort;
    private readonly int _maxConcurrentRequests;
    private int _port;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _requestTasks = [];
    private HttpListener? _listener;
    private Task? _listenTask;
    private Task? _stopTask;
    private bool _disposed;

    public PackageCallbackServer(ILogger<PackageCallbackServer> logger)
        : this(logger, new RuntimeAuthPolicyOptions())
    {
    }

    public PackageCallbackServer(ILogger<PackageCallbackServer> logger, RuntimeAuthPolicyOptions policy)
        : this(logger, policy.PackageCallbackPort, policy.PackageCallbackFallbackPort, policy.MaxConcurrentPackageCallbacks)
    {
        _policy = policy;
    }

    internal PackageCallbackServer(ILogger<PackageCallbackServer> logger, int port)
        : this(logger, port, fallbackPort: null, new RuntimeAuthPolicyOptions().MaxConcurrentPackageCallbacks)
    {
    }

    private PackageCallbackServer(ILogger<PackageCallbackServer> logger, int port, int? fallbackPort, int maxConcurrentRequests)
    {
        _logger = logger;
        _policy = new RuntimeAuthPolicyOptions
        {
            PackageCallbackPort = port,
            PackageCallbackFallbackPort = fallbackPort ?? port,
            MaxConcurrentPackageCallbacks = maxConcurrentRequests,
        };
        _port = port;
        _fallbackPort = fallbackPort;
        _maxConcurrentRequests = maxConcurrentRequests;
        BaseUri = CreateBaseUri(_port);
    }

    public Uri BaseUri { get; private set; }

    public Uri GetCallbackUri(string callbackSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackSessionId);
        return new Uri(BaseUri, $"callbacks/{Uri.EscapeDataString(callbackSessionId)}");
    }

    public Uri GetAuthenticationCallbackUri()
        => new(BaseUri, "auth/callback");

    internal int RegisteredHandlerCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _handlers.Count;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void EnsureStarted()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
            {
                return;
            }

            var listener = TryStartListener(_port, out var startException);
            var actualPort = _port;
            if (listener is null && _fallbackPort is { } fallbackPort && fallbackPort != _port)
            {
                _logger.LogWarning(
                    startException,
                    "Package callback listener could not start on {CallbackUri}; trying fallback port {FallbackPort}",
                    BaseUri,
                    fallbackPort);
                listener = TryStartListener(fallbackPort, out startException);
                actualPort = fallbackPort;
            }

            if (listener is null)
            {
                throw new InvalidOperationException(
                    _fallbackPort is { } configuredFallbackPort && configuredFallbackPort != _port
                        ? $"Sunder could not start the local package callback listener on {CreateBaseUri(_port)} or {CreateBaseUri(configuredFallbackPort)}. Close applications using ports {_port} and {configuredFallbackPort} and retry."
                        : $"Sunder could not start the local package callback listener on {BaseUri} because that address is already in use. Close applications using port {_port} and retry.",
                    startException);
            }

            _port = actualPort;
            BaseUri = CreateBaseUri(actualPort);
            _listener = listener;
            _listenTask = ListenLoopAsync(listener);
            _logger.LogInformation("Package callback listener started on {CallbackUri}", BaseUri);
        }
    }

    private static HttpListener? TryStartListener(int port, out Exception? exception)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(CreateBaseUri(port).AbsoluteUri);
        try
        {
            listener.Start();
            exception = null;
            return listener;
        }
        catch (Exception ex)
        {
            listener.Close();
            exception = ex;
            return null;
        }
    }

    private static Uri CreateBaseUri(int port)
        => new($"http://localhost:{port}/");

    public void RegisterHandler(string authSessionId, Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>> handler)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _handlers[authSessionId] = handler;
        }

        _logger.LogInformation("Package callback handler registered.");
    }

    public bool UnregisterHandler(string authSessionId)
    {
        lock (_syncRoot)
        {
            return _handlers.Remove(authSessionId);
        }
    }

    public void Dispose()
        => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
        => new(StopAsync(CancellationToken.None));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_syncRoot)
        {
            if (_stopTask is not null)
            {
                stopTask = _stopTask;
            }
            else
            {
                _disposed = true;
                _handlers.Clear();
                var listener = _listener;
                var listenTask = _listenTask;
                _listener = null;
                stopTask = StopCoreAsync(listener, listenTask);
                _stopTask = stopTask;
            }
        }
        await stopTask.WaitAsync(cancellationToken);
    }

    private async Task StopCoreAsync(HttpListener? listener, Task? listenTask)
    {
        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch (HttpListenerException)
        {
            // A concurrent listener shutdown can race endpoint-manager cleanup.
        }

        if (listenTask is not null)
        {
            await listenTask;
        }
        Task[] requestTasks;
        lock (_syncRoot)
        {
            requestTasks = _requestTasks.ToArray();
        }
        await Task.WhenAll(requestTasks);
    }

    private async Task ListenLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
                Task? requestTask = null;
                lock (_syncRoot)
                {
                    _requestTasks.RemoveWhere(task => task.IsCompleted);
                    if (!_disposed && _requestTasks.Count < _maxConcurrentRequests)
                    {
                        requestTask = HandleRequestSafelyAsync(context);
                        _requestTasks.Add(requestTask);
                    }
                }
                if (requestTask is null)
                {
                    await WriteResponseAsync(context.Response, false, "Too many callback requests.");
                }
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected package callback listener failure");
                if (context is not null)
                {
                    try { context.Response.OutputStream.Close(); } catch { }
                }
            }
        }
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Package callback request failed");
            try { context.Response.Abort(); } catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var callbackSessionId = GetSessionId(context.Request);
        _logger.LogInformation(
            "Package callback request received. HasSession={HasSession} HasCode={HasCode} HasError={HasError}",
            !string.IsNullOrWhiteSpace(callbackSessionId),
            !string.IsNullOrWhiteSpace(context.Request.QueryString["code"]),
            !string.IsNullOrWhiteSpace(context.Request.QueryString["error"]));
        if (string.IsNullOrWhiteSpace(callbackSessionId))
        {
            _logger.LogWarning("Package callback request had an invalid path.");
            await WriteResponseAsync(context.Response, false, "Invalid callback path.");
            return;
        }

        if (!TryValidateTransport(context.Request, out var queryValues, out var transportError))
        {
            await WriteResponseAsync(context.Response, false, transportError);
            return;
        }

        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>>? handler;
        lock (_syncRoot)
        {
            if (_handlers.Remove(callbackSessionId, out var foundHandler))
            {
                handler = foundHandler;
            }
            else
            {
                handler = null;
            }
        }

        if (handler is null)
        {
            _logger.LogWarning("Package callback request did not match a registered handler.");
            await WriteResponseAsync(context.Response, false, "No matching callback session was found.");
            return;
        }

        bool completed;
        try
        {
            completed = await handler(queryValues, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Package callback handling failed for session {CallbackSessionId}", callbackSessionId);
            completed = false;
        }
        _logger.LogInformation("Package callback handling completed. Completed={Completed}", completed);

        await WriteResponseAsync(context.Response, completed, completed
            ? "You can close this browser window and return to Sunder."
            : "The callback failed. Return to Sunder and retry.");
    }

    private bool TryValidateTransport(
        HttpListenerRequest request,
        out IReadOnlyDictionary<string, string?> values,
        out string error)
    {
        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            || request.ContentLength64 > 0)
        {
            values = new Dictionary<string, string?>();
            error = "The callback request was invalid.";
            return false;
        }
        if ((request.RawUrl?.Length ?? 0) > _policy.MaxCallbackRequestLineBytes)
        {
            values = new Dictionary<string, string?>();
            error = "The callback request was too large.";
            return false;
        }
        if (request.Headers.Count > _policy.MaxCallbackHeaderCount)
        {
            values = new Dictionary<string, string?>();
            error = "The callback request contained too many headers.";
            return false;
        }

        var headerBytes = 0;
        foreach (var key in request.Headers.AllKeys)
        {
            var value = request.Headers[key];
            var lineBytes = key is null
                ? int.MaxValue
                : Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (lineBytes > _policy.MaxCallbackHeaderLineBytes)
            {
                values = new Dictionary<string, string?>();
                error = "The callback request contained an oversized header.";
                return false;
            }
            headerBytes = checked(headerBytes + lineBytes);
            if (headerBytes > _policy.MaxCallbackHeaderBytes)
            {
                values = new Dictionary<string, string?>();
                error = "The callback request headers were too large.";
                return false;
            }
        }
        return TryReadQuery(request, out values, out error);
    }

    private static string? GetSessionId(HttpListenerRequest request)
    {
        var segments = request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sessionId = segments switch
        {
            ["callbacks", var pathSessionId] => Uri.UnescapeDataString(pathSessionId),
            ["auth", "callback"] => request.QueryString["state"],
            _ => null,
        };
        return sessionId?.Length is > 0 and <= 128 ? sessionId : null;
    }

    private bool TryReadQuery(
        HttpListenerRequest request,
        out IReadOnlyDictionary<string, string?> values,
        out string error)
    {
        var keys = request.QueryString.AllKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).ToArray();
        if (keys.Length > _policy.MaxPackageCallbackQueryValues)
        {
            values = new Dictionary<string, string?>();
            error = "The callback contained too many values.";
            return false;
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var key in keys)
        {
            var value = request.QueryString[key!];
            if (key!.Length > _policy.MaxPackageCallbackQueryKeyLength
                || value?.Length > _policy.MaxPackageCallbackQueryValueLength)
            {
                values = new Dictionary<string, string?>();
                error = "The callback contained an oversized value.";
                return false;
            }
            total = checked(total + key.Length + (value?.Length ?? 0));
            if (total > _policy.MaxPackageCallbackQueryCharacters)
            {
                values = new Dictionary<string, string?>();
                error = "The callback exceeded the allowed size.";
                return false;
            }
            result[key] = value;
        }
        values = result;
        error = string.Empty;
        return true;
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, bool success, string message)
    {
        var html = BuildCallbackPage(success, message);
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private static string BuildCallbackPage(bool success, string message)
    {
        var title = WebUtility.HtmlEncode(success ? "Authorization complete" : "Authorization failed");
        var subtitle = WebUtility.HtmlEncode(message);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{title}}</title>
                <link rel="preconnect" href="https://fonts.googleapis.com" />
                <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
                <link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600;700&display=swap" rel="stylesheet" />
                <style>
                    :root {
                        color-scheme: dark;
                        --bg: #15171a;
                        --bg-lifted: #1d2025;
                        --text: #dedad3;
                        --muted: #ccc7be;
                        --accent-strong: #e7b765;
                        --accent-rgb: 217, 154, 58;
                        --success-rgb: 110, 231, 183;
                        --white-rgb: 255, 255, 255;
                        font-family: "IBM Plex Sans", system-ui, sans-serif;
                    }

                    * {
                        box-sizing: border-box;
                    }

                    body {
                        margin: 0;
                        min-height: 100vh;
                        background:
                            radial-gradient(circle at 20% 10%, rgba(var(--accent-rgb), 0.18), transparent 28rem),
                            radial-gradient(circle at 85% 0%, rgba(var(--success-rgb), 0.12), transparent 24rem),
                            linear-gradient(180deg, var(--bg-lifted) 0%, var(--bg) 42rem);
                        color: var(--text);
                    }

                    .boot-shell {
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        gap: 14px;
                        min-height: 100vh;
                    }

                    .boot-mark {
                        display: grid;
                        width: 44px;
                        height: 44px;
                        place-items: center;
                        border: 1px solid rgba(var(--accent-rgb), 0.46);
                        border-radius: 14px;
                        background: linear-gradient(135deg, rgba(var(--accent-rgb), 0.2), rgba(var(--white-rgb), 0.04));
                        color: var(--accent-strong);
                        font-weight: 800;
                    }

                    .boot-title {
                        font-weight: 800;
                    }

                    .boot-subtitle {
                        color: var(--muted);
                    }
                </style>
            </head>
            <body>
                <main class="boot-shell">
                    <div class="boot-mark">S</div>
                    <div>
                        <div class="boot-title">{{title}}</div>
                        <div class="boot-subtitle">{{subtitle}}</div>
                    </div>
                </main>
            </body>
            </html>
            """;
    }
}
