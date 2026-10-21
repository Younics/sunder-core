using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ExecutionRuntimeStore
{
    internal async Task<RuntimeWorkerExecutionReference> CaptureWorkerExecutionAsync(
        RuntimeExecutionReservationIdentity reservation, string ownerPackageId, CancellationToken token)
    {
        using var recovery = await EnterObservationReadAsync(token).ConfigureAwait(false);
        return await _leases.CaptureExecutionAsync(reservation, ownerPackageId, token).ConfigureAwait(false);
    }

    internal async Task<RuntimeWorkerExecutionObservation> ObserveWorkerExecutionAsync(
        RuntimeWorkerExecutionReference reference, string ownerPackageId, CancellationToken token)
    {
        using var recovery = await EnterObservationReadAsync(token).ConfigureAwait(false);
        return await _leases.ObserveExecutionAsync(reference, ownerPackageId, token).ConfigureAwait(false);
    }

    private async Task<RuntimeFileLock> EnterObservationReadAsync(CancellationToken token)
    {
        // Unlike acquisition, an observation must NEVER initialize the store: initialization
        // can recover/terminate orphan processes. Bootstrap/owning lifecycle does that work.
        if (!_initialized) throw new RuntimeUnavailableException("Worker execution observations are not ready.");
        var recovery = await RuntimeFileLock.AcquireSharedAsync(_layout.RecoveryFencePath, token).ConfigureAwait(false);
        try
        {
            if (!_initialized || HasPendingRecoveryEvidence())
                throw new RuntimeUnavailableException("Worker execution observation recovery is pending.");
            token.ThrowIfCancellationRequested();
            return recovery;
        }
        catch { recovery.Dispose(); throw; }
    }
}
