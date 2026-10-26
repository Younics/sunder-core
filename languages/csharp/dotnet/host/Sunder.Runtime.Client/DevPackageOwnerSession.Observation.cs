using System.Net;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class DevPackageOwnerSession
{
    public async Task<DevPackageOwnerLeaseResponse?> ObserveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            DevPackageOwnerLeaseResponse? lease;
            lock (_syncRoot) lease = _lease;
            if (lease is null) return null;
            using var client = _clients();
            var handshake = await client.GetRuntimeHandshakeAsync(cancellationToken).ConfigureAwait(false);
            // An observation must not erase an unresolved operation or acquire its
            // desired folders against a replacement Runtime/owner.
            if (handshake.RuntimeInstanceId != lease.RuntimeInstanceId) return null;
            try
            {
                var observed = await client.HeartbeatDevPackageOwnerAsync(OwnerId,
                    new(lease.RuntimeInstanceId, OwnerToken), cancellationToken).ConfigureAwait(false);
                ReconcileHeartbeat(observed, lease.RuntimeInstanceId);
                return observed;
            }
            catch (RuntimeClientException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
            {
                return null;
            }
        }
        finally { _mutationGate.Release(); }
    }
}
