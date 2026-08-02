using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryAuthCoordinator : IHostedService, IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, AuthSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeLogouts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly RegistryAuthApiClient _apiClient;
    private readonly RegistryCredentialStore _credentialStore;
    private readonly ILogger<RegistryAuthCoordinator> _logger;
    private readonly RuntimeAuthPolicyOptions _policy;
    private readonly TimeProvider _timeProvider;
    private Task _maintenanceTask = Task.CompletedTask;
    private bool _stopping;

    internal RegistryAuthCoordinator(
        IHttpClientFactory httpClientFactory,
        RegistryCredentialStore credentialStore,
        ILogger<RegistryAuthCoordinator> logger)
        : this(
            new RegistryHttpClient(httpClientFactory, new RuntimeTransportPolicyOptions(), TimeProvider.System),
            credentialStore,
            logger,
            new RuntimeAuthPolicyOptions(),
            TimeProvider.System)
    {
    }

    internal RegistryAuthCoordinator(
        IHttpClientFactory httpClientFactory,
        RegistryCredentialStore credentialStore,
        ILogger<RegistryAuthCoordinator> logger,
        TimeSpan sessionLifetime,
        TimeSpan terminalRetention,
        int maxSessions)
        : this(
            new RegistryHttpClient(httpClientFactory, new RuntimeTransportPolicyOptions(), TimeProvider.System),
            credentialStore,
            logger,
            new RuntimeAuthPolicyOptions
            {
                SessionLifetime = sessionLifetime,
                TerminalSessionRetention = terminalRetention,
                MaxRegistrySessions = maxSessions,
            },
            TimeProvider.System)
    {
    }

    public RegistryAuthCoordinator(
        RegistryHttpClient registryClient,
        RegistryCredentialStore credentialStore,
        ILogger<RegistryAuthCoordinator> logger,
        RuntimeAuthPolicyOptions policy,
        TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(policy.SessionLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.TerminalSessionRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxRegistrySessions, 1);
        _apiClient = new RegistryAuthApiClient(registryClient);
        _credentialStore = credentialStore;
        _logger = logger;
        _policy = policy;
        _timeProvider = timeProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("Registry authorization coordinator is stopping.");
            }
            if (_maintenanceTask.IsCompleted)
            {
                _maintenanceTask = MaintainSessionsAsync(_shutdown.Token);
            }
        }
        return Task.CompletedTask;
    }

    public RuntimeRegistryAuthStartResponse Start(RuntimeRegistryAuthStartRequest request)
    {
        var registryOrigin = RegistryOrigin.Normalize(request.RegistryOrigin);
        var registryOriginKey = RegistryOrigin.Key(registryOrigin);
        var authorizationOrigin = RegistryOrigin.NormalizeAuthorizationOrigin(request.AuthorizationOrigin, registryOrigin);
        lock (_syncRoot)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("Registry authorization coordinator is stopping.");
            }
            if (_activeLogouts.ContainsKey(registryOriginKey))
            {
                throw new InvalidOperationException("Registry authorization cannot start while logout is in progress for this Registry.");
            }

            SweepExpiredLocked(_timeProvider.GetUtcNow());
            if (_sessions.Count >= _policy.MaxRegistrySessions)
            {
                throw new InvalidOperationException("Too many Registry authorization sessions are active. Wait for an existing session to finish and retry.");
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var sessionId = Guid.NewGuid().ToString("N");
            var state = RegistryAuthCallbackProtocol.CreateRandomToken(24);
            var verifier = RegistryAuthCallbackProtocol.CreateRandomToken(32);
            var challenge = RegistryAuthCallbackProtocol.CreateChallenge(verifier);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var callback = new Uri($"http://127.0.0.1:{port}/callback");
            var expiresAtUtc = _timeProvider.GetUtcNow() + _policy.SessionLifetime;
            var launchUrl = RegistryAuthCallbackProtocol.BuildAuthorizeUri(authorizationOrigin, callback, state, challenge, request.DisplayName);
            var session = new AuthSession(sessionId, registryOrigin, registryOriginKey, state, verifier, listener, expiresAtUtc);
            _sessions.Add(sessionId, session);
            session.CompletionTask = CompleteAsync(session);
            return new RuntimeRegistryAuthStartResponse(sessionId, registryOrigin.AbsoluteUri, launchUrl.AbsoluteUri, expiresAtUtc);
        }
    }

    public RuntimeRegistryAuthSessionStatus? GetSession(string sessionId)
    {
        lock (_syncRoot)
        {
            SweepExpiredLocked(_timeProvider.GetUtcNow());
            return _sessions.TryGetValue(sessionId, out var session) ? session.Status : null;
        }
    }

    internal int SessionCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessions.Count;
            }
        }
    }

    internal void SweepExpired(DateTimeOffset now)
    {
        lock (_syncRoot)
        {
            SweepExpiredLocked(now);
        }
    }

    public async Task<RuntimeRegistryAuthStatus> GetStatusAsync(string registryOriginValue, CancellationToken cancellationToken)
    {
        var registryOrigin = RegistryOrigin.Normalize(registryOriginValue);
        var snapshot = await _credentialStore.GetSnapshotAsync(registryOrigin, cancellationToken);
        if (snapshot is null)
        {
            return SignedOut(registryOrigin);
        }

        var credential = snapshot.Credential;
        if (credential.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return await _credentialStore.TryDeleteAsync(registryOrigin, snapshot, cancellationToken)
                ? SignedOut(registryOrigin, "Registry credential expired.")
                : await ReadCurrentStatusAsync(registryOrigin, cancellationToken);
        }

        using var response = await _apiClient.SendProfileRequestAsync(registryOrigin, credential.AccessToken, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return await _credentialStore.TryDeleteAsync(registryOrigin, snapshot, cancellationToken)
                ? SignedOut(registryOrigin, "Registry credential is no longer valid.")
                : await ReadCurrentStatusAsync(registryOrigin, cancellationToken);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new RuntimeRegistryAuthStatus(
                registryOrigin.AbsoluteUri,
                true,
                ToUser(credential),
                credential.ExpiresAtUtc,
                RuntimeRegistryErrorCode.Forbidden,
                "Registry credential is valid but cannot access the current-user endpoint.");
        }

        var updated = await _apiClient.ReadProfileAsync(credential, response, cancellationToken);
        return await _credentialStore.TryUpdateAsync(registryOrigin, snapshot, updated, cancellationToken)
            ? SignedIn(registryOrigin, updated)
            : await ReadCurrentStatusAsync(registryOrigin, cancellationToken);
    }

    public async Task<RuntimeRegistryAuthStatus> LogoutAsync(string registryOriginValue, CancellationToken cancellationToken)
    {
        var registryOrigin = RegistryOrigin.Normalize(registryOriginValue);
        var registryOriginKey = RegistryOrigin.Key(registryOrigin);
        AuthSession[] invalidatedSessions;
        lock (_syncRoot)
        {
            _activeLogouts[registryOriginKey] = _activeLogouts.GetValueOrDefault(registryOriginKey) + 1;
            var now = _timeProvider.GetUtcNow();
            invalidatedSessions = _sessions.Values
                .Where(session => session.RegistryOriginKey == registryOriginKey
                                  && session.Status.State == RuntimeRegistryAuthSessionState.Pending)
                .ToArray();
            foreach (var session in invalidatedSessions)
            {
                InvalidateForLogoutLocked(session, now);
            }
        }

        ExceptionDispatchInfo? failure = null;
        try
        {
            try
            {
                var snapshot = await _credentialStore.GetSnapshotAsync(registryOrigin, cancellationToken);
                if (snapshot is not null)
                {
                    using var response = await _apiClient.RevokeCurrentTokenAsync(
                        registryOrigin,
                        snapshot.Credential.AccessToken,
                        cancellationToken);
                    if (response.StatusCode != HttpStatusCode.Unauthorized)
                    {
                        await _apiClient.EnsureSuccessAsync(response, cancellationToken);
                    }
                }
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }

            try
            {
                await Task.WhenAll(invalidatedSessions.Select(session => session.CompletionTask));
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }

            try
            {
                await _credentialStore.DeleteAsync(registryOrigin, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_activeLogouts[registryOriginKey] == 1)
                {
                    _activeLogouts.Remove(registryOriginKey);
                }
                else
                {
                    _activeLogouts[registryOriginKey]--;
                }
            }
        }

        failure?.Throw();
        return SignedOut(registryOrigin);
    }

    private async Task CompleteAsync(AuthSession session)
    {
        using var sessionTimeout = new CancellationTokenSource(_policy.SessionLifetime, _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            sessionTimeout.Token,
            session.Cancellation.Token);
        RegistryCredential? uncommittedCredential = null;
        try
        {
            using var client = await session.Listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var callback = await RegistryAuthCallbackProtocol.ReadAsync(stream, _policy, timeout.Token);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(callback.State),
                    Encoding.UTF8.GetBytes(session.State)))
            {
                throw new InvalidOperationException("Registry authorization state did not match.");
            }

            timeout.Token.ThrowIfCancellationRequested();
            if (!IsPending(session)) return;

            // Once the Registry starts exchanging a code, let the bounded HTTP operation finish so
            // an invalidated session can revoke any token the Registry issued.
            var credential = await _apiClient.ExchangeAsync(
                session.RegistryOrigin,
                callback.Code,
                session.CodeVerifier,
                CancellationToken.None);
            uncommittedCredential = credential;
            timeout.Token.ThrowIfCancellationRequested();
            if (!IsPending(session)) return;

            await _credentialStore.SetAsync(session.RegistryOrigin, credential, CancellationToken.None);
            timeout.Token.ThrowIfCancellationRequested();
            if (!TrySetTerminalStatus(session, new RuntimeRegistryAuthSessionStatus(
                session.SessionId,
                session.RegistryOrigin.AbsoluteUri,
                RuntimeRegistryAuthSessionState.Succeeded,
                ToUser(credential),
                credential.ExpiresAtUtc,
                RuntimeRegistryErrorCode.None,
                "Signed in."))) return;
            uncommittedCredential = null;
            await RegistryAuthCallbackProtocol.WriteResponseAsync(stream, true, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TrySetTerminalStatus(session, session.Status with
            {
                State = RuntimeRegistryAuthSessionState.Expired,
                ErrorCode = RuntimeRegistryErrorCode.Cancelled,
                Message = "Registry authorization timed out.",
            });
        }
        catch (Exception ex)
        {
            if (TrySetTerminalStatus(session, session.Status with
            {
                State = RuntimeRegistryAuthSessionState.Failed,
                ErrorCode = RuntimeRegistryErrorCode.AuthenticationRequired,
                Message = ex.Message,
            }))
            {
                _logger.LogWarning("Registry authorization session {SessionId} failed: {ErrorType}", session.SessionId, ex.GetType().Name);
            }
        }
        finally
        {
            if (uncommittedCredential is not null)
            {
                await RevokeUncommittedCredentialAsync(session, uncommittedCredential);
            }
            session.Listener.Stop();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task maintenanceTask;
        AuthSession[] sessions;
        lock (_syncRoot)
        {
            if (!_stopping)
            {
                _stopping = true;
                _shutdown.Cancel();
            }
            maintenanceTask = _maintenanceTask;
            sessions = _sessions.Values.ToArray();
            foreach (var session in sessions)
            {
                session.Listener.Stop();
            }
        }

        await maintenanceTask.WaitAsync(cancellationToken);
        await Task.WhenAll(sessions.Select(session => session.CompletionTask)).WaitAsync(cancellationToken);
        lock (_syncRoot)
        {
            foreach (var session in sessions)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _shutdown.Dispose();
    }

    private async Task MaintainSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_policy.SessionSweepInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                SweepExpired(_timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool TrySetTerminalStatus(AuthSession session, RuntimeRegistryAuthSessionStatus status)
    {
        lock (_syncRoot)
        {
            if (session.Status.State != RuntimeRegistryAuthSessionState.Pending)
            {
                return false;
            }
            session.Status = status;
            session.CompletedAtUtc = _timeProvider.GetUtcNow();
            return true;
        }
    }

    private bool IsPending(AuthSession session)
    {
        lock (_syncRoot)
        {
            return session.Status.State == RuntimeRegistryAuthSessionState.Pending;
        }
    }

    private static void InvalidateForLogoutLocked(AuthSession session, DateTimeOffset now)
    {
        session.Status = session.Status with
        {
            State = RuntimeRegistryAuthSessionState.Failed,
            ErrorCode = RuntimeRegistryErrorCode.Cancelled,
            Message = "Registry authorization was cancelled by logout.",
        };
        session.CompletedAtUtc = now;
        session.Cancellation.Cancel();
        session.Listener.Stop();
    }

    private async Task RevokeUncommittedCredentialAsync(AuthSession session, RegistryCredential credential)
    {
        try
        {
            using var response = await _apiClient.RevokeCurrentTokenAsync(
                session.RegistryOrigin,
                credential.AccessToken,
                CancellationToken.None);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                await _apiClient.EnsureSuccessAsync(response, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Revoking the credential from invalidated Registry authorization session {SessionId} failed: {ErrorType}",
                session.SessionId,
                exception.GetType().Name);
        }
    }

    private void SweepExpiredLocked(DateTimeOffset now)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Status.State == RuntimeRegistryAuthSessionState.Pending && session.ExpiresAtUtc <= now)
            {
                session.Status = session.Status with
                {
                    State = RuntimeRegistryAuthSessionState.Expired,
                    ErrorCode = RuntimeRegistryErrorCode.Cancelled,
                    Message = "Registry authorization timed out.",
                };
                session.CompletedAtUtc = now;
                session.Listener.Stop();
            }
        }

        foreach (var sessionId in _sessions
                     .Where(pair => pair.Value.CompletedAtUtc is { } completedAtUtc
                                    && completedAtUtc + _policy.TerminalSessionRetention <= now
                                    && pair.Value.CompletionTask.IsCompleted)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (_sessions.Remove(sessionId, out var session))
            {
                session.Dispose();
            }
        }
    }

    private static RuntimeRegistryAuthStatus SignedOut(Uri origin, string? message = null)
        => new(origin.AbsoluteUri, false, null, null, RuntimeRegistryErrorCode.None, message);

    private static RuntimeRegistryAuthStatus SignedIn(Uri origin, RegistryCredential credential)
        => new(origin.AbsoluteUri, true, ToUser(credential), credential.ExpiresAtUtc);

    private async Task<RuntimeRegistryAuthStatus> ReadCurrentStatusAsync(
        Uri origin,
        CancellationToken cancellationToken)
    {
        var current = await _credentialStore.GetAsync(origin, cancellationToken);
        return current is null || current.ExpiresAtUtc <= _timeProvider.GetUtcNow()
            ? SignedOut(origin)
            : SignedIn(origin, current);
    }

    private static RuntimeRegistryUser ToUser(RegistryCredential credential)
        => new(credential.UserId ?? string.Empty, credential.DisplayName, credential.Email, credential.Username, credential.AvatarUrl, credential.RequiresUsername);

    private sealed class AuthSession : IDisposable
    {
        public AuthSession(
            string sessionId,
            Uri registryOrigin,
            string registryOriginKey,
            string state,
            string codeVerifier,
            TcpListener listener,
            DateTimeOffset expiresAtUtc)
        {
            SessionId = sessionId;
            RegistryOrigin = registryOrigin;
            RegistryOriginKey = registryOriginKey;
            State = state;
            CodeVerifier = codeVerifier;
            Listener = listener;
            ExpiresAtUtc = expiresAtUtc;
            Status = new RuntimeRegistryAuthSessionStatus(sessionId, registryOrigin.AbsoluteUri, RuntimeRegistryAuthSessionState.Pending, null, null, RuntimeRegistryErrorCode.None, null);
        }

        public string SessionId { get; }
        public Uri RegistryOrigin { get; }
        public string RegistryOriginKey { get; }
        public string State { get; }
        public string CodeVerifier { get; }
        public TcpListener Listener { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public RuntimeRegistryAuthSessionStatus Status { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public Task CompletionTask { get; set; } = Task.CompletedTask;
        public CancellationTokenSource Cancellation { get; } = new();

        public void Dispose() => Cancellation.Dispose();
    }
}
