using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>>> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _requestTasks = [];
    private WebApplication? _host;
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
            if (_host is not null)
            {
                return;
            }

            var host = TryStartHost(_port, out var startException);
            var actualPort = _port;
            if (host is null && _fallbackPort is { } fallbackPort && fallbackPort != _port)
            {
                _logger.LogWarning(
                    startException,
                    "Package callback listener could not start on {CallbackUri}; trying fallback port {FallbackPort}",
                    BaseUri,
                    fallbackPort);
                host = TryStartHost(fallbackPort, out startException);
                actualPort = fallbackPort;
            }

            if (host is null)
            {
                throw new InvalidOperationException(
                    _fallbackPort is { } configuredFallbackPort && configuredFallbackPort != _port
                        ? $"Sunder could not start the local package callback listener on {CreateBaseUri(_port)} or {CreateBaseUri(configuredFallbackPort)}. Close applications using ports {_port} and {configuredFallbackPort} and retry."
                        : $"Sunder could not start the local package callback listener on {BaseUri} because that address is already in use. Close applications using port {_port} and retry.",
                    startException);
            }

            _port = actualPort;
            BaseUri = CreateBaseUri(actualPort);
            _host = host;
            _logger.LogInformation("Package callback listener started on {CallbackUri}", BaseUri);
        }
    }

    private WebApplication? TryStartHost(int port, out Exception? exception)
    {
        WebApplication? host = null;
        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(PackageCallbackServer).Assembly.FullName,
                Args = [],
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(options => ConfigureKestrel(options, port));
            host = builder.Build();
            host.Run(HandleRequestSafelyAsync);
            host.StartAsync().GetAwaiter().GetResult();
            exception = null;
            return host;
        }
        catch (Exception ex)
        {
            host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            exception = ex;
            return null;
        }
    }

    private void ConfigureKestrel(KestrelServerOptions options, int port)
    {
        options.AddServerHeader = false;
        options.Limits.MaxRequestLineSize = _policy.MaxCallbackRequestLineBytes;
        options.Limits.MaxRequestHeaderCount = _policy.MaxCallbackHeaderCount;
        options.Limits.MaxRequestHeadersTotalSize = _policy.MaxCallbackHeaderBytes;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        options.Listen(IPAddress.Loopback, port);
        if (Socket.OSSupportsIPv6)
        {
            options.Listen(IPAddress.IPv6Loopback, port);
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
                var host = _host;
                _host = null;
                stopTask = StopCoreAsync(host);
                _stopTask = stopTask;
            }
        }
        await stopTask.WaitAsync(cancellationToken);
    }

    private async Task StopCoreAsync(WebApplication? host)
    {
        if (host is null)
        {
            return;
        }

        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            Task[] requestTasks;
            lock (_syncRoot)
            {
                requestTasks = _requestTasks.ToArray();
            }
            await Task.WhenAll(requestTasks);
            await host.DisposeAsync();
        }
    }

    private async Task HandleRequestSafelyAsync(HttpContext context)
    {
        TaskCompletionSource? requestCompletion = null;
        var unavailable = false;
        lock (_syncRoot)
        {
            _requestTasks.RemoveWhere(static task => task.IsCompleted);
            unavailable = _disposed;
            if (!unavailable && _requestTasks.Count < _maxConcurrentRequests)
            {
                requestCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _requestTasks.Add(requestCompletion.Task);
            }
        }

        if (requestCompletion is null)
        {
            await WriteResponseAsync(
                context.Response,
                false,
                unavailable ? "The callback listener is shutting down." : "Too many callback requests.",
                statusCode: unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests);
            return;
        }

        try
        {
            await HandleRequestAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Package callback request failed");
            if (!context.Response.HasStarted)
            {
                try
                {
                    await WriteResponseAsync(
                        context.Response,
                        false,
                        "The callback request failed.",
                        statusCode: HttpStatusCode.InternalServerError);
                }
                catch
                {
                    context.Abort();
                }
            }
            else
            {
                context.Abort();
            }
        }
        finally
        {
            requestCompletion.TrySetResult();
            lock (_syncRoot)
            {
                _requestTasks.Remove(requestCompletion.Task);
            }
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!TryValidateTransport(context, out var queryValues, out var transportError))
        {
            await WriteResponseAsync(
                context.Response,
                false,
                transportError,
                statusCode: HttpStatusCode.BadRequest);
            return;
        }

        var callbackSessionId = GetSessionId(context.Request, queryValues);
        _logger.LogInformation(
            "Package callback request received. HasSession={HasSession} HasCode={HasCode} HasError={HasError}",
            !string.IsNullOrWhiteSpace(callbackSessionId),
            queryValues.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code),
            queryValues.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error));
        if (string.IsNullOrWhiteSpace(callbackSessionId))
        {
            _logger.LogWarning("Package callback request had an invalid path.");
            await WriteResponseAsync(
                context.Response,
                false,
                "Invalid callback path.",
                statusCode: HttpStatusCode.BadRequest);
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
            await WriteResponseAsync(
                context.Response,
                false,
                "No matching callback session was found.",
                statusCode: HttpStatusCode.NotFound);
            return;
        }

        var completionTask = Task.Run(() => CompleteCallbackSafelyAsync(callbackSessionId, handler, queryValues));
        try
        {
            await WriteResponseAsync(
                context.Response,
                success: true,
                "Authorization received. Return to Sunder while sign-in finishes.",
                "Authorization received",
                HttpStatusCode.Accepted);
        }
        finally
        {
            await completionTask;
        }
    }

    private async Task CompleteCallbackSafelyAsync(
        string callbackSessionId,
        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>> handler,
        IReadOnlyDictionary<string, string?> queryValues)
    {
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
    }

    private bool TryValidateTransport(
        HttpContext context,
        out IReadOnlyDictionary<string, string?> values,
        out string error)
    {
        var request = context.Request;
        if (context.Connection.RemoteIpAddress is not { } remoteAddress
            || !IPAddress.IsLoopback(remoteAddress))
        {
            values = new Dictionary<string, string?>();
            error = "The callback request was not received from the local machine.";
            return false;
        }
        if (!string.Equals(request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || request.Host.Port != _port)
        {
            values = new Dictionary<string, string?>();
            error = "The callback request had an invalid authority.";
            return false;
        }
        if (!HttpMethods.IsGet(request.Method)
            || request.ContentLength is > 0
            || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            values = new Dictionary<string, string?>();
            error = "The callback request was invalid.";
            return false;
        }
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(rawTarget) > _policy.MaxCallbackRequestLineBytes)
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
        var headerCount = 0;
        foreach (var header in request.Headers)
        {
            var headerValues = header.Value.Count == 0 ? new[] { string.Empty } : header.Value.ToArray();
            foreach (var value in headerValues)
            {
                headerCount++;
                var lineBytes = Encoding.UTF8.GetByteCount(header.Key)
                    + Encoding.UTF8.GetByteCount(value ?? string.Empty);
                if (headerCount > _policy.MaxCallbackHeaderCount)
                {
                    values = new Dictionary<string, string?>();
                    error = "The callback request contained too many headers.";
                    return false;
                }
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
        }
        return TryReadQuery(request, out values, out error);
    }

    private static string? GetSessionId(
        HttpRequest request,
        IReadOnlyDictionary<string, string?> queryValues)
    {
        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sessionId = segments switch
        {
            ["callbacks", var pathSessionId] => pathSessionId,
            ["auth", "callback"] => GetAuthenticationSessionId(queryValues.GetValueOrDefault("state")),
            _ => null,
        };
        return sessionId?.Length is > 0 and <= 128 ? sessionId : null;
    }

    private static string? GetAuthenticationSessionId(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return state;
        }

        // Providers may append a package-validated state suffix. Runtime session ids never contain '.'.
        var suffixIndex = state.IndexOf('.');
        return suffixIndex > 0 ? state[..suffixIndex] : state;
    }

    private bool TryReadQuery(
        HttpRequest request,
        out IReadOnlyDictionary<string, string?> values,
        out string error)
    {
        var query = request.Query;
        if (query.Count > _policy.MaxPackageCallbackQueryValues)
        {
            values = new Dictionary<string, string?>();
            error = "The callback contained too many values.";
            return false;
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var total = 0;
        foreach (var pair in query)
        {
            var key = pair.Key;
            if (string.IsNullOrWhiteSpace(key) || pair.Value.Count != 1)
            {
                values = new Dictionary<string, string?>();
                error = "The callback contained a duplicate value.";
                return false;
            }
            var value = pair.Value[0];
            if (key.Length > _policy.MaxPackageCallbackQueryKeyLength
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
            if (!result.TryAdd(key, value))
            {
                values = new Dictionary<string, string?>();
                error = "The callback contained a duplicate value.";
                return false;
            }
        }
        values = result;
        error = string.Empty;
        return true;
    }

    private static async Task WriteResponseAsync(
        HttpResponse response,
        bool success,
        string message,
        string? title = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var html = AuthorizationResultPage.Build(success, message, title);
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        foreach (var header in AuthorizationResultPage.Headers) response.Headers[header.Name] = header.Value;
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes);
        await response.CompleteAsync();
    }

}
