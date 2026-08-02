using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageCallbackAccessService(RuntimeSessionOwner sessions)
{
    public async Task<PackageCallbackSessionResponse?> StartAsync(
        string packageId,
        string callbackHandlerId,
        PackageCallbackSessionStartRequest request,
        PackageCallbackServer callbackServer,
        CancellationToken cancellationToken = default)
    {
        using var lease = sessions.State.AcquireLease();
        return await sessions.Callbacks.StartAsync(
            lease,
            packageId,
            callbackHandlerId,
            request.Parameters,
            callbackServer,
            cancellationToken);
    }

    public PackageCallbackSessionResponse? GetStatus(string packageId, string callbackSessionId)
    {
        using var lease = sessions.State.AcquireLease();
        return sessions.Callbacks.GetStatus(lease, packageId, callbackSessionId);
    }

    public bool Cancel(string packageId, string callbackSessionId)
    {
        using var lease = sessions.State.AcquireLease();
        return sessions.Callbacks.Cancel(lease, packageId, callbackSessionId);
    }
}
