using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed partial class RuntimeAppWebRpcClient : IAppWorkerExecutionObservationClient
{
    public async ValueTask<PackageWorkerExecutionReference> CaptureWorkerExecutionAsync(
        SunderRpcProviderSnapshot provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var response = await ReadRuntimeAsync(management.CaptureAppWorkerExecutionAsync(
            new RuntimeRpcAppExecutionCaptureRequest(sessionId, ToProtocol(provider)), cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfError(response.Error);
        return ReadWorkerExecutionReference(response.Reference);
    }

    public async ValueTask<PackageWorkerExecutionObservation> ObserveWorkerExecutionAsync(
        PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var response = await ReadRuntimeAsync(management.ObserveAppWorkerExecutionAsync(
            new RuntimeRpcAppExecutionObserveRequest(sessionId,
                new RuntimeWorkerExecutionReference(reference.RuntimeInstanceId, reference.Value)), cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfError(response.Error);
        var observation = response.Observation ?? throw InvalidWorkerObservation();
        var observedReference = ReadWorkerExecutionReference(observation.Reference);
        if (observedReference != reference) throw InvalidWorkerObservation();
        var state = observation.State switch
        {
            RuntimeWorkerExecutionState.Unknown => PackageWorkerExecutionState.Unknown,
            RuntimeWorkerExecutionState.CleanupPending => PackageWorkerExecutionState.CleanupPending,
            RuntimeWorkerExecutionState.CleanupConfirmed => PackageWorkerExecutionState.CleanupConfirmed,
            _ => throw InvalidWorkerObservation(),
        };
        if (state != PackageWorkerExecutionState.CleanupConfirmed && observation.ConfirmedAtUtc is not null)
            throw InvalidWorkerObservation();
        return new(observedReference, state) { ConfirmedAtUtc = observation.ConfirmedAtUtc };
    }

    private static RuntimeRpcProviderDescriptor ToProtocol(SunderRpcProviderSnapshot provider)
        => new(provider.PackageId, provider.PackageVersion, provider.ProviderId, provider.ContractId,
            provider.ContractVersion, provider.DescriptorSha256, provider.AbiSha256, provider.ActivationId,
            provider.ActivationEpoch, provider.SessionGeneration, provider.Endpoint.Value, provider.CatalogRevision,
            provider.State switch
            {
                SunderRpcProviderState.Active => RuntimeRpcProviderState.Active,
                SunderRpcProviderState.Inactive => RuntimeRpcProviderState.Inactive,
                SunderRpcProviderState.Faulted => RuntimeRpcProviderState.Faulted,
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            }, provider.FaultCode);

    private static PackageWorkerExecutionReference ReadWorkerExecutionReference(RuntimeWorkerExecutionReference? reference)
    {
        if (reference is null || reference.RuntimeInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(reference.Value) || reference.Value.Length > 256)
            throw InvalidWorkerObservation();
        return new(reference.RuntimeInstanceId, reference.Value);
    }

    private static SunderRpcException InvalidWorkerObservation() => Protocol(
        "rpc.worker-execution.invalid-observation", "The Runtime returned invalid worker execution evidence.");
}
