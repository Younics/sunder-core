using System.Net;
using System.Net.Sockets;
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
        var authorizationOrigin = RegistryOrigin.NormalizeAuthorizationOrigin(request.AuthorizationOrigin, registryOrigin);
        lock (_syncRoot)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("Registry authorization coordinator is stopping.");
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
            var session = new AuthSession(sessionId, registryOrigin, state, verifier, listener, expiresAtUtc);
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
        var credential = await _credentialStore.GetAsync(registryOrigin, cancellationToken);
        if (credential is null)
        {
            return SignedOut(registryOrigin);
        }

        if (credential.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            await _credentialStore.DeleteAsync(registryOrigin, cancellationToken);
            return SignedOut(registryOrigin, "Registry credential expired.");
        }

        using var response = await _apiClient.SendProfileRequestAsync(registryOrigin, credential.AccessToken, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await _credentialStore.DeleteAsync(registryOrigin, cancellationToken);
            return SignedOut(registryOrigin, "Registry credential is no longer valid.");
        }

        var updated = await _apiClient.ReadProfileAsync(credential, response, cancellationToken);
        await _credentialStore.SetAsync(registryOrigin, updated, cancellationToken);
        return SignedIn(registryOrigin, updated);
    }

    public async Task<RuntimeRegistryAuthStatus> LogoutAsync(string registryOriginValue, CancellationToken cancellationToken)
    {
        var registryOrigin = RegistryOrigin.Normalize(registryOriginValue);
        await _credentialStore.DeleteAsync(registryOrigin, cancellationToken);
        return SignedOut(registryOrigin);
    }

    private async Task CompleteAsync(AuthSession session)
    {
        using var sessionTimeout = new CancellationTokenSource(_policy.SessionLifetime, _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, sessionTimeout.Token);
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

            var credential = await _apiClient.ExchangeAsync(session.RegistryOrigin, callback.Code, session.CodeVerifier, timeout.Token);
            await _credentialStore.SetAsync(session.RegistryOrigin, credential, timeout.Token);
            TrySetTerminalStatus(session, new RuntimeRegistryAuthSessionStatus(
                session.SessionId,
                session.RegistryOrigin.AbsoluteUri,
                RuntimeRegistryAuthSessionState.Succeeded,
                ToUser(credential),
                credential.ExpiresAtUtc,
                RuntimeRegistryErrorCode.None,
                "Signed in."));
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
            _sessions.Remove(sessionId);
        }
    }

    private static RuntimeRegistryAuthStatus SignedOut(Uri origin, string? message = null)
        => new(origin.AbsoluteUri, false, null, null, RuntimeRegistryErrorCode.None, message);

    private static RuntimeRegistryAuthStatus SignedIn(Uri origin, RegistryCredential credential)
        => new(origin.AbsoluteUri, true, ToUser(credential), credential.ExpiresAtUtc);

    private static RuntimeRegistryUser ToUser(RegistryCredential credential)
        => new(credential.UserId ?? string.Empty, credential.DisplayName, credential.Email, credential.Username, credential.AvatarUrl, credential.RequiresUsername);

    private sealed class AuthSession
    {
        public AuthSession(string sessionId, Uri registryOrigin, string state, string codeVerifier, TcpListener listener, DateTimeOffset expiresAtUtc)
        {
            SessionId = sessionId;
            RegistryOrigin = registryOrigin;
            State = state;
            CodeVerifier = codeVerifier;
            Listener = listener;
            ExpiresAtUtc = expiresAtUtc;
            Status = new RuntimeRegistryAuthSessionStatus(sessionId, registryOrigin.AbsoluteUri, RuntimeRegistryAuthSessionState.Pending, null, null, RuntimeRegistryErrorCode.None, null);
        }

        public string SessionId { get; }
        public Uri RegistryOrigin { get; }
        public string State { get; }
        public string CodeVerifier { get; }
        public TcpListener Listener { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public RuntimeRegistryAuthSessionStatus Status { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public Task CompletionTask { get; set; } = Task.CompletedTask;
    }
}
