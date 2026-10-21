using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.App.Services;

// One facade per App activation. Publication is the only public admission authority;
// observing a retired provider does not grant work to a revoked App activation.
internal sealed class AppPackageWorkerExecutionObservations(
    ISunderRpcClient rpcClient,
    AppPackageGenerationPublication publication) : IPackageWorkerExecutionObservations
{
    public Task<PackageWorkerExecutionReference> CaptureAsync<TService>(ServiceProvider<TService> provider,
        CancellationToken cancellationToken = default) where TService : class
        => RunAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(provider);
            var transport = Transport;
            if (provider.State != ServiceProviderState.Active) throw Stale();

            // ServiceProvider intentionally exposes no endpoint. Resolve a complete,
            // currently visible Host snapshot by every public exact identity field.
            // Metadata is only a selector; Runtime must authorize and validate again.
            var catalog = await rpcClient.DiscoverAsync(provider.ContractId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var matches = catalog.Providers.Where(current => Matches(provider, current)).Take(2).ToArray();
            if (matches.Length == 0) throw Stale();
            if (matches.Length != 1) throw new SunderRpcException(new(SunderRpcErrorKind.Protocol,
                "rpc.worker-execution.ambiguous-identity", "The Runtime returned duplicate exact service provider identities."));
            RequireAccess(token);
            return await transport.CaptureWorkerExecutionAsync(matches[0], token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<PackageWorkerExecutionObservation> ObserveAsync(PackageWorkerExecutionReference reference,
        CancellationToken cancellationToken = default)
        => RunAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(reference);
            // Do not rediscover a provider or route to reference.RuntimeInstanceId.
            // The current authorized session queries retained evidence for that origin.
            var observation = await Transport.ObserveWorkerExecutionAsync(reference, token).ConfigureAwait(false);
            if (observation.Reference != reference) throw new SunderRpcException(new(SunderRpcErrorKind.Protocol,
                "rpc.worker-execution.reference-mismatch", "The Runtime returned evidence for a different worker execution."));
            return observation;
        }, cancellationToken);

    private IAppWorkerExecutionObservationClient Transport => rpcClient as IAppWorkerExecutionObservationClient
        ?? throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable,
            "rpc.worker-execution.unsupported", "The connected App transport does not support worker execution observations."));

    private static bool Matches<TService>(ServiceProvider<TService> expected, SunderRpcProviderSnapshot current)
        where TService : class
        => current.State == SunderRpcProviderState.Active
            && current.PackageId == expected.PackageId && current.PackageVersion == expected.PackageVersion
            && current.ProviderId == expected.ProviderId && current.ContractId == expected.ContractId
            && current.ContractVersion == expected.ContractVersion && current.DescriptorSha256 == expected.DescriptorSha256
            && current.AbiSha256 == expected.AbiSha256 && current.ActivationId == expected.ActivationId
            && current.ActivationEpoch == expected.ActivationEpoch && current.SessionGeneration == expected.SessionGeneration;

    private void RequireAccess(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(publication.IsRevoked, this);
        publication.RequirePublished("worker-execution-observations");
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
    {
        RequireAccess(caller);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, publication.RevocationToken);
        try
        {
            RequireAccess(linked.Token);
            var result = await operation(linked.Token).ConfigureAwait(false);
            RequireAccess(linked.Token);
            return result;
        }
        catch (OperationCanceledException error) when (error.CancellationToken == linked.Token && linked.IsCancellationRequested)
        {
            if (caller.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, caller);
            throw new ObjectDisposedException(nameof(IPackageWorkerExecutionObservations), "The owning App activation was retired.");
        }
    }

    private static SunderRpcException Stale() => new(new(SunderRpcErrorKind.StaleEndpoint,
        "rpc.stale-endpoint", "The selected exact service provider is not active or is no longer visible."));
}
