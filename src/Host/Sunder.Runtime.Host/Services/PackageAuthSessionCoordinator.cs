using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;
using ProtocolCallbackState = Sunder.Runtime.Contracts.PackageCallbackSessionState;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageAuthSessionCoordinator(
    PackageSessionState sessionState,
    PackageCallbackSessionCoordinator callbacks,
    Action<string, PackageActivationIdentity, PackageFailureOrigin, Exception, string> handlePackageFault)
{
    public async Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
        PackageSessionLease lease,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = sessionState.GetLoadedPackage(lease, packageId);
        if (loadedPackage?.AuthHandler is null) return null;
        var activationIdentity = lease.GetPackageActivationIdentity(loadedPackage);
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        try
        {
            return ToProtocolAuthStatus(await loadedPackage.AuthHandler.GetStatusAsync(linked.Token));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            handlePackageFault(packageId, activationIdentity, PackageFailureOrigin.RuntimeAuthentication, exception, "read package auth status");
            return new PackageAuthStatusResponse(
                packageId,
                Sunder.Runtime.Contracts.PackageAuthStatusKind.Failed,
                exception.Message,
                CanAuthorize: false,
                CanDisconnect: false);
        }
    }

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        PackageSessionLease lease,
        string packageId,
        PackageCallbackServer callbackServer,
        CancellationToken cancellationToken = default)
    {
        var status = await callbacks.StartAsync(
            lease,
            packageId,
            PackageCallbackHandlerIds.Authentication,
            parameters: null,
            callbackServer,
            cancellationToken);
        return status is null ? null : new PackageAuthSessionStartResponse(
            status.PackageId,
            status.CallbackSessionId,
            PackageAuthFlowKind.Browser,
            status.LaunchUri ?? string.Empty,
            status.Message);
    }

    public PackageAuthSessionStatusResponse? GetPackageAuthSessionStatus(
        PackageSessionLease lease,
        string packageId,
        string authSessionId)
    {
        var status = callbacks.GetStatus(lease, packageId, authSessionId);
        return status is null ? null : new PackageAuthSessionStatusResponse(
            status.PackageId,
            status.CallbackSessionId,
            status.State switch
            {
                ProtocolCallbackState.Pending => PackageAuthSessionState.Pending,
                ProtocolCallbackState.Completed => PackageAuthSessionState.Connected,
                ProtocolCallbackState.Cancelled or ProtocolCallbackState.Expired => PackageAuthSessionState.Cancelled,
                _ => PackageAuthSessionState.Failed,
            },
            status.Message,
            status.LaunchUri);
    }

    public Task<bool> CompletePackageAuthSessionAsync(
        PackageSessionLease lease,
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
        => callbacks.CompleteAsync(lease, authSessionId, queryValues, cancellationToken);

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAsync(
        PackageSessionLease lease,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = sessionState.GetLoadedPackage(lease, packageId);
        if (loadedPackage?.AuthHandler is null) return null;
        var activationIdentity = lease.GetPackageActivationIdentity(loadedPackage);
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        try
        {
            return ToProtocolAuthStatus(await loadedPackage.AuthHandler.DisconnectAsync(linked.Token));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            handlePackageFault(packageId, activationIdentity, PackageFailureOrigin.RuntimeAuthentication, exception, "disconnect package authorization");
            return new PackageAuthStatusResponse(
                packageId,
                Sunder.Runtime.Contracts.PackageAuthStatusKind.Failed,
                exception.Message,
                CanAuthorize: false,
                CanDisconnect: false);
        }
    }
}
