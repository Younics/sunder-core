using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryAuthCoordinator(
    IHttpClientFactory httpClientFactory,
    RegistryCredentialStore credentialStore,
    ILogger<RegistryAuthCoordinator> logger)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new(StringComparer.Ordinal);

    public RuntimeRegistryAuthStartResponse Start(RuntimeRegistryAuthStartRequest request)
    {
        var registryOrigin = RegistryOrigin.Normalize(request.RegistryOrigin);
        var authorizationOrigin = RegistryOrigin.NormalizeAuthorizationOrigin(request.AuthorizationOrigin, registryOrigin);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var sessionId = Guid.NewGuid().ToString("N");
        var state = CreateRandomToken(24);
        var verifier = CreateRandomToken(32);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var callback = new Uri($"http://127.0.0.1:{port}/callback");
        var expiresAtUtc = DateTimeOffset.UtcNow + SessionLifetime;
        var launchUrl = BuildAuthorizeUri(authorizationOrigin, callback, state, challenge, request.DisplayName);
        var session = new AuthSession(sessionId, registryOrigin, state, verifier, listener, expiresAtUtc);
        if (!_sessions.TryAdd(sessionId, session))
        {
            listener.Stop();
            throw new InvalidOperationException("Could not allocate a Registry authorization session.");
        }

        _ = CompleteAsync(session);
        return new RuntimeRegistryAuthStartResponse(sessionId, registryOrigin.AbsoluteUri, launchUrl.AbsoluteUri, expiresAtUtc);
    }

    public RuntimeRegistryAuthSessionStatus? GetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.Status : null;

    public async Task<RuntimeRegistryAuthStatus> GetStatusAsync(string registryOriginValue, CancellationToken cancellationToken)
    {
        var registryOrigin = RegistryOrigin.Normalize(registryOriginValue);
        var credential = await credentialStore.GetAsync(registryOrigin, cancellationToken);
        if (credential is null)
        {
            return SignedOut(registryOrigin);
        }

        if (credential.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            await credentialStore.DeleteAsync(registryOrigin, cancellationToken);
            return SignedOut(registryOrigin, "Registry credential expired.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(registryOrigin, "api/v1/me"));
        request.Headers.Authorization = new("Bearer", credential.AccessToken);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await credentialStore.DeleteAsync(registryOrigin, cancellationToken);
            return SignedOut(registryOrigin, "Registry credential is no longer valid.");
        }

        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<RegistryCurrentUserResponse>(cancellationToken: cancellationToken)
                   ?? throw new InvalidDataException("Registry returned an empty current-user response.");
        var updated = credential with
        {
            UserId = user.UserId,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            RequiresUsername = user.RequiresUsername,
        };
        await credentialStore.SetAsync(registryOrigin, updated, cancellationToken);
        return SignedIn(registryOrigin, updated);
    }

    public async Task<RuntimeRegistryAuthStatus> LogoutAsync(string registryOriginValue, CancellationToken cancellationToken)
    {
        var registryOrigin = RegistryOrigin.Normalize(registryOriginValue);
        await credentialStore.DeleteAsync(registryOrigin, cancellationToken);
        return SignedOut(registryOrigin);
    }

    private async Task CompleteAsync(AuthSession session)
    {
        using var timeout = new CancellationTokenSource(SessionLifetime);
        try
        {
            using var client = await session.Listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token)))
            {
            }

            var callback = ParseCallback(requestLine);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(callback.State),
                    Encoding.UTF8.GetBytes(session.State)))
            {
                throw new InvalidOperationException("Registry authorization state did not match.");
            }

            using var tokenResponse = await SendAsync(
                new HttpRequestMessage(HttpMethod.Post, new Uri(session.RegistryOrigin, "api/v1/cli-auth/token"))
                {
                    Content = JsonContent.Create(new RegistryCliTokenRequest(callback.Code, session.CodeVerifier)),
                },
                timeout.Token);
            var token = await tokenResponse.Content.ReadFromJsonAsync<RegistryCliTokenResponse>(cancellationToken: timeout.Token);
            if (token is null || !token.Success || string.IsNullOrWhiteSpace(token.Token))
            {
                throw new InvalidOperationException(string.Join(" ", token?.Errors ?? ["Registry token exchange failed."]));
            }

            var credential = new RegistryCredential(token.Token, token.UserId, token.ExpiresAtUtc, null, null, null, null, false);
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(session.RegistryOrigin, "api/v1/me"));
            profileRequest.Headers.Authorization = new("Bearer", credential.AccessToken);
            using var profileResponse = await SendAsync(profileRequest, timeout.Token);
            profileResponse.EnsureSuccessStatusCode();
            var user = await profileResponse.Content.ReadFromJsonAsync<RegistryCurrentUserResponse>(cancellationToken: timeout.Token)
                       ?? throw new InvalidDataException("Registry returned an empty current-user response.");
            credential = credential with
            {
                UserId = user.UserId,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                AvatarUrl = user.AvatarUrl,
                RequiresUsername = user.RequiresUsername,
            };
            await credentialStore.SetAsync(session.RegistryOrigin, credential, timeout.Token);
            session.Status = new RuntimeRegistryAuthSessionStatus(
                session.SessionId,
                session.RegistryOrigin.AbsoluteUri,
                RuntimeRegistryAuthSessionState.Succeeded,
                ToUser(credential),
                credential.ExpiresAtUtc,
                RuntimeRegistryErrorCode.None,
                "Signed in.");
            await WriteCallbackResponseAsync(stream, true, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            session.Status = session.Status with
            {
                State = RuntimeRegistryAuthSessionState.Expired,
                ErrorCode = RuntimeRegistryErrorCode.Cancelled,
                Message = "Registry authorization timed out.",
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning("Registry authorization session {SessionId} failed: {ErrorType}", session.SessionId, ex.GetType().Name);
            session.Status = session.Status with
            {
                State = RuntimeRegistryAuthSessionState.Failed,
                ErrorCode = RuntimeRegistryErrorCode.AuthenticationRequired,
                Message = ex.Message,
            };
        }
        finally
        {
            session.Listener.Stop();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("registry");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static RuntimeRegistryAuthStatus SignedOut(Uri origin, string? message = null)
        => new(origin.AbsoluteUri, false, null, null, RuntimeRegistryErrorCode.None, message);

    private static RuntimeRegistryAuthStatus SignedIn(Uri origin, RegistryCredential credential)
        => new(origin.AbsoluteUri, true, ToUser(credential), credential.ExpiresAtUtc);

    private static RuntimeRegistryUser ToUser(RegistryCredential credential)
        => new(credential.UserId ?? string.Empty, credential.DisplayName, credential.Email, credential.Username, credential.AvatarUrl, credential.RequiresUsername);

    private static Uri BuildAuthorizeUri(Uri origin, Uri callback, string state, string challenge, string? displayName)
    {
        var builder = new UriBuilder(new Uri(origin, "cli/authorize"));
        builder.Query = string.Join('&',
            $"redirect_uri={Uri.EscapeDataString(callback.AbsoluteUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            $"display_name={Uri.EscapeDataString(string.IsNullOrWhiteSpace(displayName) ? "Sunder" : displayName.Trim())}");
        return builder.Uri;
    }

    private static (string Code, string State) ParseCallback(string? requestLine)
    {
        var parts = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Registry authorization callback was invalid.");
        }

        var query = new Uri("http://127.0.0.1" + parts[1]).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Where(value => value.Length == 2)
            .ToDictionary(value => Uri.UnescapeDataString(value[0]), value => Uri.UnescapeDataString(value[1].Replace('+', ' ')), StringComparer.OrdinalIgnoreCase);
        return query.TryGetValue("code", out var code) && query.TryGetValue("state", out var state)
            ? (code, state)
            : throw new InvalidOperationException("Registry authorization callback did not include code and state.");
    }

    private static async Task WriteCallbackResponseAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var body = $"<!doctype html><html><body><h1>Sunder Registry {(success ? "authorized" : "authorization failed")}</h1><p>You can close this window.</p></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private static string CreateRandomToken(int byteCount) => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AuthSession
    {
        public AuthSession(string sessionId, Uri registryOrigin, string state, string codeVerifier, TcpListener listener, DateTimeOffset expiresAtUtc)
        {
            SessionId = sessionId;
            RegistryOrigin = registryOrigin;
            State = state;
            CodeVerifier = codeVerifier;
            Listener = listener;
            Status = new RuntimeRegistryAuthSessionStatus(sessionId, registryOrigin.AbsoluteUri, RuntimeRegistryAuthSessionState.Pending, null, null, RuntimeRegistryErrorCode.None, null);
        }

        public string SessionId { get; }
        public Uri RegistryOrigin { get; }
        public string State { get; }
        public string CodeVerifier { get; }
        public TcpListener Listener { get; }
        public RuntimeRegistryAuthSessionStatus Status { get; set; }
    }
}
