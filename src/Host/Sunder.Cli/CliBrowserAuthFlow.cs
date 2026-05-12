using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Sunder.Cli;

internal sealed class CliBrowserAuthFlow(Uri registryUrl, RegistryClient registryClient)
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public async Task<RegistryLoginResult> LoginAsync(CancellationToken cancellationToken)
    {
        await using var listener = new LoopbackCallbackListener();
        var state = CreateRandomToken(24);
        var codeVerifier = CreateRandomToken(32);
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var authorizeUri = BuildAuthorizeUri(listener.CallbackUri, state, codeChallenge);

        ConsoleOutput.WriteInfo("Opening browser for Sunder registry sign-in...");
        OpenBrowser(authorizeUri);
        ConsoleOutput.WriteInfo($"If the browser did not open, visit: {authorizeUri}");

        var callback = await listener.WaitForCallbackAsync(LoginTimeout, cancellationToken);
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CLI authorization state did not match.");
        }

        var token = await registryClient.ExchangeCliTokenAsync(callback.Code, codeVerifier, cancellationToken);
        if (!token.Success || string.IsNullOrWhiteSpace(token.Token))
        {
            throw new InvalidOperationException(string.Join(" ", token.Errors.DefaultIfEmpty("CLI token exchange failed.")));
        }

        return new RegistryLoginResult(token.Token, token.UserId, token.ExpiresAtUtc);
    }

    private Uri BuildAuthorizeUri(Uri callbackUri, string state, string codeChallenge)
    {
        var builder = new UriBuilder(registryUrl)
        {
            Path = CombinePath(registryUrl.AbsolutePath, "cli/authorize"),
            Query = string.Join('&',
                $"redirect_uri={Uri.EscapeDataString(callbackUri.ToString())}",
                $"state={Uri.EscapeDataString(state)}",
                $"code_challenge={Uri.EscapeDataString(codeChallenge)}"),
        };

        return builder.Uri;
    }

    private static string CombinePath(string basePath, string relativePath)
    {
        basePath = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath;
        if (!basePath.EndsWith('/'))
        {
            basePath += "/";
        }

        return basePath + relativePath.TrimStart('/');
    }

    private static void OpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteWarning($"Could not open browser automatically: {ex.Message}");
        }
    }

    private static string CreateCodeChallenge(string codeVerifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string CreateRandomToken(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class LoopbackCallbackListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public LoopbackCallbackListener()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            CallbackUri = new Uri($"http://127.0.0.1:{port}/callback");
        }

        public Uri CallbackUri { get; }

        public async Task<CliCallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(linkedCts.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(linkedCts.Token);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    throw new InvalidOperationException("CLI authorization callback was empty.");
                }

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(linkedCts.Token)))
                {
                }

                var result = ParseRequestLine(requestLine);
                await WriteResponseAsync(stream, success: true, linkedCts.Token);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for registry browser authorization.");
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }

        private static CliCallbackResult ParseRequestLine(string requestLine)
        {
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("CLI authorization callback was invalid.");
            }

            var callbackUri = new Uri("http://127.0.0.1" + parts[1]);
            var query = ParseQuery(callbackUri.Query);
            if (!query.TryGetValue("code", out var code) || !query.TryGetValue("state", out var state))
            {
                throw new InvalidOperationException("CLI authorization callback did not include a code and state.");
            }

            return new CliCallbackResult(code, state);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = segment.Split('=', 2);
                if (parts.Length == 2)
                {
                    result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1].Replace('+', ' '));
                }
            }

            return result;
        }

        private static async Task WriteResponseAsync(Stream stream, bool success, CancellationToken cancellationToken)
        {
            var body = BuildCallbackPage(success);
            var bytes = Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" +
                body);
            await stream.WriteAsync(bytes, cancellationToken);
        }

        private static string BuildCallbackPage(bool success)
        {
            var title = success ? "Sunder CLI authorized" : "Sunder CLI authorization failed";
            var subtitle = success
                ? "You can close this window and return to your terminal."
                : "Return to your terminal and retry sign-in.";

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
}

internal sealed record RegistryLoginResult(string Token, string? UserId, DateTimeOffset? ExpiresAtUtc);

internal sealed record CliCallbackResult(string Code, string State);
