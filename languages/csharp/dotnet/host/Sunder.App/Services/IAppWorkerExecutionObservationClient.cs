using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

// Optional App transport only. Do not extend ISunderRpcClient or Worker V1 messages.
internal interface IAppWorkerExecutionObservationClient
{
    ValueTask<PackageWorkerExecutionReference> CaptureWorkerExecutionAsync(
        SunderRpcProviderSnapshot provider,
        CancellationToken cancellationToken = default);

    ValueTask<PackageWorkerExecutionObservation> ObserveWorkerExecutionAsync(
        PackageWorkerExecutionReference reference,
        CancellationToken cancellationToken = default);
}
