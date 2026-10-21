using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed partial class AppPackageManagedRpcClient : IAppWorkerExecutionObservationClient
{
    public async ValueTask<PackageWorkerExecutionReference> CaptureWorkerExecutionAsync(
        SunderRpcProviderSnapshot provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var client = CaptureWorkerObservationClient(cancellationToken);
        // Capture may have issued a reference before a lost response. Never replay
        // it through ReadMetadataAsync or follow a replacement from this operation.
        var result = await WorkerObservations(client).CaptureWorkerExecutionAsync(provider, cancellationToken).ConfigureAwait(false);
        RequireWorkerObservationClient(client, cancellationToken);
        return result;
    }

    public async ValueTask<PackageWorkerExecutionObservation> ObserveWorkerExecutionAsync(
        PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var client = CaptureWorkerObservationClient(cancellationToken);
        // A subsequent explicit observation may use the current authorized successor.
        // This operation itself retains its client and does not retarget a late reply.
        var result = await WorkerObservations(client).ObserveWorkerExecutionAsync(reference, cancellationToken).ConfigureAwait(false);
        RequireWorkerObservationClient(client, cancellationToken);
        return result;
    }

    private ISunderRpcClient CaptureWorkerObservationClient(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_retirements)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            publication.RequirePublished("worker-execution-observations");
            // Deliberately do not borrow _cleanupObservation. Public observations
            // have no disposal privilege, even while a later cleanup authority exists.
            return Current;
        }
    }

    private void RequireWorkerObservationClient(ISunderRpcClient client, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_retirements)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            publication.RequirePublished("worker-execution-observations");
            if (!ReferenceEquals(client, _current)) throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint,
                "rpc.caller-retired", "The App caller was replaced while observing worker execution evidence."));
        }
    }

    private static IAppWorkerExecutionObservationClient WorkerObservations(ISunderRpcClient client)
        => client as IAppWorkerExecutionObservationClient ?? throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable,
            "rpc.worker-execution.unsupported", "The connected App transport does not support worker execution observations."));
}
