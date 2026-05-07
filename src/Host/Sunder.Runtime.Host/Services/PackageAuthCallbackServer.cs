using System.Net;
using System.Text;

namespace Sunder.Runtime.Host.Services;

public sealed class PackageAuthCallbackServer : IDisposable
{
    private readonly ILogger<PackageAuthCallbackServer> _logger;
    private readonly int _port;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private HttpListener? _listener;
    private Task? _listenTask;

    public PackageAuthCallbackServer(ILogger<PackageAuthCallbackServer> logger)
        : this(logger, port: 1455)
    {
    }

    internal PackageAuthCallbackServer(ILogger<PackageAuthCallbackServer> logger, int port)
    {
        _logger = logger;
        _port = port;
        CallbackUri = new Uri($"http://localhost:{_port}/auth/callback/");
    }

    public Uri CallbackUri { get; }

    public void EnsureStarted()
    {
        lock (_syncRoot)
        {
            if (_listener is not null)
            {
                return;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{_port}/auth/callback/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                throw new InvalidOperationException(
                    $"Sunder could not start the local browser callback listener on {CallbackUri} because that address is already in use. Close other Codex/Sunder auth listeners or applications using port {_port} and retry.",
                    ex);
            }
            _listener = listener;
            _listenTask = Task.Run(ListenLoopAsync);
        }
    }

    public void RegisterHandler(string authSessionId, Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>> handler)
    {
        lock (_syncRoot)
        {
            _handlers[authSessionId] = handler;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
            _handlers.Clear();
        }
    }

    private async Task ListenLoopAsync()
    {
        while (_listener is { IsListening: true } listener)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
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
                _logger.LogError(ex, "Unexpected package auth callback listener failure");
                if (context is not null)
                {
                    try { context.Response.OutputStream.Close(); } catch { }
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var authSessionId = context.Request.QueryString["state"];
        if (string.IsNullOrWhiteSpace(authSessionId))
        {
            await WriteResponseAsync(context.Response, false, "Missing state.");
            return;
        }

        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>>? handler;
        lock (_syncRoot)
        {
            handler = _handlers.TryGetValue(authSessionId, out var foundHandler) ? foundHandler : null;
        }

        if (handler is null)
        {
            await WriteResponseAsync(context.Response, false, "No matching authorization session was found.");
            return;
        }

        var queryValues = context.Request.QueryString.AllKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToDictionary(key => key!, key => (string?)context.Request.QueryString[key!], StringComparer.OrdinalIgnoreCase);

        bool completed;
        try
        {
            completed = await handler(queryValues, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Package auth callback handling failed for session {AuthSessionId}", authSessionId);
            completed = false;
        }
        finally
        {
            lock (_syncRoot)
            {
                _handlers.Remove(authSessionId);
            }
        }

        await WriteResponseAsync(context.Response, completed, completed
            ? "You can close this browser window and return to Sunder."
            : "Authorization failed. Return to Sunder and retry.");
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
                        --bg: #080b10;
                        --text: #f2efe8;
                        --muted: #9ea8b7;
                        --accent-strong: #ffcf7d;
                        font-family: "IBM Plex Sans", system-ui, sans-serif;
                    }

                    * {
                        box-sizing: border-box;
                    }

                    body {
                        margin: 0;
                        min-height: 100vh;
                        background:
                            radial-gradient(circle at 20% 10%, rgba(255, 180, 76, 0.18), transparent 28rem),
                            radial-gradient(circle at 85% 0%, rgba(110, 231, 183, 0.12), transparent 24rem),
                            linear-gradient(180deg, #0d1118 0%, var(--bg) 42rem);
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
                        border: 1px solid rgba(255, 180, 76, 0.46);
                        border-radius: 14px;
                        background: linear-gradient(135deg, rgba(255, 180, 76, 0.2), rgba(255, 255, 255, 0.04));
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
