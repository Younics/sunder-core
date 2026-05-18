using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using static Sunder.Runtime.Host.Services.DevPackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageAuthSessionCoordinator(
    Func<string, ActiveLoadedPackage?> getLoadedPackage,
    Action<string, PackageFailureOrigin, Exception, string> handlePackageFault)
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, ActivePackageAuthSession> _authSessions = new(StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        lock (_syncRoot)
        {
            _authSessions.Clear();
        }
    }

    public void RemovePackageSessions(string packageId)
    {
        lock (_syncRoot)
        {
            foreach (var authSessionId in _authSessions
                         .Where(pair => string.Equals(pair.Value.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _authSessions.Remove(authSessionId);
            }
        }
    }

    public async Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = getLoadedPackage(packageId);
        var callbackHandler = loadedPackage is null ? null : ResolveAuthCallbackHandler(loadedPackage);
        if (loadedPackage?.AuthHandler is null || callbackHandler is null)
        {
            return null;
        }

        try
        {
            return ToProtocolAuthStatus(await loadedPackage.AuthHandler.GetStatusAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFaultedAuthStatus(packageId, PackageFailureOrigin.RuntimeAuthentication, ex, "read package auth status");
        }
    }

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        string packageId,
        PackageAuthCallbackServer packageAuthCallbackServer,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = getLoadedPackage(packageId);
        var callbackHandler = loadedPackage is null ? null : ResolveAuthCallbackHandler(loadedPackage);
        if (loadedPackage?.AuthHandler is null || callbackHandler is null)
        {
            return null;
        }

        var existingSession = GetLatestPendingAuthSession(packageId);
        if (existingSession is not null)
        {
            return ToProtocolAuthSessionStart(existingSession.Status);
        }

        var authSessionId = Guid.NewGuid().ToString("N");
        try
        {
            packageAuthCallbackServer.EnsureStarted();
        }
        catch (Exception ex)
        {
            return new PackageAuthSessionStartResponse(
                packageId,
                authSessionId,
                Sunder.Protocol.PackageAuthFlowKind.Browser,
                string.Empty,
                ex.Message);
        }

        try
        {
            var callbackUri = packageAuthCallbackServer.CallbackUri;
            var result = await callbackHandler.StartCallbackAsync(
                new PackageCallbackStartContext(authSessionId, callbackUri, PackageCallbackHandlerIds.Authentication),
                cancellationToken);

            if (result is null)
            {
                return null;
            }

            var sessionStatus = new PackageAuthSessionStatus(
                packageId,
                authSessionId,
                Sunder.Sdk.Authentication.PackageAuthSessionState.Pending,
                result.Message,
                result.LaunchUrl);

            packageAuthCallbackServer.RegisterHandler(
                authSessionId,
                (queryValues, ct) => CompletePackageAuthSessionAsync(authSessionId, queryValues, ct));

            lock (_syncRoot)
            {
                _authSessions[authSessionId] = new ActivePackageAuthSession(packageId, loadedPackage.AuthHandler, callbackHandler, sessionStatus);
            }

            return ToProtocolAuthSessionStart(sessionStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            handlePackageFault(packageId, PackageFailureOrigin.RuntimeAuthentication, ex, "start package authorization");
            return new PackageAuthSessionStartResponse(
                packageId,
                authSessionId,
                Sunder.Protocol.PackageAuthFlowKind.Browser,
                string.Empty,
                ex.Message);
        }
    }

    public PackageAuthSessionStatusResponse? GetPackageAuthSessionStatus(string packageId, string authSessionId)
    {
        lock (_syncRoot)
        {
            return _authSessions.TryGetValue(authSessionId, out var session) && string.Equals(session.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                ? ToProtocolAuthSessionStatus(session.Status)
                : null;
        }
    }

    public async Task<bool> CompletePackageAuthSessionAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
    {
        ActivePackageAuthSession? session;
        lock (_syncRoot)
        {
            session = _authSessions.TryGetValue(authSessionId, out var foundSession) ? foundSession : null;
        }

        if (session is null)
        {
            return false;
        }

        PackageAuthStatus finalStatus;
        try
        {
            var completion = await session.CallbackHandler.CompleteCallbackAsync(
                new PackageCallbackCompletionContext(authSessionId, queryValues),
                cancellationToken);
            finalStatus = completion.State == PackageCallbackCompletionState.Completed
                ? await session.AuthHandler.GetStatusAsync(cancellationToken)
                : new PackageAuthStatus(
                    session.PackageId,
                    Sunder.Sdk.Authentication.PackageAuthStatusKind.Failed,
                    completion.Message,
                    CanAuthorize: true,
                    CanDisconnect: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            handlePackageFault(session.PackageId, PackageFailureOrigin.RuntimeAuthentication, ex, "complete package authorization");
            finalStatus = new PackageAuthStatus(
                session.PackageId,
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Failed,
                ex.Message,
                CanAuthorize: false,
                CanDisconnect: false);
        }

        lock (_syncRoot)
        {
            _authSessions[authSessionId] = session with
            {
                Status = new PackageAuthSessionStatus(
                    session.PackageId,
                    authSessionId,
                    finalStatus.Status switch
                    {
                        Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected => Sunder.Sdk.Authentication.PackageAuthSessionState.Connected,
                        Sunder.Sdk.Authentication.PackageAuthStatusKind.Failed => Sunder.Sdk.Authentication.PackageAuthSessionState.Failed,
                        _ => Sunder.Sdk.Authentication.PackageAuthSessionState.Failed,
                    },
                    finalStatus.Message,
                    session.Status.LaunchUrl)
            };
        }

        return true;
    }

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = getLoadedPackage(packageId);
        if (loadedPackage?.AuthHandler is null)
        {
            return null;
        }

        try
        {
            return ToProtocolAuthStatus(await loadedPackage.AuthHandler.DisconnectAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFaultedAuthStatus(packageId, PackageFailureOrigin.RuntimeAuthentication, ex, "disconnect package authorization");
        }
    }

    private ActivePackageAuthSession? GetLatestPendingAuthSession(string packageId)
    {
        lock (_syncRoot)
        {
            return _authSessions.Values.LastOrDefault(session =>
                string.Equals(session.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                && session.Status.State == Sunder.Sdk.Authentication.PackageAuthSessionState.Pending);
        }
    }

    private PackageAuthStatusResponse CreateFaultedAuthStatus(
        string packageId,
        PackageFailureOrigin origin,
        Exception exception,
        string action)
    {
        handlePackageFault(packageId, origin, exception, action);
        return new PackageAuthStatusResponse(
            packageId,
            Sunder.Protocol.PackageAuthStatusKind.Failed,
            exception.Message,
            CanAuthorize: false,
            CanDisconnect: false);
    }

    private sealed record ActivePackageAuthSession(
        string PackageId,
        IPackageAuthHandler AuthHandler,
        IPackageCallbackHandler CallbackHandler,
        PackageAuthSessionStatus Status);

    private static IPackageCallbackHandler? ResolveAuthCallbackHandler(ActiveLoadedPackage loadedPackage)
        => loadedPackage.GetCallbackHandler(PackageCallbackHandlerIds.Authentication)
           ?? (loadedPackage.AuthHandler as IPackageCallbackHandler);
}
