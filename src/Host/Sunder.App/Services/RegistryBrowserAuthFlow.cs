using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Sunder.App.Services;

public sealed class RegistryBrowserAuthFlow(
    Uri registryUrl,
    IRegistryApiClient registryClient,
    ExternalBrowserService browserService)
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public async Task<RegistryBrowserAuthResult> LoginAsync(CancellationToken cancellationToken = default)
    {
        await using var listener = new LoopbackCallbackListener();
        var state = CreateRandomToken(24);
        var codeVerifier = CreateRandomToken(32);
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var authorizeUri = BuildAuthorizeUri(listener.CallbackUri, state, codeChallenge);

        browserService.Open(authorizeUri);

        var callback = await listener.WaitForCallbackAsync(LoginTimeout, cancellationToken);
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Registry authorization state did not match.");
        }

        var token = await registryClient.ExchangeCliTokenAsync(callback.Code, codeVerifier, cancellationToken);
        if (!token.Success || string.IsNullOrWhiteSpace(token.Token))
        {
            throw new InvalidOperationException(string.Join(" ", token.Errors.DefaultIfEmpty("Registry token exchange failed.")));
        }

        return new RegistryBrowserAuthResult(token.Token, token.UserId, token.ExpiresAtUtc);
    }

    private Uri BuildAuthorizeUri(Uri callbackUri, string state, string codeChallenge)
    {
        var builder = new UriBuilder(registryUrl)
        {
            Path = CombinePath(registryUrl.AbsolutePath, "cli/authorize"),
            Query = string.Join('&',
                $"redirect_uri={Uri.EscapeDataString(callbackUri.ToString())}",
                $"state={Uri.EscapeDataString(state)}",
                $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
                "display_name=Sunder%20App"),
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

        public async Task<RegistryCallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken)
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
                    throw new InvalidOperationException("Registry authorization callback was empty.");
                }

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(linkedCts.Token)))
                {
                }

                var result = ParseRequestLine(requestLine);
                await WriteResponseAsync(stream, linkedCts.Token);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for Registry browser authorization.");
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }

        private static RegistryCallbackResult ParseRequestLine(string requestLine)
        {
            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Registry authorization callback was invalid.");
            }

            var callbackUri = new Uri("http://127.0.0.1" + parts[1]);
            var query = ParseQuery(callbackUri.Query);
            if (!query.TryGetValue("code", out var code) || !query.TryGetValue("state", out var state))
            {
                throw new InvalidOperationException("Registry authorization callback did not include a code and state.");
            }

            return new RegistryCallbackResult(code, state);
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

        private static async Task WriteResponseAsync(Stream stream, CancellationToken cancellationToken)
        {
            const string body = "<!doctype html><html><body><h1>Sunder Registry authorized</h1><p>You can close this window and return to Sunder.</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" +
                body);
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }
}

public sealed record RegistryBrowserAuthResult(string Token, string? UserId, DateTimeOffset? ExpiresAtUtc);

internal sealed record RegistryCallbackResult(string Code, string State);
