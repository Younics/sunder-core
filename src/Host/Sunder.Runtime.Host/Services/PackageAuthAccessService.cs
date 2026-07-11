using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageAuthAccessService(RuntimeSessionOwner sessions)
{
    public Task<PackageAuthStatusResponse?> GetStatusAsync(string packageId, CancellationToken cancellationToken = default)
        => sessions.Auth.GetPackageAuthStatusAsync(packageId, cancellationToken);

    public Task<PackageAuthSessionStartResponse?> StartAsync(
        string packageId,
        PackageAuthCallbackServer callbackServer,
        CancellationToken cancellationToken = default)
        => sessions.Auth.StartPackageAuthAsync(packageId, callbackServer, cancellationToken);

    public PackageAuthSessionStatusResponse? GetSessionStatus(string packageId, string authSessionId)
        => sessions.Auth.GetPackageAuthSessionStatus(packageId, authSessionId);

    public Task<bool> CompleteAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
        => sessions.Auth.CompletePackageAuthSessionAsync(authSessionId, queryValues, cancellationToken);

    public Task<PackageAuthStatusResponse?> DisconnectAsync(string packageId, CancellationToken cancellationToken = default)
        => sessions.Auth.DisconnectPackageAsync(packageId, cancellationToken);
}
