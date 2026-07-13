using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageAuthAccessService(RuntimeSessionOwner sessions)
{
    public async Task<PackageAuthStatusResponse?> GetStatusAsync(string packageId, CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        return await sessions.Auth.GetPackageAuthStatusAsync(lease, packageId, cancellationToken);
    }

    public async Task<PackageAuthSessionStartResponse?> StartAsync(
        string packageId,
        PackageCallbackServer callbackServer,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        return await sessions.Auth.StartPackageAuthAsync(lease, packageId, callbackServer, cancellationToken);
    }

    public PackageAuthSessionStatusResponse? GetSessionStatus(string packageId, string authSessionId)
    {
        using var lease = sessions.State.AcquireLease();
        return sessions.Auth.GetPackageAuthSessionStatus(lease, packageId, authSessionId);
    }

    public async Task<bool> CompleteAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        return await sessions.Auth.CompletePackageAuthSessionAsync(lease, authSessionId, queryValues, cancellationToken);
    }

    public async Task<PackageAuthStatusResponse?> DisconnectAsync(string packageId, CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        return await sessions.Auth.DisconnectPackageAsync(lease, packageId, cancellationToken);
    }
}
